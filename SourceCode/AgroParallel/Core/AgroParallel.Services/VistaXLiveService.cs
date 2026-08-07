// ============================================================================
// VistaXLiveService.cs — implementación.
//
// Estrategia:
//   - Suscribe el filtro vistax/+/telemetria (y el configurado) al MQTT
//     compartido del NodoRegistry, vía MqttLiveServiceBase<Reading>.
//   - Parsea payload { uid, sensores:[{cable, valor, raw}] }.
//   - Mantiene Dictionary<(uid,cable), LastReading> con timestamp + valor.
//     La clave compuesta "uid#cable" es compatible con _readings heredado.
//   - Cada tick (interval = cfg.UiUpdateIntervalMs), recompone el snapshot
//     mapeando (uid, cable) → SensorConfig.Bajada/Tren y proyectando a Trenes.
//   - SPM = (valor_actual - valor_previo) / Δt en segundos * 60. Se clampa
//     si no hay datos suficientes o el sensor está en timeout.
//   - Estado por surco:
//       * SeccionCortada (placeholder, requiere bridge PilotX) → no-data
//       * Sin lectura reciente (timeout)                    → no-data
//       * SPM < (1-tolerancia)*objetivo / 60                → bad
//       * SPM > (1+tolerancia)*objetivo / 60                → warn
//       * caso contrario                                    → ok
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.VistaX;

