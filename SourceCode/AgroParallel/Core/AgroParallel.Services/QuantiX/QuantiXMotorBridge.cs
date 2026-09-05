// ============================================================================
// QuantiXMotorBridge.cs - Puente dosis→PPS→MQTT para motores ESP32
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.Common;

namespace AgroParallel.QuantiX
{
    public class QuantiXMotorBridge : IDisposable
    {
        // Fase A: el bridge dejó de referenciar FormGPS directamente.
        // Lee todo el estado de PilotX a través de IAogStateProvider.
        private readonly IAogStateProvider _state;
        // Transporte MQTT: reusa la conexión del NodoRegistryService (la misma
        // que usan los live services y el config service). El bridge ya no abre
        // un IMqttClient propio — una sola conexión PC↔broker para todo QuantiX.
        private readonly INodoRegistryService _nodos;
        // Opcional: si está presente y hay prescripción activa, la dosis del
        // motor se resuelve por point-in-polygon contra la zona del GPS antes
        // de caer al shapefile/DosisFija. null = comportamiento legacy.
        private readonly IPrescripcionService _prescripciones;
        private System.Timers.Timer _timer;
        private System.Timers.Timer _reloadTimer;
        private MotoresConfig _motores;
        private bool _disposed;

        public bool IsRunning { get; private set; }
        public int MessagesSent { get; private set; }

        // Historial posición/secciones para motores en tren trasero (Tren=1).
        // Compartido con SectionXBridge: ambos usan el mismo PositionHistory
        // de AgroParallel.Common (mañana también LineX).
        private readonly PositionHistory _posHistory = new PositionHistory(Log);

        // Implemento central (Task 5): fuente de verdad de a qué tren pertenece
        // cada surco. Si está seteado y el implemento tiene trenes útiles para
        // los surcos de un motor, la distancia de tren sale de ahí; si no, se
        // cae al fallback manual de siempre (motor.Tren + nodo.DistanciaEntreTrenes)
        // — bit a bit igual que antes de esto. null = comportamiento legacy puro
        // (caller no lo cableó todavía).
        public Func<ImplementoDto> ImplementoProvider { get; set; }

        // "Una vez por arranque": evita spamear el log a tick rate (200ms) si
        // un motor queda mal configurado (surcos de trenes distintos).
        private bool _loggedTrenConflicto;

        // Antirrebote de APAGADO por (uid, motor): sectionOnRequest es la señal
        // CRUDA del cálculo de secciones del engine y puede parpadear en falso
        // durante un solo frame (medido en banco 2026-08-22: blips de 1 tick con
        // velocidad y dosis estables, cada 6-30 s). Cada blip mandaba un target
        // con seccion_on:false → el nodo cortaba PWM y reseteaba el PID → bache
        // de dosis de ~1 s. Regla: para CORTAR hace falta ver la sección apagada
        // SEC_OFF_TICKS ticks seguidos (~400 ms a 200 ms/tick); para PRENDER no
        // hay retardo. El cierre de lote (IsJobStarted) NO pasa por acá: corta
        // en el tick.
        private const int SEC_OFF_TICKS = 2;
        private readonly Dictionary<string, int[]> _secOffStreak = new Dictionary<string, int[]>();

        // "Una vez por arranque" (mismo patrón que SectionXCutAdapter): dos
        // flags independientes porque un mismo rig puede tener motores que sí
        // derivan del implemento y otros que caen al fallback por nodo.
        private bool _loggedDerivadoImplemento;
        private bool _loggedFallbackNodo;

        /// Retorna el PPS real del motor. Sale del registry, que ya parsea
        /// agp/quantix/{uid}/status_live en NodoStatus.MotorsLive — el bridge
        /// dejó de duplicar ese parseo con una suscripción propia.
        public double GetPpsReal(string uid, int motorIdx)
        {
            if (_nodos == null || string.IsNullOrEmpty(uid)) return 0;
            try
            {
                var all = _nodos.GetAll();
                for (int i = 0; i < all.Count; i++)
                {
                    var n = all[i];
                    if (n == null || !string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase)) continue;
                    var ml = n.MotorsLive;
                    if (ml != null)
                        for (int m = 0; m < ml.Count; m++)
                            if (ml[m] != null && ml[m].Id == motorIdx) return ml[m].PpsReal;
                    return 0;
                }
            }
            catch { }
            return 0;
        }

