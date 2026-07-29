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

                foreach (var nodo in _motores.Nodos)
                {
                    if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;

                    // Snapshot del tren trasero (puede ser null si todavía no
                    // hay historial suficiente — los motores Tren=1 caen al
                    // delantero hasta que el tractor avance lo suficiente).
                    bool[] secTrasero = seccionesPilotX;
                    if (nodo.DistanciaEntreTrenes > 0.05 && seccionesPilotX != null)
                    {
                        secTrasero = _posHistory.GetSectionsAtDistanceBack(nodo.DistanciaEntreTrenes)
                            ?? seccionesPilotX;
                    }

                    for (int mi = 0; mi < nodo.Motores.Length; mi++)
                    {
                        var motor = nodo.Motores[mi];

                        // Fuente de secciones según tren físico del motor.
                        bool[] secMotor = (motor.Tren == 0) ? seccionesPilotX : secTrasero;

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

                        double anchoActivo = seccionOn ? anchoTotal : 0;
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
                            Surcos = tieneCortes ? motor.Cortes.Count : 1,
                            SemillasVuelta = motor.SemillasVuelta,
                            DientesEngranaje = motor.DientesEngranaje,
                        });

                        // Log detallado por motor cada 5 segundos.
                        if (MessagesSent % 25 == 0 && mi == 0)
                        {
                            double rpmTarget = QxPulseCalculator.Rpm(pps, motor.DientesEngranaje);
                            if (esSemillas)
                            {
                                int surcos = tieneCortes ? motor.Cortes.Count : 1;
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

        // Velocidad efectiva de un motor = promedio de la velocidad real de las
        // secciones que cubre (Cortes). PilotX calcula esas velocidades con el
        // efecto de rotación del implemento en curvas (signo incluido: una
        // sección interna puede ir más lento o incluso para atrás). Si no hay
        // velocidades por sección o el motor no tiene cortes válidos, cae a la
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
            return count > 0 ? sum / count : avgSpeedKmh;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