namespace AgroParallel.Services
{
    public sealed class VistaXLiveService : MqttLiveServiceBase<VistaXLiveService.Reading>,
        IVistaXLiveService, IDisposable
    {
        private readonly IVistaXConfigService _cfgSvc;
        // Fuente única de verdad de la geometría física (ancho, distancia entre
        // surcos, trenes, torres). Si está presente, gana sobre la copia legacy
        // del vistaX implemento — así el operario edita la geometría en un solo
        // lugar (herramienta.html) y el overlay VistaX la refleja sin tener que
        // reguardar la pestaña VistaX. Si es null, se cae al comportamiento
        // legacy (geometría desde el vistaX implemento).
        private readonly IImplementoService _impCentral;
        private readonly IQuantiXConfigService _quantixCfg;

        // Objetivo dinámico por surco (sem/m que el motor QuantiX tiene mandado
        // AHORA). Cache corto: GetSnapshot corre a UI-rate y la cuenta recorre
        // config de motores + registry + implemento.
        private System.Collections.Generic.Dictionary<int, double> _objDinPorSurco;
        // El mismo objetivo pero en sem/m (sin velocidad): es lo que se MUESTRA
        // (encabezado del tren, ficha del surco). El de arriba está en sem/MIN
        // porque compara contra el Spm del sensor; mostrar ese obligaba a estar
        // en movimiento para saber el objetivo.
        private System.Collections.Generic.Dictionary<int, double> _objDinSemM;
        private DateTime _objDinStamp;
        // Opcional: si está presente, se consultan los bounds DropMin/DropMax del
        // insumo activo para definir "bajo"/"exceso" por surco. Si es null o el
        // insumo activo no tiene bounds seteados, se cae al cálculo legacy
        // (DensidadObjetivo * (1 ± ToleranciaDesvio)).
        private readonly IInsumoCatalogService _insumos;
        // Opcional: provider de estado PilotX. Permite calcular MonitoreoActivo
        // (sembradora "está sembrando") con la misma semántica que SeedMonitor:
        // velocidad real ≥ 1 km/h y suficientes sensores reportando SPM > 0.
        // Si es null, MonitoreoActivo siempre vuelve false (UI gris).
        private readonly IAogStateProvider _state;

        // Opcional: snapshot de secciones AOG (OnRequest[]). Habilita dos cosas:
        //  1. MetodoInicio="pintando": MonitoreoActivo arranca cuando AOG pinta
        //     al menos una sección, en vez de detectar caída de semilla.
        //  2. Por surco: si SeccionAOG>0 y la sección está OFF, el surco queda
        //     en estado "seccion-off" (gris, no genera alarma, no cuenta SPM).
        // Si es null, todo se comporta como si todas las secciones estuvieran ON.
        private readonly ISectionControlService _sections;

        // Estado del "estamos sembrando" — máquina COMPARTIDA con SeedMonitor
        // (deuda D#1): mismas reglas de arranque por método, histéresis de
        // parada (vel<0.3 por >10s) y apagado a 2s sin pintar. Así el snapshot
        // HTTP del Hub coincide con lo que ve el overlay nativo en FormGPS.
        private readonly AgroParallel.VistaX.SiembraStateMachine _siembra =
            new AgroParallel.VistaX.SiembraStateMachine();

        private VistaXConfigDto _cfg;
        private VistaXImplementoDto _imp;

        // (uid,cable) → última lectura (heredado _readings de la base con clave compuesta)
        public sealed class Reading
        {
            public double LastValor;
            public double PrevValor;
            public DateTime LastTs;
            public DateTime PrevTs;
            public double Spm;
            public string Uid;
            public int Cable;
            // Acumulador monotónico del firmware (campo "acum", v2.8.0+). Si el
            // payload lo trae, SPM se deriva de acá (dv/dt) — con flujo estable
            // la derivada sobre "valor" (tasa puntual) colapsa a ≈0.
            // La derivada se calcula sobre una VENTANA de ≥1 s (no tick a tick):
            // "acum" es entero, y a 4 Hz el delta por tick es tan chico (8-9
            // pulsos a 35 pps) que la cuantización mete ±12% de ruido → falsas
            // alarmas bajo/exceso. Con ventana de 1 s el error cae a ~±3%.
            public double WinAcum;      // acum al inicio de la ventana
            /// <summary>Último acumulado crudo del firmware. Lo usa la PRUEBA
            /// DE SIEMBRA para contar semillas entre dos puntos.</summary>
            public double UltimoAcum;
            public DateTime WinTs;      // inicio de la ventana
            public bool HasAcum;
            // true si el sensor reporta modo "state" (on/off): SPM no aplica.
            public bool IsState;
        }

        // Nodos VistaX vistos en MQTT (uid → última telemetría)
        // Extra respecto a _readings (que usa clave compuesta uid#cable).
        private readonly Dictionary<string, DateTime> _nodosVistos =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // UIDs que ya avisamos por usar el topic legacy (D#4) — un log por nodo.
        private readonly HashSet<string> _legacyAvisados =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Configuración de la base ─────────────────────────────────────────
        protected override string TopicPrefix => "vistax/";
        protected override int MinParts => 3;

        // Subscriptions fijas: wildcard estándar por UID.
        // El TelemetriaTopic configurable se suscribe dinámicamente en OnStart().
        protected override string[] Subscriptions => new[] { "vistax/+/telemetria" };

        // Timeout configurable (SensorTimeoutMs); default 3000 ms.
        protected override int TimeoutMs
        {
            get
            {
                int t;
                lock (_lock) { t = _cfg?.SensorTimeoutMs ?? 0; }
                return t > 0 ? t : 3000;
            }
        }

        // ExtractUid: leer del payload ("uid"); si falta o parts[1]="nodos" (legacy),
        // intentar parts[1] como UID del topic "vistax/<uid>/telemetria".
        // El mensaje legacy "vistax/nodos/telemetria" siempre trae uid en el payload.
        protected override string ExtractUid(string[] parts, JsonElement root)
        {
            if (root.TryGetProperty("uid", out var ju))
            {
                var v = ju.GetString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            // Fallback: parts[1] si no es "nodos" (topic moderno vistax/<uid>/telemetria).
            if (parts.Length >= 3 &&
                !string.Equals(parts[1], "nodos", StringComparison.OrdinalIgnoreCase))
            {
                return parts[1];
            }
            return null;
        }

        // ── Constructor ──────────────────────────────────────────────────────
        // ── Objetivo dinámico (dosis variable QuantiX) ────────────────────────

        private double ObjetivoDinamicoDeSurco(int surco)
        {
            if (surco <= 0) return 0;
            var mapa = ArmarObjetivosDinamicos();
            double v;
            return mapa != null && mapa.TryGetValue(surco, out v) ? v : 0;
        }

        /// <summary>Dosis buscada del surco en sem/m (para MOSTRAR). 0 = sin
        /// motor QuantiX asignado o fuente en "manual".</summary>
        private double ObjetivoSemMDeSurco(int surco)
        {
            if (surco <= 0) return 0;
            ArmarObjetivosDinamicos();   // refresca los dos mapas juntos
            double v;
            return _objDinSemM != null && _objDinSemM.TryGetValue(surco, out v) ? v : 0;
        }

        // ── Prueba de siembra (contar semillas sobre N metros) ───────────────
        //
        // Se guarda el acumulado de cada cable al arrancar y se va sumando la
        // distancia recorrida (integrada del pivote, así vale también en curva).
        // Al llegar a la distancia pedida se congela: cada surco queda con lo
        // que contó contra lo que debía sembrar.
        private readonly object _pruebaLock = new object();
        private bool _pruebaActiva, _pruebaTerminada;
        private double _pruebaDistObjetivo, _pruebaDistRecorrida;
        private DateTime _pruebaIniciada;
        private double _pruebaLastE, _pruebaLastN;
        private bool _pruebaTienePos;
        private readonly System.Collections.Generic.Dictionary<string, double> _pruebaAcumInicial
            = new System.Collections.Generic.Dictionary<string, double>();
        private readonly System.Collections.Generic.Dictionary<string, double> _pruebaAcumFinal
            = new System.Collections.Generic.Dictionary<string, double>();

        /// <summary>Arranca una prueba nueva, o REANUDA la que estaba pausada
        /// (si hay medición en curso y no se pidió otra distancia).</summary>
        public void PruebaIniciar(double distanciaM)
        {
            if (distanciaM <= 0) distanciaM = 100;
            lock (_pruebaLock)
            {
                bool reanudar = !_pruebaActiva && !_pruebaTerminada
                                && _pruebaDistRecorrida > 0
                                && Math.Abs(_pruebaDistObjetivo - distanciaM) < 0.01;
                if (!reanudar)
                {
                    _pruebaAcumInicial.Clear();
                    _pruebaAcumFinal.Clear();
                    foreach (var kv in _readings)
                    {
                        _pruebaAcumInicial[kv.Key] = kv.Value.UltimoAcum;
                        _pruebaAcumFinal[kv.Key] = kv.Value.UltimoAcum;
                    }
                    _pruebaDistObjetivo = distanciaM;
                    _pruebaDistRecorrida = 0;
                    _pruebaIniciada = DateTime.Now;
                }
                _pruebaActiva = true;
                _pruebaTerminada = false;
                _pruebaTienePos = false;
            }
        }

        /// <summary>Pausa: deja de sumar metros y semillas, pero conserva lo
        /// medido. Volver a iniciar REANUDA desde donde quedó.</summary>
        public void PruebaCancelar()
        {
            lock (_pruebaLock)
            {
                _pruebaActiva = false;
                _pruebaTienePos = false;   // al reanudar no cuenta el tramo parado
            }
        }

        /// <summary>Borra la prueba: metros y contadores a cero.</summary>
        public void PruebaReset()
        {
            lock (_pruebaLock)
            {
                _pruebaActiva = false;
                _pruebaTerminada = false;
                _pruebaDistRecorrida = 0;
                _pruebaTienePos = false;
                _pruebaAcumInicial.Clear();
                _pruebaAcumFinal.Clear();
                _pruebaIniciada = default(DateTime);
            }
        }

        /// <summary>Avanza la prueba: suma distancia y refresca los contadores.
        /// Se llama en cada snapshot (la UI pide 2 veces por segundo).</summary>
        private void PruebaTick()
        {
            lock (_pruebaLock)
            {
                if (!_pruebaActiva) return;
                try
                {
                    var snap = _state?.GetSnapshot();
                    if (snap != null)
                    {
                        double e = snap.PivotEasting, n = snap.PivotNorthing;
                        if (_pruebaTienePos)
                        {
                            double dx = e - _pruebaLastE, dy = n - _pruebaLastN;
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            // Saltos grandes = teleport del simulador o fix nuevo:
                            // no son metros sembrados.
                            if (d > 0 && d < 25) _pruebaDistRecorrida += d;
                        }
                        _pruebaLastE = e; _pruebaLastN = n; _pruebaTienePos = true;
                    }
                }
                catch { }

                foreach (var kv in _readings)
                {
                    _pruebaAcumFinal[kv.Key] = kv.Value.UltimoAcum;
                    if (!_pruebaAcumInicial.ContainsKey(kv.Key))
                        _pruebaAcumInicial[kv.Key] = kv.Value.UltimoAcum;   // sensor que apareció después
                }

                if (_pruebaDistRecorrida >= _pruebaDistObjetivo)
                {
                    _pruebaActiva = false;
                    _pruebaTerminada = true;
                }
            }
        }

        /// <summary>Surco (bajada) al que está mapeado un cable de un nodo.
        /// 0 si ese cable no figura en el mapeo del implemento.</summary>
        private int BajadaDeSensor(string uid, int cable)
        {
            try
            {
                if (_imp?.MapeoSensores == null) return 0;
                foreach (var sc in _imp.MapeoSensores)
                {
                    if (sc == null || !sc.IsActive) continue;
                    if (sc.Cable == cable &&
                        string.Equals(sc.Uid ?? "", uid ?? "", StringComparison.OrdinalIgnoreCase))
                        return sc.Bajada;
                }
            }
            catch { }
            return 0;
        }

        public VistaXPruebaDto PruebaEstado()
        {
            var dto = new VistaXPruebaDto();
            var objetivos = ArmarObjetivosDinamicos();
            double mMin = LeerVelocidadSegura() / 3.6 * 60.0;
            double objManual = _imp?.Setup?.DensidadObjetivo ?? 0;
            double tol = _imp?.Setup?.ToleranciaDesvio ?? 20;
            if (tol <= 0) tol = 20;

            lock (_pruebaLock)
            {
                dto.Activa = _pruebaActiva;
                dto.Terminada = _pruebaTerminada;
                dto.DistanciaObjetivoM = _pruebaDistObjetivo;
                dto.DistanciaRecorridaM = Math.Round(_pruebaDistRecorrida, 1);
                dto.ToleranciaPct = tol;
                dto.IniciadaIso = _pruebaIniciada == default(DateTime) ? "" : _pruebaIniciada.ToString("O");

                double dist = _pruebaDistRecorrida;
                foreach (var kv in _pruebaAcumFinal)
                {
                    double ini = _pruebaAcumInicial.TryGetValue(kv.Key, out var v0) ? v0 : kv.Value;
                    double cuenta = kv.Value - ini;
                    if (cuenta < 0) cuenta = 0;      // el nodo se reinició en el medio

                    Reading r;
                    if (!_readings.TryGetValue(kv.Key, out r) || r == null) continue;
                    if (r.IsState) continue;         // sensores on/off no cuentan semillas

                    // A qué surco corresponde este cable (mapeo del implemento).
                    int bajada = BajadaDeSensor(r.Uid, r.Cable);
                    double objSemM = objManual;
                    if (bajada > 0 && objetivos.TryGetValue(bajada, out var objSpm) && mMin > 0)
                        objSemM = objSpm / mMin;     // el dinámico viene en sem/min

                    var s = new VistaXPruebaSurcoDto
                    {
                        Bajada = bajada,
                        Cable = r.Cable,
                        Uid = r.Uid ?? "",
                        Semillas = Math.Round(cuenta, 0),
                        ObjetivoSemM = Math.Round(objSemM, 2),
                        Esperadas = Math.Round(objSemM * dist, 0),
                        SemM = dist > 0 ? Math.Round(cuenta / dist, 2) : 0,
                    };
                    if (dist <= 0 || cuenta <= 0)
                    {
                        s.Veredicto = "sin_datos";
                    }
                    else if (objSemM > 0)
                    {
                        s.DesvioPct = Math.Round((s.SemM - objSemM) / objSemM * 100.0, 1);
                        s.Veredicto = Math.Abs(s.DesvioPct) <= tol ? "ok"
                                    : (s.DesvioPct < 0 ? "bajo" : "exceso");
                    }
                    else
                    {
                        s.Veredicto = "sin_datos";   // sin objetivo no hay veredicto posible
                    }
                    dto.Surcos.Add(s);
                }
            }
            dto.Surcos.Sort((a, b) => a.Bajada != b.Bajada ? a.Bajada.CompareTo(b.Bajada) : a.Cable.CompareTo(b.Cable));
            return dto;
        }

        private System.Collections.Generic.Dictionary<int, double> ArmarObjetivosDinamicos()
        {
            if (_objDinPorSurco != null && (DateTime.UtcNow - _objDinStamp).TotalMilliseconds < 700)
                return _objDinPorSurco;
            _objDinStamp = DateTime.UtcNow;
            var mapa = new System.Collections.Generic.Dictionary<int, double>();
            var mapaSemM = new System.Collections.Generic.Dictionary<int, double>();
            _objDinPorSurco = mapa;
            _objDinSemM = mapaSemM;
            try
            {
                // Fuente del objetivo (pedido 2026-08-06): "manual" ignora
                // QuantiX y usa siempre el objetivo propio de VistaX
                // (DensidadObjetivo). Con "quantix" (default) manda la dosis
                // del motor y el propio queda de respaldo para los surcos sin
                // motor asignado.
                string fuente = (_imp?.Setup?.ObjetivoFuente ?? "quantix").Trim().ToLowerInvariant();
                if (fuente == "manual") return mapa;

                if (_quantixCfg == null) return mapa;
                var cfg = _quantixCfg.GetMotores();
                if (cfg == null || cfg.Nodos == null) return mapa;

                var vivos = Registry != null ? Registry.GetAll() : null;
                if (vivos == null) return mapa;

                // Contexto para la dosis buscada: velocidad (para pasar sem/m a
                // sem/min) y prescripción bajo el tractor (si hay shape activo,
                // manda esa dosis, igual que la consigna del motor).
                double velKmh = LeerVelocidadSegura();
                double metrosPorMinuto = velKmh * 1000.0 / 60.0;
                // OJO: parado NO se retorna — el mapa de sem/MIN queda vacío
                // (sin velocidad no hay comparación posible), pero el de sem/m
                // se arma igual: es lo que se MUESTRA como objetivo, y el
                // operario lo mira justamente con la máquina parada.
                double shapeDosis = 0;
                bool usaShape = false;
                try
                {
                    var snap = _state?.GetSnapshot();
                    if (snap != null && snap.ShapeIsInside && snap.ShapeCurrentDose > 0)
                    {
                        shapeDosis = snap.ShapeCurrentDose;
                        usaShape = true;
                    }
                }
                catch { }

                AgroParallel.Models.ImplementoDto central = null;
                try { central = _impCentral?.GetImplemento(); } catch { }
                var surcosPorSeccion = Common.SurcosPorSeccion.Construir(central);

                foreach (var nodo in cfg.Nodos)
                {
                    if (nodo == null || nodo.Motores == null) continue;
                    AgroParallel.Models.NodoStatus live = null;
                    foreach (var v in vivos)
                        if (v != null && string.Equals(v.Uid, nodo.Uid, StringComparison.OrdinalIgnoreCase))
                        { live = v; break; }
                    if (live == null || live.MotorsLive == null) continue;

                    for (int mi = 0; mi < nodo.Motores.Length; mi++)
                    {
                        var m = nodo.Motores[mi];
                        if (m == null) continue;
                        if (!string.Equals(m.UnidadDosis, "sem_m", StringComparison.OrdinalIgnoreCase)) continue;

                        AgroParallel.Models.MotorLive ml = null;
                        foreach (var x in live.MotorsLive)
                            if (x != null && x.Id == mi) { ml = x; break; }
                        if (ml == null) continue;

                        // motor.Cortes numera SECCIONES PilotX (la lección de la
                        // Task 4): expandir a surcos por el implemento central;
                        // sin implemento con surcos, el corte se toma como surco
                        // (sembradora 1 sección = 1 surco, que es lo común acá).
                        var surcos = new System.Collections.Generic.List<int>();
                        if (m.Cortes != null)
                        {
                            foreach (var c in m.Cortes)
                            {
                                System.Collections.Generic.List<int> ss = null;
                                if (surcosPorSeccion != null) surcosPorSeccion.TryGetValue(c, out ss);
                                if (ss != null) surcos.AddRange(ss);
                                else surcos.Add(c);
                            }
                        }
                        if (surcos.Count == 0) continue;

                        // El objetivo del surco es la DOSIS QUE SE BUSCA, no lo
                        // que el motor está entregando (pedido usuario
                        // 2026-08-06: "debería ser 6.0, igual a las variables
                        // frente al shape o al manual de QuantiX").
                        //
                        // Por qué importa: derivarlo del pps_target del motor
                        // hacía que el objetivo SIGUIERA al motor — si el motor
                        // dosificaba mal, el objetivo se movía con él y VistaX
                        // nunca marcaba desvío, que es justo para lo que está.
                        //
                        // La dosis se resuelve con QxDoseResolver — la MISMA
                        // cascada Manual > Mapa > Fija que usa el bridge para
                        // mandar la consigna al motor. Acá había una copia a
                        // mano (Shape > Fija) que ignoraba el modo manual:
                        // QuantiX en MAN con dosis 1,9 y VistaX seguía
                        // mostrando el 6 del shape (reporte 2026-08-07).
                        // Sale en sem/m y se pasa a sem/MIN, que es la unidad
                        // con la que abajo se compara el Spm medido.
                        double dosisSemM = AgroParallel.QuantiX.QxDoseResolver.Resolve(
                            m.ManualMode, m.ManualDosis, m.DosisFija, m.CampoDosis,
                            usaShape ? shapeDosis : 0,
                            campo => { try { return _state?.GetShapeFieldDose(campo) ?? 0; } catch { return 0; } });
                        if (dosisSemM <= 0) continue;

                        // NO se reparte entre surcos: la dosis de siembra es
                        // POR SURCO (es por giro del dosificador). Un motor que
                        // sirve N surcos entrega N veces esa dosis, pero cada
                        // surco sigue esperando la misma.
                        foreach (var s in surcos) mapaSemM[s] = dosisSemM;

                        double spm = dosisSemM * metrosPorMinuto;
                        if (spm <= 0) continue;   // parado: solo el mapa de display
                        foreach (var s in surcos) mapa[s] = spm;
                    }
                }
            }
            catch { /* objetivo dinámico es best-effort: el fijo siempre queda */ }
            return mapa;
        }

        public VistaXLiveService(INodoRegistryService nodos, IVistaXConfigService cfgSvc,
            IInsumoCatalogService insumos = null, IAogStateProvider state = null,
            ISectionControlService sections = null, IImplementoService impCentral = null,
            IQuantiXConfigService quantixCfg = null)
            : base(nodos)
        {
            _cfgSvc = cfgSvc;
            _insumos = insumos;
            _state = state;
            _sections = sections;
            _impCentral = impCentral;
            _quantixCfg = quantixCfg;
            Reload();
        }

        public void Reload()
        {
            lock (_lock)
            {
                _cfg = _cfgSvc.GetConfig() ?? new VistaXConfigDto();
                _imp = _cfgSvc.GetImplemento() ?? new VistaXImplementoDto();
            }
        }

        public void Dispose() => Stop();

        // ── OnStart / OnStop ─────────────────────────────────────────────────
        // Suscribir el topic legacy configurable (además del wildcard de Subscriptions).
        protected override void OnStart()
        {
            string legacy;
            lock (_lock) { legacy = _cfg?.TelemetriaTopic; }
            if (!string.IsNullOrEmpty(legacy))
                Subscribe(legacy);

            // D#2: dejar registro explícito de qué dependencias opcionales
            // faltan — sin esto el servicio degrada en silencio y cuesta
            // diagnosticar por qué (p.ej.) no gatea por sección o no toma
            // los bounds del insumo.
            var faltantes = new List<string>();
            if (_insumos == null) faltantes.Add("insumos (bounds de insumo → tolerancia genérica)");
            if (_state == null) faltantes.Add("state (velocidad PilotX → siempre 0, sin auto-monitoreo)");
            if (_sections == null) faltantes.Add("sections (corte de sección → todos los surcos como ON)");
            if (_impCentral == null) faltantes.Add("implemento central (geometría → copia vistaX legacy)");
            if (faltantes.Count > 0)
                AgpLog.Warn("VistaXLive", "providers opcionales ausentes: " + string.Join("; ", faltantes));

            System.Diagnostics.Trace.WriteLine("[vistax] live service started");
        }

        protected override void OnStop()
        {
            lock (_lock) { _nodosVistos.Clear(); _siembra.Reset(); }
            System.Diagnostics.Trace.WriteLine("[vistax] live service stopped");
        }

        // ── OnPayload ────────────────────────────────────────────────────────
        // El uid ya viene extraído del payload o del topic. Procesa el array
        // "sensores" y calcula SPM por cable. También actualiza _nodosVistos.
        protected override void OnPayload(string uid, string subtopic, string[] topicParts, JsonElement root)
        {
            DateTime now = DateTime.UtcNow;
            lock (_lock) _nodosVistos[uid] = now;

            // D#4: el topic legacy "vistax/nodos/telemetria" sigue soportado
            // (puede haber firmware viejo en campo) pero avisamos una vez por
            // UID para saber qué nodos falta actualizar.
            if (topicParts.Length >= 2 &&
                string.Equals(topicParts[1], "nodos", StringComparison.OrdinalIgnoreCase))
            {
                bool avisar;
                lock (_lock) avisar = _legacyAvisados.Add(uid);
                if (avisar)
                    AgpLog.Warn("VistaXLive", "nodo " + uid +
                        " publica por el topic legacy vistax/nodos/telemetria (firmware viejo — actualizar)");
            }

            if (!root.TryGetProperty("sensores", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var s in arr.EnumerateArray())
            {
                int cable = s.TryGetProperty("cable", out var jc) && jc.ValueKind == JsonValueKind.Number
                    ? jc.GetInt32() : 0;
                double valor = s.TryGetProperty("valor", out var jv) && jv.ValueKind == JsonValueKind.Number
                    ? jv.GetDouble() : 0.0;
                // "acum": acumulador monotónico de pulsos (firmware v2.8.0+).
                bool hasAcum = s.TryGetProperty("acum", out var ja) && ja.ValueKind == JsonValueKind.Number;
                double acum = hasAcum ? ja.GetDouble() : 0.0;
                // "modo":"state" → sensor on/off (bajada/tolva/presión): SPM no aplica.
                bool isState = s.TryGetProperty("modo", out var jm) &&
                    jm.ValueKind == JsonValueKind.String &&
                    string.Equals(jm.GetString(), "state", StringComparison.OrdinalIgnoreCase);
                string key = uid + "#" + cable;
                lock (_lock)
                {
                    if (!_readings.TryGetValue(key, out var r))
                    {
                        r = new Reading { Uid = uid, Cable = cable };
                        _readings[key] = r;
                    }
                    r.PrevValor = r.LastValor;
                    r.PrevTs = r.LastTs;
                    r.LastValor = valor;
                    r.LastTs = now;
                    r.IsState = isState;

                    if (isState)
                    {
                        // On/off: no hay "pulsos por minuto" que derivar.
                        r.Spm = 0;
                    }
                    else if (hasAcum)
                    {
                        // SPM = derivada del ACUMULADOR (estable con flujo constante).
                        // El firmware manda "valor" como tasa puntual (pps) y "acum"
                        // como contador monotónico: derivar sobre valor colapsa a ≈0
                        // apenas el flujo se estabiliza. Ventana ≥1 s (ver Reading).
                        if (!r.HasAcum || r.WinTs == default(DateTime))
                        {
                            r.WinTs = now;
                            r.WinAcum = acum;
                            r.Spm = valor * 60.0; // primer sample: tasa puntual
                        }
                        else
                        {
                            double dv = acum - r.WinAcum;
                            if (dv < 0)
                            {
                                // Reboot del nodo (acum arranca de 0): reiniciar la
                                // ventana y mantener el SPM anterior este tick.
                                r.WinTs = now;
                                r.WinAcum = acum;
                            }
                            else
                            {
                                double dt = (now - r.WinTs).TotalSeconds;
                                if (dt >= 1.0)
                                {
                                    r.Spm = dv / dt * 60.0;
                                    r.WinTs = now;
                                    r.WinAcum = acum;
                                }
                                // dt < 1 s: ventana en curso, mantener SPM anterior.
                            }
                        }
                        r.HasAcum = true;
                        r.UltimoAcum = acum;   // para la prueba de siembra
                    }
                    else if (r.PrevTs != default(DateTime))
                    {
                        // Legacy sin "acum": heurística histórica sobre "valor".
                        double dt = (now - r.PrevTs).TotalSeconds;
                        if (dt > 0.01)
                        {
                            double dv = r.LastValor - r.PrevValor;
                            // valor*60 si parece tasa, derivada si parece acumulador.
                            if (dv >= 0 && r.PrevValor > 0)
                                r.Spm = dv / dt * 60.0;
                            else
                                r.Spm = valor * 60.0;
                        }
                    }
                    else
                    {
                        r.Spm = valor * 60.0;
                    }
                }
            }
        }

        // ------------------- Snapshot --------------------
        public VistaXLiveSnapshotDto GetSnapshot()
        {
            lock (_lock)
            {
                // Geometría desde el implemento CENTRAL (fuente única de verdad).
                // Se lee fresco cada tick — GetImplemento() devuelve el cache del
                // servicio, así que es barato y siempre refleja la última edición
                // hecha en herramienta.html sin depender de un Reload() manual.
                ImplementoDto central = null;
                try { central = _impCentral?.GetImplemento(); } catch { /* fallback legacy */ }

                double distCentral = (central != null && central.DistanciaEntreSurcosM > 0)
                    ? central.DistanciaEntreSurcosM
                    : (_imp?.Setup?.DistanciaEntreSurcos ?? 0.191);
                int torresCentral = (central != null && central.NumeroTorres > 0)
                    ? central.NumeroTorres
                    : (_imp?.Setup?.Torres ?? 0);
                string nombreCentral = !string.IsNullOrEmpty(central?.Nombre)
                    ? central.Nombre
                    : (_imp?.Nombre ?? "");

                var snap = new VistaXLiveSnapshotDto
                {
                    // Asignado al final del método con la lógica real (velocidad +
                    // sensores activos + hysteresis). El valor "IsRunning" de antes
                    // solo significaba "estoy suscripto a MQTT", lo que dejaba todo
                    // pintado como "sembrando" aun con tractor quieto en el galpón.
                    MonitoreoActivo = false,
                    NombreImplemento = nombreCentral,
                    DistanciaEntreSurcos = distCentral,
                    ToleranciaDesvio = _imp?.Setup?.ToleranciaDesvio ?? 0,
                    Torres = torresCentral,
                    SurcosPorTorre = _imp?.Setup?.SurcosPorTorre ?? 0,
                    VistaModoDefault = _imp?.Setup?.VistaModoDefault ?? "surcos"
                };
                int timeoutMs = _cfg?.SensorTimeoutMs > 0 ? _cfg.SensorTimeoutMs : 3000;
                DateTime now = DateTime.UtcNow;

                // Prueba de siembra: avanza con cada snapshot (la UI pide 2/s).
                PruebaTick();

                // Metros por minuto, para llevar los objetivos configurados en
                // sem/m (insumo, tren, override del sensor) a la unidad del
                // comparador: sem/MIN, la misma del Spm medido. Con el tractor
                // parado da 0 → los estados se resuelven por la rama "sin
                // umbrales" (sembrando apagado), nunca dividimos por esto.
                double metrosPorMinuto = LeerVelocidadSegura() / 3.6 * 60.0;

                // Snapshot de secciones AOG: lo consultamos UNA vez por tick.
                // Si _sections es null o el array está vacío, todos los surcos
                // se consideran "sección ON" (comportamiento legacy).
                bool[] secOn = null;
                try
                {
                    var secSnap = _sections?.GetSnapshot();
                    if (secSnap?.OnRequest != null) secOn = secSnap.OnRequest;
                }
                catch { /* defensivo: no romper el snapshot por un fallo en sections */ }

                // Surco → sección PilotX desde el implemento central: el sensor se
                // mapea a un SURCO; la sección que lo corta se deriva sola de acá
                // (una sección = un surco por spec, pero el central es la verdad).
                // Sin esto, la supresión por sección cortada exigía cargar
                // seccion_aog a mano en cada sensor — nadie lo hacía y el corte
                // no suprimía la alarma de tubo.
                Dictionary<int, int> surcoASeccion = null;
                if (central?.Surcos != null && central.Surcos.Count > 0)
                {
                    surcoASeccion = new Dictionary<int, int>();
                    foreach (var su in central.Surcos)
                        if (su != null && su.Numero > 0 && su.SeccionPilotX > 0)
                            surcoASeccion[su.Numero] = su.SeccionPilotX;
                }

                // Trenes: la ESTRUCTURA (id + nombre) sale del implemento central si
                // está disponible; si no, del vistaX implemento legacy. Los OBJETIVOS
                // por tren siguen siendo propios de VistaX (_imp.Setup), porque son
                // densidad de siembra, no geometría.
                var trenesFuente = new List<Tuple<int, string>>();
                if (central?.Trenes != null && central.Trenes.Count > 0)
                {
                    foreach (var t in central.Trenes)
                        trenesFuente.Add(Tuple.Create(t.Id, t.Nombre));
                }
                else if (_imp?.Trenes != null && _imp.Trenes.Count > 0)
                {
                    foreach (var t in _imp.Trenes)
                        trenesFuente.Add(Tuple.Create(t.Id, t.Nombre));
                }

                var trenes = new Dictionary<int, VistaXTrenLiveDto>();
                foreach (var t in trenesFuente)
                {
                    if (!trenes.ContainsKey(t.Item1))
                    {
                        double obj = _imp?.Setup?.DensidadObjetivo ?? 0;
                        if (_imp?.Setup?.ObjetivosTren != null &&
                            _imp.Setup.ObjetivosTren.TryGetValue(t.Item1.ToString(), out var v))
                            obj = v;
                        trenes[t.Item1] = new VistaXTrenLiveDto
                        {
                            Tren = t.Item1,
                            Nombre = string.IsNullOrEmpty(t.Item2) ? ("Tren " + t.Item1) : t.Item2,
                            Objetivo = obj
                        };
                    }
                }

                // Trenes cuyo objetivo mostrado ya lo fijó la dosis de QuantiX
                // (ver abajo): el primero que la tiene gana, el resto no pisa.
                var trenesConObjetivoDinamico = new System.Collections.Generic.HashSet<int>();

                if (_imp?.MapeoSensores != null)
                {
                    foreach (var sc in _imp.MapeoSensores)
                    {
                        if (!sc.IsActive) continue;
                        int trenId = sc.Tren <= 0 ? 1 : sc.Tren;
                        if (!trenes.TryGetValue(trenId, out var tl))
                        {
                            tl = new VistaXTrenLiveDto
                            {
                                Tren = trenId,
                                Nombre = "Tren " + trenId,
                                Objetivo = _imp.Setup?.DensidadObjetivo ?? 0
                            };
                            trenes[trenId] = tl;
                        }

                        string key = (sc.Uid ?? "") + "#" + sc.Cable;
                        _readings.TryGetValue(key, out var r);
        // Per-sensor override (sc.Objetivo > 0): útil para los sensores "otros"
                        // (turbina/tolva/bajada_herramienta) donde la UI muestra barras y el
                        // operario fija un setpoint distinto al de siembra. 0 = usar el del tren.
                        //
                        // DOSIS VARIABLE: si el surco lo alimenta un motor QuantiX
                        // con consigna viva, el objetivo es LO QUE EL MOTOR TIENE
                        // MANDADO (prescripción + velocidad incluidas), no el fijo
                        // del insumo — con shape a 2,3 sem/m y objetivo fijo 16,
                        // era alarma perpetua contra un número que nadie pidió.
                        // El override manual por sensor sigue mandando sobre todo.
                        // UNIDADES: el comparador trabaja en sem/MIN (el Spm del
                        // sensor). El dinámico ya viene en sem/min (pps×60); los
                        // objetivos CONFIGURADOS (override del sensor, tren) están
                        // en sem/m → se convierten con la velocidad viva. Antes se
                        // comparaba 16 sem/m contra ~360 sem/min y todo surco sano
                        // quedaba en "exceso" perpetuo.
                        double objDinamico = ObjetivoDinamicoDeSurco(sc.SurcoDesde > 0 ? sc.SurcoDesde : sc.Bajada);
                        double objMin = sc.Objetivo > 0 ? sc.Objetivo * metrosPorMinuto
                            : (objDinamico > 0 ? objDinamico : tl.Objetivo * metrosPorMinuto);

                        // El OBJETIVO QUE SE MUESTRA en el encabezado del tren
                        // sigue a la dosis efectiva de QuantiX (Manual > Mapa >
                        // Fija). Antes quedaba clavado en el DensidadObjetivo de
                        // la config de VistaX: QuantiX en MAN a 1,9 y el tren
                        // seguía diciendo "objetivo 6" (reporte 2026-08-07). El
                        // primer surco del tren con motor asignado fija el valor.
                        double objSemMDin = ObjetivoSemMDeSurco(sc.SurcoDesde > 0 ? sc.SurcoDesde : sc.Bajada);
                        if (objSemMDin > 0 && !trenesConObjetivoDinamico.Contains(trenId))
                        {
                            tl.Objetivo = Math.Round(objSemMDin, 2);
                            trenesConObjetivoDinamico.Add(trenId);
                        }
                        var surco = new VistaXSurcoStateDto
                        {
                            Bajada = sc.Bajada,
                            Tipo = sc.Tipo ?? "semilla",
                            Tren = trenId,
                            Uid = sc.Uid ?? "",
                            Cable = sc.Cable,
                            Valor = r?.LastValor ?? 0,
                            Spm = r?.Spm ?? 0,
                            // Densidad real: sem/min ÷ metros por minuto. La
                            // hace la PANTALLA porque es la única que conoce la
                            // velocidad — el nodo solo cuenta sus pulsos.
                            SemM = metrosPorMinuto > 0 ? (r?.Spm ?? 0) / metrosPorMinuto : 0,
                            Objetivo = objMin,
                            Muted = sc.Muted,
                            LastSeenIso = r != null && r.LastTs != default(DateTime)
                                ? r.LastTs.ToString("O") : ""
                        };
                        surco.RatioObjetivo = (objMin > 0) ? surco.Spm / objMin : 0.0;

                        // Si el surco está mapeado a una sección AOG y esa sección
                        // está apagada (relay cerrado / fuera de boundary / master OFF),
                        // forzamos estado "seccion-off": gris, sin alarma, sin contar
                        // para SPM agregado. Cuando la sección vuelve ON, recupera
                        // automáticamente el estado real en el próximo tick.
                        // Sección explícita del mapeo si la hay; si no, derivada
                        // del surco vía el implemento central (surco→seccion_pilotx).
                        int seccionDelSurco = sc.SeccionAOG;
                        if (seccionDelSurco <= 0 && surcoASeccion != null)
                        {
                            int nroSurco = sc.SurcoDesde > 0 ? sc.SurcoDesde : sc.Bajada;
                            surcoASeccion.TryGetValue(nroSurco, out seccionDelSurco);
                        }
                        bool seccionOff = false;
                        if (seccionDelSurco > 0 && secOn != null && seccionDelSurco <= secOn.Length)
                        {
                            seccionOff = !secOn[seccionDelSurco - 1];
                        }
                        surco.SeccionCortada = seccionOff;

                        bool stale = r == null || (now - r.LastTs).TotalMilliseconds > timeoutMs;
                        bool esState = VistaXSensorTypes.IsState(sc.Tipo);
                        bool esSiembra = string.Equals(sc.Tipo, VistaXSensorTypes.Semilla, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(sc.Tipo, VistaXSensorTypes.Fertilizante, StringComparison.OrdinalIgnoreCase);
                        // Los casos que NO dependen de umbrales se resuelven en
                        // VxSurcoEvaluator (función pura, con tests de borde:
                        // cabecera, sensor mudo, silenciado, tolva vacía). Acá
                        // solo se arman los datos de entrada. Los umbrales de
                        // densidad necesitan el catálogo de insumos, así que se
                        // calculan abajo y se evalúan en la misma función.
                        bool resueltoSinUmbrales =
                            seccionOff || sc.Muted || stale || esState || (!esSiembra && sc.Objetivo <= 0);

                        if (resueltoSinUmbrales)
                        {
                            var ev = VxSurcoEvaluator.Evaluar(new VxSurcoInput
                            {
                                SeccionCortada = seccionOff,
                                Silenciado = sc.Muted,
                                SinDatos = stale,
                                EsSiembra = esSiembra,
                                EsEstado = esState,
                                EsTolvaVacia = string.Equals(sc.Tipo, VistaXSensorTypes.TolvaVacia,
                                                             StringComparison.OrdinalIgnoreCase),
                                Valor = surco.Valor,
                                SembrandoActivo = _siembra.Activo,
                                ObjetivoSpm = sc.Objetivo,
                            });
                            surco.Estado = ev.Estado;
                            surco.Alerta = ev.Alerta;
                        }
                        else
                        {
                            // Bounds por insumo (Gap #2): si hay insumo activo con
                            // DropMin/DropMax > 0, usar esos sem/m absolutos. Si no,
                            // fallback al cálculo objMin * (1 ± tolerancia).
                            // objMin acá está en sem/min; los bounds del insumo en
                            // sem/m → a sem/min con la velocidad viva (el "×60" de
                            // antes asumía 1 m/s clavado: solo era cierto a 3,6 km/h).
                            double tol = (_imp.Setup?.ToleranciaDesvio ?? 20) / 100.0;
                            double lo  = objMin * (1 - tol);
                            double hi  = objMin * (1 + tol);
                            try
                            {
                                // Con el surco alimentado por un motor QuantiX
                                // (objetivo dinámico) MANDA LA CONSIGNA: los
                                // límites absolutos del insumo no aplican — si
                                // la prescripción pide 2,3 sem/m, tirar 2,3 es
                                // correcto aunque el insumo diga "mínimo 11".
                                var insumo = (objDinamico <= 0 && _insumos != null) ? _insumos.GetActivo() : null;
                                if (insumo != null)
                                {
                                    if (insumo.DropMinSemM > 0) lo = insumo.DropMinSemM * metrosPorMinuto;
                                    if (insumo.DropMaxSemM > 0) hi = insumo.DropMaxSemM * metrosPorMinuto;
                                }
                            }
                            catch { /* catálogo inválido → fallback ya seteado */ }

                            // Misma función que arriba, ahora con los umbrales ya
                            // resueltos: una sola fuente de verdad para el estado
                            // del surco, la que está cubierta por tests.
                            var ev = VxSurcoEvaluator.Evaluar(new VxSurcoInput
                            {
                                EsSiembra = esSiembra,
                                SembrandoActivo = _siembra.Activo,
                                Spm = surco.Spm,
                                ObjetivoSpm = objMin,
                                LimiteBajo = lo,
                                LimiteAlto = hi,
                            });
                            surco.Estado = ev.Estado;
                            surco.Alerta = ev.Alerta;
                        }
                        tl.Surcos.Add(surco);
                    }
                }

                // "Sin mapear": SIEMPRE exponer las lecturas (uid,cable) que NO
                // matchearon ningún MapeoSensores. Antes esto solo aparecía cuando
                // el implemento entero estaba vacío, y eso dejaba al operario
                // ciego ante un mismatch parcial (típico: la UID del nodo no
                // coincide con la mapeada → la UI muestra "no-data" y no hay
                // forma de diagnosticar sin tocar el broker). Ahora si llega
                // algo del nodo y no encaja en el mapeo, aparece igual en un
                // tren "(sin mapear)" con UID + cable + flujo crudos — el
                // operario lo ve y arregla la config en herramienta.html.
                var keysMapeados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_imp?.MapeoSensores != null)
                {
                    foreach (var sc in _imp.MapeoSensores)
                    {
                        if (!sc.IsActive) continue;
                        keysMapeados.Add((sc.Uid ?? "") + "#" + sc.Cable);
                    }
                }
                var sinMapear = new List<Reading>();
                foreach (var kv in _readings)
                {
                    if (!keysMapeados.Contains(kv.Key))
                        sinMapear.Add(kv.Value);
                }
                if (sinMapear.Count > 0)
                {
                    // Tren id alto reservado (99) para que no choque con trenes reales (1..N).
                    var tDiag = new VistaXTrenLiveDto
                    {
                        Tren = 99,
                        Nombre = "(sin mapear)",
                        Objetivo = 0
                    };
                    int bajada = 1;
                    foreach (var r in sinMapear
                        .OrderBy(r => r.Uid, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Cable))
                    {
                        bool stale = (now - r.LastTs).TotalMilliseconds > timeoutMs;
                        tDiag.Surcos.Add(new VistaXSurcoStateDto
                        {
                            Bajada = bajada++,
                            Tipo = "semilla",
                            Tren = 99,
                            Uid = r.Uid ?? "",
                            Cable = r.Cable,
                            Valor = r.LastValor,
                            Spm = r.Spm,
                            Objetivo = 0,
                            Muted = false,
                            LastSeenIso = r.LastTs != default(DateTime) ? r.LastTs.ToString("O") : "",
                            RatioObjetivo = 0,
                            Estado = stale ? "no-data" : "ok"
                        });
                    }
                    trenes[99] = tDiag;
                }

                // Stats globales:
                //  - activos: sensores con telemetría reciente (no incluye no-data ni muted).
                //  - fallas: bajadas tapadas o por debajo del objetivo (no incluye exceso ni muted).
                //  - SpmPromedio: media de los que están reportando algo > 0 y no muted.
                int activos = 0, fallas = 0;
                double sumSpm = 0; int sumN = 0;
                foreach (var tl in trenes.Values)
                {
                    foreach (var s in tl.Surcos)
                    {
                        if (s.Estado == "no-data" && s.Alerta)
                        {
                            // no-data CON alerta (monitoreo activo, sensor de siembra
                            // sin telemetría) = falla productiva. No suma a "activos"
                            // porque no está reportando, pero sí a "fallas".
                            fallas++;
                            continue;
                        }
                        if (s.Estado == "muted" || s.Estado == "no-data" || s.Estado == "seccion-off") continue;
                        activos++;
                        // "alerta" = tolva vacía (sensor state): cuenta como falla productiva.
                        if (s.Estado == "tapado" || s.Estado == "bajo" || s.Estado == "alerta") fallas++;
                        // SPM promedio: solo sensores de siembra/fertilización — meter
                        // RPM de turbina o rotación de eje distorsiona la media.
                        bool esSiembraStat = string.Equals(s.Tipo, VistaXSensorTypes.Semilla, StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(s.Tipo, VistaXSensorTypes.Fertilizante, StringComparison.OrdinalIgnoreCase);
                        if (esSiembraStat && s.Spm > 0) { sumSpm += s.Spm; sumN++; }
                    }
                }
                snap.SurcosActivos = activos;
                snap.FallasActivas = fallas;
                snap.SpmPromedio = sumN > 0 ? Math.Round(sumSpm / sumN, 1) : 0;
                snap.HasAlarm = fallas > 0;
                if (snap.HasAlarm) snap.AlarmMessage = fallas + " surco(s) con falla";

                snap.Trenes = trenes.Values.OrderBy(t => t.Tren).ToList();

                // Nodos VistaX activos
                foreach (var kv in _nodosVistos)
                {
                    bool online = (now - kv.Value).TotalMilliseconds <= timeoutMs * 2;
                    int reporting = _readings.Values.Count(r =>
                        string.Equals(r.Uid, kv.Key, StringComparison.OrdinalIgnoreCase) &&
                        (now - r.LastTs).TotalMilliseconds <= timeoutMs);
                    snap.Nodos.Add(new VistaXNodoLiveDto
                    {
                        Uid = kv.Key,
                        Online = online,
                        SensorsReporting = reporting,
                        LastSeenIso = kv.Value.ToString("O")
                    });
                }

                // ---- Evaluación "estamos sembrando" (MonitoreoActivo) ----
                // Misma semántica que SeedMonitor.EvaluarInicio(): velocidad real
                // ≥ 1 km/h y ≥ umbral surcos de semilla reportando SPM>0; histeresis
                // de 10 s con vel<0.3 para apagar. Sin state provider, queda false.
                snap.MonitoreoActivo = EvaluarSembrando(snap, now);
                snap.Velocidad = _state != null ? LeerVelocidadSegura() : 0;

                // ---- Regla de tres kg/ha (dosis por cinemática de la máquina) ----
                // La sembradora mecánica dosifica los kg/ha para los que fue
                // calibrada (dosis_kgha del insumo activo). El flujo promedio de
                // la PRIMERA pasada estable se captura como referencia: ese
                // flujo ≡ esa dosis; de ahí en más el kg/ha estimado de cada
                // surco es proporcional (spm ÷ spm_ref × dosis_ref, lo saca el
                // cliente). Si cambia el insumo activo, se recaptura.
                CapturarFlujoDeReferencia(snap, now);

                return snap;
            }
        }

        // ---- Captura del flujo de referencia (regla de tres kg/ha) ----------
        private double _spmRef;
        private string _refInsumoId = "";
        private DateTime _refVentanaInicio;
        private double _refAcum;
        private int _refN;

        private void CapturarFlujoDeReferencia(VistaXLiveSnapshotDto snap, DateTime now)
        {
            AgroParallel.Models.InsumoDto insumo = null;
            try { insumo = _insumos?.GetActivo(); } catch { }
            string id = insumo?.Id ?? "";

            // Cambió el insumo activo → la dosis calibrada es otra: recapturar.
            if (id != _refInsumoId)
            {
                _refInsumoId = id;
                _spmRef = 0;
                _refAcum = 0;
                _refN = 0;
                _refVentanaInicio = default(DateTime);
            }

            snap.DosisRefKgHa = insumo?.DosisKgha ?? 0;
            snap.DosisRefUnidad = string.IsNullOrEmpty(insumo?.DosisUnidad)
                ? "kg_ha" : insumo.DosisUnidad;

            if (_spmRef <= 0)
            {
                if (snap.MonitoreoActivo && snap.SpmPromedio > 0)
                {
                    if (_refVentanaInicio == default(DateTime)) _refVentanaInicio = now;
                    _refAcum += snap.SpmPromedio;
                    _refN++;
                    // 10 s de siembra estable promediados = la referencia. Corto
                    // para que el kg/ha aparezca temprano, largo para que un
                    // arranque con baches no fije una referencia mentirosa.
                    if ((now - _refVentanaInicio).TotalSeconds >= 10 && _refN >= 5)
                        _spmRef = _refAcum / _refN;
                }
                else
                {
                    // Se cortó la siembra a mitad de captura: ventana de nuevo.
                    _refVentanaInicio = default(DateTime);
                    _refAcum = 0;
                    _refN = 0;
                }
            }
            snap.SpmRef = _spmRef > 0 ? Math.Round(_spmRef, 1) : 0;
        }

        /// <summary>Ajuste MANUAL de la referencia (además de la captura
        /// automática): "fijar" = el flujo de este instante equivale a la
        /// densidad configurada; "auto" = borrar y recapturar solo.</summary>
        public double AjustarReferenciaDensidad(string accion)
        {
            // El spm actual se calcula ANTES de tomar _lock (GetSnapshot ya
            // lockea internamente).
            double spmActual = 0;
            try { spmActual = GetSnapshot()?.SpmPromedio ?? 0; } catch { }

            lock (_lock)
            {
                _refVentanaInicio = default(DateTime);
                _refAcum = 0;
                _refN = 0;
                bool fijar = string.Equals(accion, "fijar", StringComparison.OrdinalIgnoreCase);
                // "fijar" sin flujo (parado / secciones cortadas) degrada a
                // "auto": no hay un instante que fijar, mejor recapturar.
                _spmRef = fijar && spmActual > 0 ? spmActual : 0;
                return _spmRef;
            }
        }

        // Lee AvgSpeed de PilotX defensivo: cualquier excepción del provider,
        // NaN o ±Infinity → 0 (tractor "frenado"), nunca propagamos al snapshot.
        private double LeerVelocidadSegura()
        {
            if (_state == null) return 0;
            try
            {
                double v = _state.GetSnapshot()?.AvgSpeed ?? 0;
                if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
                return v;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Inicio/parada manual del monitoreo (método "manual" o stop del
        /// operario). Mismo contrato que SeedMonitor.IniciarMonitoreoManual().
        /// </summary>
        public void ForzarMonitoreoManual(bool activo)
        {
            lock (_lock) _siembra.ForzarManual(activo);
        }

        // Decide si "estamos sembrando" usando el snapshot recién armado.
        // Debe llamarse adentro de _lock (lo está). La lógica vive en la
        // SiembraStateMachine COMPARTIDA con SeedMonitor (D#1) — acá solo
        // juntamos las lecturas (velocidad, secciones, sensores) y volcamos
        // los contadores al snapshot para el pill "detenido" del widget.
        private bool EvaluarSembrando(VistaXLiveSnapshotDto snap, DateTime now)
        {
            if (_state == null)
            {
                snap.MotivoDetenido = "Sin estado de PilotX (state provider null)";
                return false;
            }

            string metodo = (_cfg?.MetodoInicio ?? "sensores").Trim().ToLowerInvariant();
            _siembra.Configurar(metodo,
                _cfg?.UmbralSensoresActivos ?? 3,
                _cfg?.TiempoConfirmacionMs ?? 500);
            snap.MetodoInicio = _siembra.Metodo;

            // Secciones pintando (fuente: ISectionControlService, si está).
            int seccionesPintando = 0;
            bool seccionesDisponibles = _sections != null;
            if (_sections != null)
            {
                try
                {
                    var secSnap = _sections.GetSnapshot();
                    if (secSnap?.OnRequest != null)
                    {
                        for (int i = 0; i < secSnap.OnRequest.Length; i++)
                            if (secSnap.OnRequest[i]) seccionesPintando++;
                    }
                }
                catch { /* defensivo */ }
            }

            // Sensores de semilla con caída detectada (SPM>0.5, no muted).
            int sensoresArriba = 0;
            if (snap.Trenes != null)
            {
                foreach (var t in snap.Trenes)
                {
                    if (t?.Surcos == null) continue;
                    foreach (var s in t.Surcos)
                    {
                        if (s == null) continue;
                        if (!string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase)) continue;
                        if (s.Muted) continue;
                        if (s.Estado == "no-data" || s.Estado == "muted" || s.Estado == "seccion-off") continue;
                        if (s.Spm > 0.5) sensoresArriba++;
                    }
                }
            }

            bool activo = _siembra.Evaluar(new AgroParallel.VistaX.SiembraEntrada
            {
                VelocidadKmh = LeerVelocidadSegura(),
                SeccionesActivas = seccionesPintando,
                SeccionesDisponibles = seccionesDisponibles,
                SensoresActivos = sensoresArriba
            }, now);

            // Volcamos los contadores al snapshot — el widget los muestra al
            // tocar el pill "detenido" para que el operario sepa qué falta.
            snap.SeccionesPintando = seccionesPintando;
            snap.SensoresArriba = sensoresArriba;
            snap.UmbralSensores = _siembra.UmbralSensores;
            snap.VelMinima = _siembra.VelMinima;
            snap.MotivoDetenido = _siembra.MotivoDetenido;
            return activo;
        }
    }
}