        private static readonly string LogPath = Path.Combine(
            AgroParallel.Common.AgpPaths.ConfigRoot, "qx_bridge.log");

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n"); }
            catch { }
        }

        public QuantiXMotorBridge(IAogStateProvider state, INodoRegistryService nodos,
            IPrescripcionService prescripciones = null)
        {
            _state = state ?? throw new ArgumentNullException("state");
            _nodos = nodos;
            _prescripciones = prescripciones;
        }

        public System.Threading.Tasks.Task StartAsync()
        {
            if (IsRunning) return System.Threading.Tasks.Task.CompletedTask;

            _motores = MotoresConfig.Load();
            if (_motores.Nodos.Count == 0)
            {
                Log("No hay nodos configurados");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            if (_nodos == null)
            {
                Log("Sin NodoRegistry: bridge no puede publicar");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            _timer = new System.Timers.Timer { Interval = 200, AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();

            // Recargar config cada 2 segundos para capturar cambios manuales.
            _reloadTimer = new System.Timers.Timer { Interval = 2000, AutoReset = true };
            _reloadTimer.Elapsed += (s2, ev2) => { try { _motores = MotoresConfig.Load(); } catch { } };
            _reloadTimer.Start();

            IsRunning = true;
            Log("Iniciado con " + _motores.Nodos.Count + " nodo(s)");
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_reloadTimer != null) { _reloadTimer.Stop(); _reloadTimer.Dispose(); _reloadTimer = null; }

            // La conexión MQTT es del NodoRegistryService — acá no hay nada
            // que desconectar.
            Log("Detenido (" + MessagesSent + " msgs)");
        }

        /// <summary>
        /// Lo conecta el ejecutable al arrancar: recibe los BITS de las
        /// secciones (bit 0 = sección 1) que no deben pintarse porque su motor
        /// está apagado a mano. El bridge no conoce al guiado —viven en
        /// assemblies que no se referencian—, así que el host se suscribe acá.
        /// Si nadie lo conecta, no pasa nada: el pintado sigue como siempre.
        /// </summary>
        public Action<uint> OnSeccionesApagadas;

        private uint _ultimaMascaraApagados;

        /// <summary>Arma la máscara de surcos de motores apagados y la publica.</summary>
        private void PublicarSeccionesApagadas()
        {
            uint mask = 0;
            var nodos = _motores?.Nodos;
            if (nodos != null)
            {
                foreach (var nodo in nodos)
                {
                    if (nodo == null || !nodo.Habilitado || nodo.Motores == null) continue;
                    foreach (var motor in nodo.Motores)
                    {
                        if (motor == null || !motor.Apagado || motor.Cortes == null) continue;
                        foreach (int corte in motor.Cortes)
                        {
                            int bit = corte - 1;              // corte 1 → bit 0
                            if (bit >= 0 && bit < 32) mask |= (1u << bit);
                        }
                    }
                }
            }

            if (mask != _ultimaMascaraApagados)
            {
                _ultimaMascaraApagados = mask;
                Log(string.Format("motores apagados a mano: mascara de surcos 0x{0:X}", mask));
            }

            var h = OnSeccionesApagadas;
            if (h != null) { try { h(mask); } catch { } }
        }

        private async void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed || _nodos == null) return;

            try
            {
                // Snapshot atómico de PilotX — todas las lecturas en este tick
                // ven el mismo estado (no hay races con FormGPS).
                AogStateSnapshot snap = null;
                try { snap = _state.GetSnapshot(); } catch { }
                if (snap == null) return;

                // Surcos de los motores apagados a mano → el guiado los saca
                // del pintado (si no dosifica, no se sembró). Se recalcula en
                // cada tick porque el operario puede apagar/prender en marcha,
                // y se publica aunque no haya cambiado: es un uint, sale gratis.
                PublicarSeccionesApagadas();

                double dosis = 0;
                bool inside = false;
                double velocidadKmh = snap.AvgSpeed;

                // Solo leer dosis del mapa si hay un trabajo abierto.
                // Sin job, el shapefileLayer puede quedar con la última
                // lectura — eso causaba que el bridge siguiera dosificando
                // con el mapa anterior.
                if (snap.IsJobStarted)
                {
                    dosis = snap.ShapeCurrentDose;
                    inside = snap.ShapeIsInside;
                }

                // ---- Prescripción variable-rate (Gap #5) ---------------------
                // Una sola consulta por tick (compartida por todos los motores
                // que NO tengan DosisFija ni CampoDosis específicos). Si hay
                // activa y el GPS cae en una zona, eso pisa la dosis del
                // shapefile global (mayor especificidad). El "inside" también
                // se considera true porque la prescripción cubre el lote.
                double dosisPrescripcion = 0;
                if (_prescripciones != null && snap.IsJobStarted &&
                    Math.Abs(snap.Latitude) > 0.001 && Math.Abs(snap.Longitude) > 0.001)
                {
                    try { dosisPrescripcion = _prescripciones.GetDoseAt(snap.Latitude, snap.Longitude); }
                    catch { /* lookup fail -> 0 -> cae al shapefile */ }
                    if (dosisPrescripcion > 0)
                    {
                        dosis = dosisPrescripcion;
                        inside = true; // estamos en una zona definida
                    }
                }

                // Secciones activas de PilotX (snapshot.SectionOnRequest ya
                // contiene exactamente sec.sectionOnRequest por índice).
                bool[] seccionesPilotX = snap.SectionOnRequest;
                int numSecSnap = snap.NumSections;

                // Acumular historial de secciones PilotX por distancia recorrida —
                // necesario para motores en tren trasero (Tren=1).
                if (seccionesPilotX != null)
                    _posHistory.Record(snap.PivotEasting, snap.PivotNorthing, seccionesPilotX);

                // Log cada 2 segundos.
                if (MessagesSent % 10 == 0)
                {
                    string secInfo = "null";
                    if (seccionesPilotX != null)
                    {
                        int onCount = 0;
                        foreach (bool s in seccionesPilotX) if (s) onCount++;
                        secInfo = onCount + "/" + seccionesPilotX.Length + " ON";
                    }
                    Log(string.Format("dosis={0:F1} inside={1} vel={2:F1} sec={3} toolW={4:F2}",
                        dosis, inside, velocidadKmh, secInfo, snap.ToolWidth));
                }

                // Sin velocidad o fuera del mapa → dosis 0 (pero se puede overridear con DosisFija).
                bool dosisFromMap = dosis > 0 && inside;
                if (velocidadKmh < 0.5) dosis = 0;
                if (!inside) dosis = 0;

                // Implemento central (Task 5): motor.Cortes son SECCIONES PilotX
                // (1-based) que controla el motor — NO números de surco.
                // TrenResolver espera SurcoDto.Numero (mismo espacio que
                // SectionXCutAdapter/Task 4), así que armamos el mapa
                // sección->surcos una sola vez por tick antes de resolver cada
                // motor, para no confundir los dos espacios de numeración.
                // El provider puede tirar (IOException del disco, etc.): si eso
                // aborta el tick entero, TODOS los motores pierden su target este
                // ciclo por un problema ajeno a ellos. Con el catch, este tick
                // sigue con implCentral=null y cada motor cae a su fallback por
                // nodo (mismo resultado que "provider no wireado").
                ImplementoDto implCentral = null;
                try { if (ImplementoProvider != null) implCentral = ImplementoProvider(); }
                catch { /* sin implemento este tick: fallback por nodo, el tick sigue */ }

                Dictionary<int, List<int>> surcosPorSeccion = SurcosPorSeccion.Construir(implCentral);

                // Cache de secciones "atrasadas" por distancia: motores de
                // distintos nodos pueden compartir la misma distancia de tren;
                // el recorrido del historial se hace una sola vez por distancia
                // en este tick (mismo patrón que SectionXCutAdapter).
                Dictionary<double, bool[]> secRetrasadasCache = null;

                foreach (var nodo in _motores.Nodos)
                {
                    if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;

                    for (int mi = 0; mi < nodo.Motores.Length; mi++)
                    {
                        var motor = nodo.Motores[mi];

                        // Canal sin motor cableado: se le manda consigna nula en vez
                        // de saltearlo. Saltearlo dejaría al nodo con el último target
                        // vivo hasta que actúe su watchdog (3 s), y además CommTime
                        // dejaría de refrescarse — CheckRelays corta todas las salidas
                        // a los 4 s sin comunicación, incluidas las de los motores que
                        // sí funcionan.
                        if (!motor.Habilitado)
                        {
                            try
                            {
                                await _nodos.PublishAsync(
                                    "agp/quantix/" + nodo.Uid + "/target",
                                    "{\"id\":" + mi + ",\"pps\":0,\"seccion_on\":false}",
                                    false);
                                MessagesSent++;
                            }
                            catch { }
                            continue;
                        }

                        // Tren del motor: derivado de sus surcos vía el
                        // implemento central, con fallback EXACTO al campo
                        // manual de siempre si no hay dato derivable.
                        var surcosMotor = SurcosDeSecciones(motor.Cortes, surcosPorSeccion);
                        var trM = TrenResolver.Resolver(implCentral, surcosMotor);
                        double distMotor;
                        if (trM != null)
                        {
                            distMotor = trM.DistanciaM;
                            if (!_loggedDerivadoImplemento)
                            {
                                Log("trenes: derivados del implemento");
                                _loggedDerivadoImplemento = true;
                            }
                            if (trM.Conflicto && !_loggedTrenConflicto)
                            {
                                Log(string.Format("  M{0} (nodo {1}): surcos de trenes distintos — usando tren {2}",
                                    mi, nodo.Uid, trM.TrenId));
                                _loggedTrenConflicto = true;
                            }
                        }
                        else
                        {
                            distMotor = (motor.Tren == 0) ? 0 : nodo.DistanciaEntreTrenes; // fallback fase 1
                            // Antes esto solo logueaba con motor.Tren != 0 — un rig
                            // todo-delantero (Tren == 0 en todos los motores) nunca
                            // mostraba la fuente. Logueamos una vez pase lo que pase.
                            if (!_loggedFallbackNodo)
                            {
                                Log(motor.Tren != 0
                                    ? "trenes: fallback por nodo (implemento sin distancias)"
                                    : "trenes: fallback por nodo (sin desfase: tren delantero)");
                                _loggedFallbackNodo = true;
                            }
                        }

                        // Fuente de secciones según la distancia de tren resuelta.
                        bool[] secMotor = seccionesPilotX;
                        if (distMotor > 0.05 && seccionesPilotX != null)
                            secMotor = ObtenerSeccionesRetrasadas(distMotor, seccionesPilotX, ref secRetrasadasCache);

                        // Velocidad real de este motor según las secciones que cubre.
                        // Captura el efecto de rotación en curvas (un motor en el
                        // extremo externo va más rápido que el promedio, el interno
                        // más lento). Fallback a AvgSpeed si no hay datos por sección.
                        double velMotorKmh = MotorSpeedKmh(motor.Cortes, snap.SectionSpeedsKmh, snap.AvgSpeed);

                        // Dosis efectiva: Manual > Mapa > Fija (ver QxDoseResolver).
                        // Antes la DosisFija ganaba sobre el mapa; ahora "mapa manda".
                        // 'dosis' es el mapa global del tick (0 si fuera del lote/sin vel).
                        double dosisEfectiva = QxDoseResolver.Resolve(
                            motor.ManualMode,
                            motor.ManualDosis,
                            motor.DosisFija,
                            motor.CampoDosis,
                            dosis,
                            campo => _state.GetShapeFieldDose(campo));

                        // Eje solidario: el motor alimenta TODAS las secciones.
                        // El corte mecánico lo hace el embrague/relay (SectionX).
                        // El motor gira al ancho TOTAL mientras al menos 1 sección esté abierta.
                        double anchoTotal = snap.ToolWidth > 0 ? snap.ToolWidth : 1.0;
                        bool seccionOn = false;

                        if (motor.Cortes != null && motor.Cortes.Count > 0 && secMotor != null)
                        {
                            // Si AL MENOS una sección del motor está activa → motor ON a ancho total.
                            foreach (int corte in motor.Cortes)
                            {
                                int idx = corte - 1;
                                if (idx >= 0 && idx < secMotor.Length && secMotor[idx])
                                {
                                    seccionOn = true;
                                    break;
                                }
                            }
                        }

                        // Sin cortes asignados → funciona si hay dosis y velocidad.
                        bool tieneCortes = motor.Cortes != null && motor.Cortes.Count > 0;
                        if (!seccionOn && !tieneCortes && dosisEfectiva > 0 && velMotorKmh > 0.5)
                            seccionOn = true;

                        // Antirrebote de apagado (ver campo _secOffStreak arriba):
                        // absorber blips de sectionOnRequest de menos de
                        // SEC_OFF_TICKS ticks antes de mandar el corte al nodo.
                        int[] streak;
                        if (!_secOffStreak.TryGetValue(nodo.Uid, out streak) ||
                            streak.Length < nodo.Motores.Length)
                        {
                            streak = new int[nodo.Motores.Length];
                            _secOffStreak[nodo.Uid] = streak;
                        }
                        if (seccionOn)
                        {
                            streak[mi] = 0;
                        }
                        else
                        {
                            streak[mi]++;
                            if (streak[mi] < SEC_OFF_TICKS)
                            {
                                seccionOn = true; // blip de 1 tick: sostener el motor
                                Log(string.Format("  M{0}: blip seccion OFF de 1 tick absorbido (antirrebote)", mi));
                            }
                        }

                        // Gate maestro de siembra: sin trabajo/lote abierto NO se
                        // dosifica, aunque haya velocidad, secciones en ON o dosis
                        // fija/manual cargada. El estado de sección (SectionOnRequest)
                        // y la dosis manual/fija son independientes de IsJobStarted,
                        // así que el motor arrancaba con solo velocidad y el lote
                        // cerrado. QxPulseCalculator.Pps ya devuelve 0 con SeccionOn
                        // en false, así que el motor queda quieto (pps:0, seccion_on:false).
                        if (!snap.IsJobStarted)
                            seccionOn = false;

                        // OFF del operario (overlay): el motor queda fuera de la
                        // siembra hasta que lo prendan de nuevo. Va DESPUÉS del
                        // antirrebote a propósito — no es un blip de secciones,
                        // es una orden explícita y no se "sostiene" ni un tick.
                        // Gana sobre todo: manual, mapa, dosis fija y secciones.
                        if (motor.Apagado)
                            seccionOn = false;

                        // Ancho y surcos REALES del motor (fix 2026-08-08, QxAnchoMotor):
                        // antes kg/ha usaba SIEMPRE el ancho total (un motor con la
                        // mitad de los surcos dosificaba al DOBLE) y sem/m contaba
                        // las SECCIONES como surcos (4 secciones de 12 surcos = 12
                        // veces de menos). El mejor dato gana: implemento central →
                        // proporcional a secciones → ancho total (histórico).
                        double anchoMotor = QxAnchoMotor.Resolver(
                            motor.Cortes, surcosMotor, implCentral,
                            numSecSnap > 0 ? numSecSnap : (seccionesPilotX != null ? seccionesPilotX.Length : 0),
                            anchoTotal);
                        double anchoActivo = seccionOn ? anchoMotor : 0;
                        bool esSemillas = string.Equals(motor.UnidadDosis, "sem_m",
                            StringComparison.OrdinalIgnoreCase);

                        // PPS (pulsos por segundo para el motor) según la unidad de dosis.
                        // El cálculo vive en QxPulseCalculator (función pura, con
                        // tests de borde: parado, sección cerrada, sin calibrar).
                        // Acá SOLO se arman los datos de entrada.
                        double pps = QxPulseCalculator.Pps(new QxPulseInput
                        {
                            Dosis = dosisEfectiva,
                            VelocidadKmh = velMotorKmh,
                            SeccionOn = seccionOn,
                            EsSemillas = esSemillas,
                            AnchoM = anchoActivo,
                            MeterCal = motor.MeterCal,
                            Surcos = QxAnchoMotor.Surcos(motor.Cortes, surcosMotor),
                            SemillasVuelta = motor.SemillasVuelta,
                            DientesEngranaje = motor.DientesEngranaje,
                        });

                        // Log detallado por motor cada 5 segundos.
                        if (MessagesSent % 25 == 0 && mi == 0)
                        {
                            double rpmTarget = QxPulseCalculator.Rpm(pps, motor.DientesEngranaje);
                            if (esSemillas)
                            {
                                int surcos = QxAnchoMotor.Surcos(motor.Cortes, surcosMotor);
                                Log(string.Format("  M{0} dosis={1:F1}sem/m surcos={2} vel={3:F1}km/h sem/vuelta={4:F0} RPM={5:F0} pps={6:F1}",
                                    mi, dosisEfectiva, surcos, velMotorKmh, motor.SemillasVuelta, rpmTarget, pps));
                            }
                            else
                            {
                                Log(string.Format("  M{0} dosis={1:F0}kg/ha ancho={2:F1}m vel={3:F1}km/h cal={4:F1}g/p RPM={5:F0} pps={6:F1}",
                                    mi, dosisEfectiva, anchoActivo, velMotorKmh, motor.MeterCal, rpmTarget, pps));
                            }
                        }

                        string topic = "agp/quantix/" + nodo.Uid + "/target";
                        string payload = "{\"id\":" + mi
                            + ",\"pps\":" + Math.Round(pps, 2).ToString(CultureInfo.InvariantCulture)
                            + ",\"seccion_on\":" + (seccionOn ? "true" : "false")
                            + "}";

                        try
                        {
                            // PublishAsync devuelve false si el registry está
                            // desconectado del broker — el target se pierde y
                            // el firmware aplica su timeout de seguridad.
                            await _nodos.PublishAsync(topic, payload, false);
                            MessagesSent++;
                        }
                        catch { }
                    }
                    // Secciones/relays las controla SectionX, no QuantiX.
                }
            }
            catch (Exception ex)
            {
                Log("Tick error: " + ex.Message);
            }
        }

        // Velocidad efectiva de un motor = velocidad GPS del tractor escalada
        // por la PROPORCIÓN entre la velocidad de sus secciones (Cortes) y la
        // media de todas las secciones. La proporción captura el efecto de
        // rotación en curvas (la sección externa va más rápido que la interna,
        // signo incluido). La MAGNITUD absoluta se ancla al GPS a propósito:
        // speedPixels del núcleo asume que el lazo de posición corre exacto a
        // gpsHz y en la práctica corre más lento — medido en banco 2026-08-24,
        // TODAS las velocidades por sección salían ~8% bajas y la dosis quedaba
        // corta en la misma proporción (7 sem/m pedidos → 6.4 entregados).
        // Si no hay velocidades por sección, el motor no tiene cortes válidos,
        // o la media global es casi cero (parado: ratio ruidoso), cae a la
        // velocidad promedio del tractor.
        private static double MotorSpeedKmh(IList<int> cortes, double[] sectionSpeeds, double avgSpeedKmh)
        {
            if (sectionSpeeds == null || sectionSpeeds.Length == 0) return avgSpeedKmh;
            if (cortes == null || cortes.Count == 0) return avgSpeedKmh;

            double sum = 0;
            int count = 0;
            foreach (int corte in cortes)
            {
                int idx = corte - 1;
                if (idx >= 0 && idx < sectionSpeeds.Length)
                {
                    sum += sectionSpeeds[idx];
                    count++;
                }
            }
            if (count == 0) return avgSpeedKmh;

            double globalSum = 0;
            for (int i = 0; i < sectionSpeeds.Length; i++) globalSum += sectionSpeeds[i];
            double globalMean = globalSum / sectionSpeeds.Length;
            if (globalMean < 0.5) return avgSpeedKmh;

            return avgSpeedKmh * ((sum / count) / globalMean);
        }

        // motor.Cortes son SECCIONES PilotX (1-based) que controla el motor —
        // TrenResolver espera números de SURCO (SurcoDto.Numero), un espacio
        // distinto (puede haber N surcos por sección, migración VistaX típica).
        // Devuelve la unión de surcos de todas las secciones del motor, o null
        // si no hay mapa/cortes (el resolver hace fallback solo con null).
        private static List<int> SurcosDeSecciones(IList<int> cortes, Dictionary<int, List<int>> surcosPorSeccion)
        {
            if (cortes == null || cortes.Count == 0 || surcosPorSeccion == null) return null;
            List<int> surcos = null;
            foreach (int seccion in cortes)
            {
                List<int> lista;
                if (!surcosPorSeccion.TryGetValue(seccion, out lista)) continue;
                if (surcos == null) surcos = new List<int>();
                surcos.AddRange(lista);
            }
            return surcos;
        }

        // Secciones del tren retrasado a `distancia` metros, con caché por tick
        // (varios motores pueden pedir la misma distancia; el recorrido del
        // historial se hace una sola vez — mismo patrón que SectionXCutAdapter).
        private bool[] ObtenerSeccionesRetrasadas(double distancia, bool[] fallback, ref Dictionary<double, bool[]> cache)
        {
            if (cache == null) cache = new Dictionary<double, bool[]>();
            bool[] cached;
            if (!cache.TryGetValue(distancia, out cached))
            {
                cached = _posHistory.GetSectionsAtDistanceBack(distancia) ?? fallback;
                cache[distancia] = cached;
            }
            return cached;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
