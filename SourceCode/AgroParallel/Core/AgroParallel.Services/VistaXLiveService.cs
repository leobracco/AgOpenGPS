// ============================================================================
// VistaXLiveService.cs — implementación.
//
// Estrategia:
//   - Suscribe el filtro vistax/+/telemetria (y el configurado) al MQTT
//     compartido del NodoRegistry.
//   - Parsea payload { uid, sensores:[{cable, valor, raw}] }.
//   - Mantiene Dictionary<(uid,cable), LastReading> con timestamp + valor.
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

namespace AgroParallel.Services
{
    public sealed class VistaXLiveService : IVistaXLiveService, IDisposable
    {
        private readonly INodoRegistryService _nodos;
        private readonly IVistaXConfigService _cfgSvc;
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

        // Estado del "estamos sembrando" con hysteresis: una vez activo, se mantiene
        // hasta que la velocidad esté < 0.3 km/h por más de 10 s seguidos. Replica
        // EvaluarInicio() del SeedMonitor para que el snapshot HTTP del Hub coincida
        // con lo que ve el overlay nativo en FormGPS.
        private bool _monitoreoActivo;
        private DateTime _paradaDesde = DateTime.MinValue;
        // Tracking del "sin pintar" en modo pintando: si AOG deja de pintar
        // todas las secciones durante >2s, apagamos el monitor inmediatamente
        // (sin esperar los 10s de la histeresis de velocidad). Pensado para
        // que el monitor refleje el estado real del mapeo: si AOG no pinta,
        // no estamos sembrando para fines de mapeo aunque el tractor siga
        // andando rápido entre cabeceras.
        private DateTime _sinPintarDesde = DateTime.MinValue;

        private VistaXConfigDto _cfg;
        private VistaXImplementoDto _imp;
        private readonly object _lock = new object();

        // (uid,cable) → última lectura
        private sealed class Reading
        {
            public double LastValor;
            public double PrevValor;
            public DateTime LastTs;
            public DateTime PrevTs;
            public double Spm;
            public string Uid;
            public int Cable;
        }
        private readonly Dictionary<string, Reading> _readings =
            new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);

        // Nodos VistaX vistos en MQTT (uid → última telemetría)
        private readonly Dictionary<string, DateTime> _nodosVistos =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public bool IsRunning { get; private set; }

