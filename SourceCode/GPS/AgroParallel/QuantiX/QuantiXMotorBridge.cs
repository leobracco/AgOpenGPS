// ============================================================================
// QuantiXMotorBridge.cs - Puente dosis→PPS→MQTT para motores ESP32
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.QuantiX
{
    public class QuantiXMotorBridge : IDisposable
    {
        private readonly AgOpenGPS.FormGPS _parent;
        private IMqttClient _mqtt;
        private System.Windows.Forms.Timer _timer;
        private MotoresConfig _motores;
        private bool _disposed;
        private bool _connected;

        public bool IsRunning { get; private set; }
        public int MessagesSent { get; private set; }

        // PPS real por motor (leído del status MQTT del ESP32).
        // Key: "uid-motorIdx", Value: pps_real.
        private readonly Dictionary<string, double> _ppsReal =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

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

        public QuantiXMotorBridge(AgOpenGPS.FormGPS parent)
        {
            _parent = parent ?? throw new ArgumentNullException("parent");
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

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 200;
            _timer.Tick += OnTick;
            _timer.Start();

            // Recargar config cada 2 segundos para capturar cambios manuales.
            var _reloadTimer = new System.Windows.Forms.Timer();
            _reloadTimer.Interval = 2000;
            _reloadTimer.Tick += (s2, ev2) => { try { _motores = MotoresConfig.Load(); } catch { } };
            _reloadTimer.Start();

            IsRunning = true;
            Log("Iniciado con " + _motores.Nodos.Count + " nodo(s)");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }

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

        private async void OnTick(object sender, EventArgs e)
        {
            if (_disposed || _mqtt == null || !_connected) return;

            try
            {
                double dosis = 0;
                bool inside = false;
                double velocidadKmh = 0;

                // Dosis del shapefile.
                try
                {
                    var layerField = _parent.GetType().GetField("shapefileLayer",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (layerField != null)
                    {
                        object layer = layerField.GetValue(_parent);
                        if (layer != null)
                        {
                            var pDose = layer.GetType().GetProperty("CurrentDose");
                            var pInside = layer.GetType().GetProperty("CurrentInside");
                            if (pDose != null) dosis = (double)pDose.GetValue(layer);
                            if (pInside != null) inside = (bool)pInside.GetValue(layer);
                        }
                    }
                }
                catch { }

                velocidadKmh = _parent.avgSpeed;

                // Secciones activas de AOG.
                bool[] seccionesAOG = null;
                try
                {
                    if (_parent.tool != null)
                    {
                        int numSec = _parent.tool.numOfSections;
                        seccionesAOG = new bool[numSec];
                        for (int i = 0; i < numSec; i++)
                        {
                            var sec = _parent.section[i];
                            // sectionOnRequest refleja el corte de cabecera/contorno.
                            // isSectionOn puede quedarse true después del corte.
                            seccionesAOG[i] = sec != null && sec.sectionOnRequest;
                        }
                    }
                }
                catch { }

                // Log cada 2 segundos.
                if (MessagesSent % 10 == 0)
                {
                    string secInfo = "null";
                    if (seccionesAOG != null)
                    {
                        int onCount = 0;
                        foreach (bool s in seccionesAOG) if (s) onCount++;
                        secInfo = onCount + "/" + seccionesAOG.Length + " ON";
                    }
                    Log(string.Format("dosis={0:F1} inside={1} vel={2:F1} sec={3} toolW={4:F2}",
                        dosis, inside, velocidadKmh, secInfo,
                        _parent.tool != null ? _parent.tool.width : 0));
                }

                // Sin velocidad o fuera del mapa → dosis 0 (pero se puede overridear con DosisFija).
                bool dosisFromMap = dosis > 0 && inside;
                if (velocidadKmh < 0.5) dosis = 0;
                if (!inside) dosis = 0;

                foreach (var nodo in _motores.Nodos)
                {
                    if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;

                    for (int mi = 0; mi < nodo.Motores.Length; mi++)
                    {
                        var motor = nodo.Motores[mi];

                        // Dosis efectiva: fija > campo específico > campo global del shapefile.
                        double dosisEfectiva = motor.DosisFija;
                        if (dosisEfectiva <= 0)
                        {
                            // Si el motor tiene un campo específico, leerlo del shapefile.
                            if (!string.IsNullOrEmpty(motor.CampoDosis))
                            {
                                dosisEfectiva = ReadShapeFieldDose(motor.CampoDosis);
                            }
                            else
                            {
                                dosisEfectiva = dosis; // Campo global (StyleField).
                            }
                        }

                        // Eje solidario: el motor alimenta TODAS las secciones.
                        // El corte mecánico lo hace el embrague/relay (SectionX).
                        // El motor gira al ancho TOTAL mientras al menos 1 sección esté abierta.
                        double anchoTotal = _parent.tool != null && _parent.tool.width > 0
                            ? _parent.tool.width : 1.0;
                        bool seccionOn = false;

                        if (motor.Cortes != null && motor.Cortes.Count > 0 && seccionesAOG != null)
                        {
                            // Si AL MENOS una sección del motor está activa → motor ON a ancho total.
                            foreach (int corte in motor.Cortes)
                            {
                                int idx = corte - 1;
                                if (idx >= 0 && idx < seccionesAOG.Length && seccionesAOG[idx])
                                {
                                    seccionOn = true;
                                    break;
                                }
                            }
                        }

                        // Sin cortes asignados → funciona si hay dosis y velocidad.
                        bool tieneCortes = motor.Cortes != null && motor.Cortes.Count > 0;
                        if (!seccionOn && !tieneCortes && dosisEfectiva > 0 && velocidadKmh > 0.5)
                            seccionOn = true;

                        double anchoActivo = seccionOn ? anchoTotal : 0;

                        // PPS (pulsos por segundo para el motor).
                        // Fórmula:
                        //   producto_g_por_seg = dosis_kg_ha × 1000 × ancho_m × vel_m/s / 10000
                        //   pps = producto_g_por_seg / meterCal (gramos por pulso)
                        double pps = 0;
                        if (seccionOn && dosisEfectiva > 0 && motor.MeterCal > 0 && velocidadKmh > 0.5)
                        {
                            double velocidadMs = velocidadKmh / 3.6;
                            double productoGramosPorSeg = (dosisEfectiva * 1000.0 * anchoActivo * velocidadMs) / 10000.0;
                            pps = productoGramosPorSeg / motor.MeterCal;
                        }

                        // Log detallado por motor cada 5 segundos.
                        if (MessagesSent % 25 == 0 && mi == 0)
                        {
                            int ppr = motor.DientesEngranaje > 0 ? motor.DientesEngranaje : 24;
                            double rpmTarget = ppr > 0 ? pps * 60.0 / ppr : 0;
                            Log(string.Format("  M{0} dosis={1:F0}kg/ha ancho={2:F1}m cal={3:F1}g/p RPM={4:F0} pps={5:F1}",
                                mi, dosisEfectiva, anchoActivo, motor.MeterCal, rpmTarget, pps));
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

        /// Lee el valor de un campo DBF específico del polígono actual del shapefile.
        private double ReadShapeFieldDose(string fieldName)
        {
            try
            {
                var layerField = _parent.GetType().GetField("shapefileLayer",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (layerField == null) return 0;

                object layer = layerField.GetValue(_parent);
                if (layer == null) return 0;

                // CurrentPolygonIndex: qué polígono está bajo el tractor.
                var pIdx = layer.GetType().GetProperty("CurrentPolygonIndex");
                if (pIdx == null) return 0;
                int polyIdx = (int)pIdx.GetValue(layer);
                if (polyIdx < 0) return 0;

                // TryGetPolygonNumeric(int polyIndex, string fieldName, out double value)
                var method = layer.GetType().GetMethod("TryGetPolygonNumeric",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    object[] args = new object[] { polyIdx, fieldName, 0.0 };
                    bool ok = (bool)method.Invoke(layer, args);
                    if (ok) return (double)args[2];
                }
            }
            catch { }
            return 0;
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
