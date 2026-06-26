// ============================================================================
// QuantiXMotorBridge.cs - Puente dosis→PPS→MQTT para motores ESP32
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.QuantiX
{
    public class QuantiXMotorBridge : IDisposable
    {
        // Fase A: el bridge dejó de referenciar FormGPS directamente.
        // Lee todo el estado de PilotX a través de IAogStateProvider. Eso permite
        // moverlo a AgroParallel.Services (netstandard2.0) en Fase C sin
        // tocar la lógica funcional. Mantenemos un campo legacy `_parent` solo
        // para el constructor de compatibilidad con call sites viejos hasta que
        // se migre el wire-up (FormGPS y FormAgroParallelHub).
        private readonly IAogStateProvider _state;
        // Opcional: si está presente y hay prescripción activa, la dosis del
        // motor se resuelve por point-in-polygon contra la zona del GPS antes
        // de caer al shapefile/DosisFija. null = comportamiento legacy.
        private readonly IPrescripcionService _prescripciones;
        private IMqttClient _mqtt;
        private System.Timers.Timer _timer;
        private System.Timers.Timer _reloadTimer;
        private MotoresConfig _motores;
        private bool _disposed;
        private bool _connected;

        public bool IsRunning { get; private set; }
        public int MessagesSent { get; private set; }

        // PPS real por motor (leído del status MQTT del ESP32).
        // Key: "uid-motorIdx", Value: pps_real.
        private readonly Dictionary<string, double> _ppsReal =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Historial posición/secciones para motores en tren trasero (Tren=1).
        // Compartido con SectionXBridge: ambos usan el mismo PositionHistory
        // de AgroParallel.Common (mañana también LineX).
        private readonly PositionHistory _posHistory = new PositionHistory(Log);

        /// Retorna el PPS real del motor (leído del ESP32 via MQTT status).
        public double GetPpsReal(string uid, int motorIdx)
        {
            string key = uid + "-" + motorIdx;
            double val;
            return _ppsReal.TryGetValue(key, out val) ? val : 0;
        }

        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "qx_bridge.log");

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n"); }
            catch { }
        }

        public QuantiXMotorBridge(IAogStateProvider state, IPrescripcionService prescripciones = null)
        {
            _state = state ?? throw new ArgumentNullException("state");
            _prescripciones = prescripciones;
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;

            _motores = MotoresConfig.Load();
            if (_motores.Nodos.Count == 0)
            {
                Log("No hay nodos configurados");
                return;
            }

            try
            {
                var vistaXCfg = VistaXConfig.Load();
                var factory = new MqttFactory();
                _mqtt = factory.CreateMqttClient();

                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(vistaXCfg.BrokerAddress ?? "127.0.0.1",
                        vistaXCfg.BrokerPort > 0 ? vistaXCfg.BrokerPort : 1883)
                    .WithClientId("QX_Bridge_" + Guid.NewGuid().ToString("N").Substring(0, 6))
                    .WithCleanSession(true)
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                    .Build();

                _mqtt.ApplicationMessageReceivedAsync += OnStatusReceived;
                await _mqtt.ConnectAsync(opts);
                _connected = true;
                Log("MQTT conectado a " + vistaXCfg.BrokerAddress + ":" + vistaXCfg.BrokerPort);

                // Suscribirse al status de todos los nodos.
                await _mqtt.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter("agp/quantix/+/status_live").Build());
                Log("Suscrito a status_live");
            }
            catch (Exception ex)
            {
                Log("MQTT error: " + ex.Message);
                return;
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
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_reloadTimer != null) { _reloadTimer.Stop(); _reloadTimer.Dispose(); _reloadTimer = null; }

            if (_mqtt != null)
            {
                var m = _mqtt; _mqtt = null;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { m.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build()).Wait(2000); m.Dispose(); }
                    catch { }
                });
            }

            Log("Detenido (" + MessagesSent + " msgs)");
        }

        private async void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed || _mqtt == null || !_connected) return;

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
                        double pps = 0;
                        double velocidadMs = velMotorKmh / 3.6;
                        if (seccionOn && dosisEfectiva > 0 && velMotorKmh > 0.5)
                        {
                            if (esSemillas)
                            {
                                // sem/m: dosis = semillas por metro de surco.
                                //   surcos = cortes del motor (eje solidario: planta todos juntos).
                                //   semillas/seg = dosis × vel_m/s × surcos
                                //   semillas_por_pulso = semillas_vuelta / pulsos_por_vuelta (dientes)
                                //   pps = semillas/seg / semillas_por_pulso
                                int surcos = tieneCortes ? motor.Cortes.Count : 1;
                                int ppvSem = motor.DientesEngranaje > 0 ? motor.DientesEngranaje : 24;
                                double semPorPulso = motor.SemillasVuelta > 0 ? motor.SemillasVuelta / ppvSem : 0;
                                if (semPorPulso > 0)
                                {
                                    double semillasPorSeg = dosisEfectiva * velocidadMs * surcos;
                                    pps = semillasPorSeg / semPorPulso;
                                }
                            }
                            else if (motor.MeterCal > 0)
                            {
                                // kg/ha: producto_g_por_seg = dosis × 1000 × ancho × vel / 10000
                                //        pps = producto_g_por_seg / meterCal (gramos por pulso)
                                double productoGramosPorSeg = (dosisEfectiva * 1000.0 * anchoActivo * velocidadMs) / 10000.0;
                                pps = productoGramosPorSeg / motor.MeterCal;
                            }
                        }

                        // Log detallado por motor cada 5 segundos.
                        if (MessagesSent % 25 == 0 && mi == 0)
                        {
                            int ppr = motor.DientesEngranaje > 0 ? motor.DientesEngranaje : 24;
                            double rpmTarget = ppr > 0 ? pps * 60.0 / ppr : 0;
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
                            var msg = new MqttApplicationMessageBuilder()
                                .WithTopic(topic)
                                .WithPayload(payload)
                                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                                .Build();
                            await _mqtt.PublishAsync(msg);
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

        private System.Threading.Tasks.Task OnStatusReceived(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                string topic = args.ApplicationMessage.Topic;
                if (!topic.Contains("status_live")) return System.Threading.Tasks.Task.CompletedTask;

                var seg = args.ApplicationMessage.PayloadSegment;
                if (seg.Count == 0) return System.Threading.Tasks.Task.CompletedTask;
                string payload = System.Text.Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);

                // Extraer UID del topic: agp/quantix/{UID}/status_live
                string[] parts = topic.Split('/');
                if (parts.Length < 4) return System.Threading.Tasks.Task.CompletedTask;
                string uid = parts[2];

                int id = (int)ExtractNum(payload, "\"id\":");
                double ppsReal = ExtractNum(payload, "\"pps_real\":");

                string key = uid + "-" + id;
                _ppsReal[key] = ppsReal;
            }
            catch { }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private static double ExtractNum(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += key.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-'))
                end++;
            if (end == idx) return 0;
            double val;
            double.TryParse(json.Substring(idx, end - idx),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out val);
            return val;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