        public VistaXLiveService(INodoRegistryService nodos, IVistaXConfigService cfgSvc,
            IInsumoCatalogService insumos = null, IAogStateProvider state = null,
            ISectionControlService sections = null)
        {
            _nodos = nodos;
            _cfgSvc = cfgSvc;
            _insumos = insumos;
            _state = state;
            _sections = sections;
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

        public void Start()
        {
            if (IsRunning) return;
            if (_nodos == null) return;
            _nodos.MessageReceived += OnMqttMessage;
            // El filtro estándar legacy.
            _ = _nodos.SubscribeAsync(_cfg?.TelemetriaTopic ?? "vistax/nodos/telemetria");
            // Wildcard adicional por compatibilidad con futuros UIDs por-nodo.
            _ = _nodos.SubscribeAsync("vistax/+/telemetria");
            IsRunning = true;
            System.Diagnostics.Debug.WriteLine("[vistax] live service started");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try { _nodos.MessageReceived -= OnMqttMessage; } catch { }
            IsRunning = false;
            lock (_lock) { _readings.Clear(); _nodosVistos.Clear(); }
            System.Diagnostics.Debug.WriteLine("[vistax] live service stopped");
        }

        public void Dispose() => Stop();

        // ------------------- MQTT in --------------------
        private void OnMqttMessage(object sender, MqttMessageReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Topic)) return;
            // Aceptamos: vistax/nodos/telemetria (legacy) y vistax/<uid>/telemetria.
            if (!e.Topic.StartsWith("vistax/", StringComparison.OrdinalIgnoreCase)) return;
            if (!e.Topic.EndsWith("/telemetria", StringComparison.OrdinalIgnoreCase) &&
                !e.Topic.Equals(_cfg?.TelemetriaTopic ?? "", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                using (var doc = JsonDocument.Parse(e.Payload))
                {
                    var root = doc.RootElement;
                    string uid = root.TryGetProperty("uid", out var ju) ? (ju.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(uid))
                    {
                        // Si el topic es vistax/<uid>/telemetria, intentar inferir UID del topic.
                        var parts = e.Topic.Split('/');
                        if (parts.Length >= 3) uid = parts[1];
                    }
                    if (string.IsNullOrEmpty(uid)) return;

                    DateTime now = DateTime.UtcNow;
                    lock (_lock) _nodosVistos[uid] = now;

                    if (!root.TryGetProperty("sensores", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
                    foreach (var s in arr.EnumerateArray())
                    {
                        int cable = s.TryGetProperty("cable", out var jc) && jc.ValueKind == JsonValueKind.Number
                            ? jc.GetInt32() : 0;
                        double valor = s.TryGetProperty("valor", out var jv) && jv.ValueKind == JsonValueKind.Number
                            ? jv.GetDouble() : 0.0;
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
                            // SPM = derivada temporal escalada a /min, si tenemos prev.
                            if (r.PrevTs != default(DateTime))
                            {
                                double dt = (now - r.PrevTs).TotalSeconds;
                                if (dt > 0.01)
                                {
                                    double dv = r.LastValor - r.PrevValor;
                                    // El payload típico ya viene como "sem/m" o lecturas
                                    // crudas. Para SPM aproximamos: valor * 60 si parece tasa,
                                    // o derivada si parece acumulador. Heurística simple:
                                    // si el "valor" actual ≥ "prev", lo tratamos como acumulador.
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
            }
            catch { }
        }

        // ------------------- Snapshot --------------------
        public VistaXLiveSnapshotDto GetSnapshot()
        {
            lock (_lock)
            {
                var snap = new VistaXLiveSnapshotDto
                {
                    // Asignado al final del método con la lógica real (velocidad +
                    // sensores activos + hysteresis). El valor "IsRunning" de antes
                    // solo significaba "estoy suscripto a MQTT", lo que dejaba todo
                    // pintado como "sembrando" aun con tractor quieto en el galpón.
                    MonitoreoActivo = false,
                    NombreImplemento = _imp?.Nombre ?? "",
                    ToleranciaDesvio = _imp?.Setup?.ToleranciaDesvio ?? 0,
                    Torres = _imp?.Setup?.Torres ?? 0,
                    SurcosPorTorre = _imp?.Setup?.SurcosPorTorre ?? 0,
                    VistaModoDefault = _imp?.Setup?.VistaModoDefault ?? "surcos"
                };
                int timeoutMs = _cfg?.SensorTimeoutMs > 0 ? _cfg.SensorTimeoutMs : 3000;
                DateTime now = DateTime.UtcNow;

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

                // Trenes: derivar desde Implemento.Trenes o, si no hay, de los sensores.
                var trenes = new Dictionary<int, VistaXTrenLiveDto>();
                if (_imp?.Trenes != null && _imp.Trenes.Count > 0)
                {
                    foreach (var t in _imp.Trenes)
                    {
                        if (!trenes.ContainsKey(t.Id))
                        {
                            double obj = _imp.Setup?.DensidadObjetivo ?? 0;
                            if (_imp.Setup?.ObjetivosTren != null &&
                                _imp.Setup.ObjetivosTren.TryGetValue(t.Id.ToString(), out var v))
                                obj = v;
                            trenes[t.Id] = new VistaXTrenLiveDto
                            {
                                Tren = t.Id,
                                Nombre = string.IsNullOrEmpty(t.Nombre) ? ("Tren " + t.Id) : t.Nombre,
                                Objetivo = obj
                            };
                        }
                    }
                }

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
                        double objMin = sc.Objetivo > 0 ? sc.Objetivo : tl.Objetivo;
                        var surco = new VistaXSurcoStateDto
                        {
                            Bajada = sc.Bajada,
                            Tipo = sc.Tipo ?? "semilla",
                            Tren = trenId,
                            Uid = sc.Uid ?? "",
                            Cable = sc.Cable,
                            Valor = r?.LastValor ?? 0,
                            Spm = r?.Spm ?? 0,
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
                        bool seccionOff = false;
                        if (sc.SeccionAOG > 0 && secOn != null && sc.SeccionAOG <= secOn.Length)
                        {
                            seccionOff = !secOn[sc.SeccionAOG - 1];
                        }
                        surco.SeccionCortada = seccionOff;

                        bool stale = r == null || (now - r.LastTs).TotalMilliseconds > timeoutMs;
                        if (seccionOff)
                        {
                            // Prioridad por encima de mute/no-data: el operario decidió
                            // (manual o auto) que esta franja no siembra ahora.
                            surco.Estado = "seccion-off";
                        }
                        else if (sc.Muted)
                        {
                            // Sensor silenciado por config: no dispara alarma, no cuenta como falla.
                            // Igual reportamos la lectura para que la UI pueda mostrarla en gris.
                            surco.Estado = "muted";
                        }
                        else if (stale)
                        {
                            surco.Estado = "no-data";
                        }
                        else
                        {
                            // Bounds por insumo (Gap #2): si hay insumo activo con
                            // DropMin/DropMax > 0, usar esos sem/m absolutos. Si no,
                            // fallback al cálculo objMin * (1 ± tolerancia).
                            // objMin acá está en sem/min, los bounds del insumo en
                            // sem/m → convertimos sem/m * 60 = sem/min para comparar.
                            double tol = (_imp.Setup?.ToleranciaDesvio ?? 20) / 100.0;
                            double lo  = objMin * (1 - tol);
                            double hi  = objMin * (1 + tol);
                            try
                            {
                                var insumo = _insumos != null ? _insumos.GetActivo() : null;
                                if (insumo != null)
                                {
                                    if (insumo.DropMinSemM > 0) lo = insumo.DropMinSemM * 60.0;
                                    if (insumo.DropMaxSemM > 0) hi = insumo.DropMaxSemM * 60.0;
                                }
                            }
                            catch { /* catálogo inválido → fallback ya seteado */ }

                            if (objMin <= 0)
                            {
                                surco.Estado = "ok";
                            }
                            else if (surco.Spm <= 0.5)
                            {
                                // Sensor reportando telemetría pero sin pulsos = bajada bloqueada.
                                surco.Estado = "tapado";
                                surco.Alerta = true;
                            }
                            else if (surco.Spm < lo)
                            {
                                surco.Estado = "bajo";
                                surco.Alerta = true;
                            }
                            else if (surco.Spm > hi)
                            {
                                // Exceso: en general no es falla productiva, pero la UI lo marca en azul.
                                surco.Estado = "exceso";
                            }
                            else
                            {
                                surco.Estado = "ok";
                            }
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
                        if (s.Estado == "muted" || s.Estado == "no-data" || s.Estado == "seccion-off") continue;
                        activos++;
                        if (s.Estado == "tapado" || s.Estado == "bajo") fallas++;
                        if (s.Spm > 0) { sumSpm += s.Spm; sumN++; }
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

                return snap;
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

        // Decide si "estamos sembrando" usando el snapshot recién armado.
        // Debe llamarse adentro de _lock (lo está). Mantiene estado en
        // _monitoreoActivo y _paradaDesde para histeresis equivalente a
        // SeedMonitor.EvaluarInicio() (velocidad <0.3 km/h por >10 s → off).
        //
        // Branch por _cfg.MetodoInicio:
        //   - "pintando": arranca cuando AOG pinta ≥1 sección + vel≥1 km/h.
        //                 Para cuando deja de pintar o vel<0.3 sostenida 10s.
        //   - "sensores" (o cualquier otro / default): arranca cuando hay
        //                 ≥UmbralSensoresActivos surcos detectando caída de
        //                 semilla (SPM>0.5) + vel≥1 km/h. Para con vel<0.3
        //                 sostenida 10s. Es el comportamiento histórico.
        private bool EvaluarSembrando(VistaXLiveSnapshotDto snap, DateTime now)
        {
            if (_state == null)
            {
                snap.MotivoDetenido = "Sin estado de PilotX (state provider null)";
                return false;
            }
            double vel = LeerVelocidadSegura();

            string metodo = (_cfg?.MetodoInicio ?? "sensores").Trim().ToLowerInvariant();
            snap.MetodoInicio = metodo;

            bool condicionArranque;
            int seccionesPintando = 0;
            int sensoresArriba = 0;
            int umbralCfg = 3;
            double velMin;
            string motivo = "";

            if (metodo == "pintando")
            {
                velMin = 0.3;
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
                else
                {
                    motivo = "Sin servicio de secciones (sections null)";
                }
                condicionArranque = seccionesPintando > 0 && vel >= velMin;
                if (string.IsNullOrEmpty(motivo))
                {
                    if (seccionesPintando == 0)        motivo = "AOG no está pintando ninguna sección";
                    else if (vel < velMin)             motivo = "Velocidad " + vel.ToString("0.0") + " km/h < " + velMin.ToString("0.0");
                }
            }
            else
            {
                velMin = 1.0;
                try { if (_cfg != null && _cfg.UmbralSensoresActivos > 0) umbralCfg = _cfg.UmbralSensoresActivos; }
                catch { }

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

                condicionArranque = vel >= velMin && sensoresArriba >= umbralCfg;
                if (sensoresArriba < umbralCfg) motivo = "Solo " + sensoresArriba + " sensor(es) con SPM>0.5 (umbral " + umbralCfg + ")";
                else if (vel < velMin)          motivo = "Velocidad " + vel.ToString("0.0") + " km/h < " + velMin.ToString("0.0");
            }

            // Volcamos los contadores al snapshot — el widget los muestra al
            // tocar el pill "detenido" para que el operario sepa qué falta.
            snap.SeccionesPintando = seccionesPintando;
            snap.SensoresArriba = sensoresArriba;
            snap.UmbralSensores = umbralCfg;
            snap.VelMinima = velMin;
            snap.MotivoDetenido = motivo;

            // Histeresis común a ambos modos: una vez activo, sigue activo
            // hasta que vel<0.3 sostenida >10 s.
            if (_monitoreoActivo)
            {
                if (vel < 0.3)
                {
                    if (_paradaDesde == DateTime.MinValue) _paradaDesde = now;
                    else if ((now - _paradaDesde).TotalSeconds > 10) _monitoreoActivo = false;
                }
                else
                {
                    _paradaDesde = DateTime.MinValue;
                }

                // En modo "pintando": si AOG deja de pintar todas las
                // secciones por >2s, apagamos el monitor sin esperar a la
                // parada del tractor. Es la semántica que el operario espera:
                // "no pinta → no estoy sembrando".
                if (metodo == "pintando")
                {
                    if (seccionesPintando == 0)
                    {
                        if (_sinPintarDesde == DateTime.MinValue) _sinPintarDesde = now;
                        else if ((now - _sinPintarDesde).TotalSeconds > 2)
                        {
                            _monitoreoActivo = false;
                            _sinPintarDesde = DateTime.MinValue;
                        }
                    }
                    else
                    {
                        _sinPintarDesde = DateTime.MinValue;
                    }
                }
                else
                {
                    _sinPintarDesde = DateTime.MinValue;
                }
            }
            else
            {
                if (condicionArranque)
                {
                    _monitoreoActivo = true;
                    _paradaDesde = DateTime.MinValue;
                    _sinPintarDesde = DateTime.MinValue;
                }
            }
            return _monitoreoActivo;
        }
    }
}
