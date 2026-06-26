// ============================================================================
// SectionsSpeedPublisher.cs
//
// Publica por MQTT la velocidad real de cada sección del implemento (km/h).
// Origen: AogStateSnapshot.SectionSpeedsKmh, calculado por PilotX en
// CalculateSectionLookAhead() — captura el efecto de rotación del implemento
// en curvas (sección externa más rápida, interna más lenta o negativa).
//
// Topic: agp/aog/sections_speed
// Cadencia: 5 Hz (200 ms). El raw de PilotX es 10 Hz, pero a 5 Hz alcanza para
// dosis variable + diagnóstico y baja a la mitad la carga del broker.
//
// Consumidores típicos:
//   - QuantiX (dosis por motor, usa la velocidad del set de surcos que cubre)
//   - FlowX (dosis líquida proporcional)
//   - VistaX (SPM esperado por surco → detección de tapado/exceso real en giros)
//   - Logger/observabilidad
//
// El publisher NO depende de SectionXConfig.Nodos — la velocidad es info de
// PilotX independiente de si hay nodos SectionX conectados. Sí toma el broker
// de VistaXConfig por consistencia con los otros bridges.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.SectionX
{
    public sealed class SectionsSpeedPublisher : IDisposable
    {
        private readonly IAogStateProvider _state;
        private IMqttClient _mqtt;
        private System.Timers.Timer _timer;
        private bool _disposed, _connected;

        public bool IsRunning { get; private set; }
        public long MessagesSent { get; private set; }

        // Cadencia: 200 ms = 5 Hz. Lo suficiente para dosis variable y monitoreo.
        private const int IntervalMs = 200;

        // Mismo topic para todos los consumidores (broadcast PilotX).
        private const string Topic = "agp/aog/sections_speed";

        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "sections_speed.log");

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n"); }
            catch { }
        }

        public SectionsSpeedPublisher(IAogStateProvider state)
        {
            if (state == null) throw new ArgumentNullException("state");
            _state = state;
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;
            try
            {
                var vCfg = VistaXConfig.Load();
                var factory = new MqttFactory();
                _mqtt = factory.CreateMqttClient();
                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(
                        string.IsNullOrEmpty(vCfg.BrokerAddress) ? "127.0.0.1" : vCfg.BrokerAddress,
                        vCfg.BrokerPort > 0 ? vCfg.BrokerPort : 1883)
                    .WithClientId("SXSPD_" + Guid.NewGuid().ToString("N").Substring(0, 6))
                    .WithCleanSession(true)
                    .Build();
                await _mqtt.ConnectAsync(opts);
                _connected = true;
            }
            catch (Exception ex)
            {
                Log("MQTT connect error: " + ex.Message);
                return;
            }

            _timer = new System.Timers.Timer { Interval = IntervalMs, AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();
            IsRunning = true;
            Log("Iniciado · topic=" + Topic + " · intervalo=" + IntervalMs + "ms");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_mqtt != null)
            {
                var m = _mqtt; _mqtt = null;
                Task.Run(() =>
                {
                    try { m.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build()).Wait(2000); m.Dispose(); }
                    catch { }
                });
            }
        }

        private async void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed || _mqtt == null || !_connected) return;
            try
            {
                AogStateSnapshot snap = null;
                try { snap = _state.GetSnapshot(); } catch { }
                if (snap == null) return;
                if (!snap.IsJobStarted) return;  // sin lote abierto no hay nada que reportar
                if (snap.SectionSpeedsKmh == null || snap.SectionSpeedsKmh.Length == 0) return;

                string payload = BuildPayload(snap);
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(Topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .Build();
                await _mqtt.PublishAsync(msg);
                MessagesSent++;
            }
            catch { /* nunca romper el timer */ }
        }

        // Payload JSON manual para evitar dependencia de System.Text.Json en hot path
        // y mantener compatibilidad con consumidores ESP32 que parsean con ArduinoJson.
        // Formato:
        // { "ts":<ms>, "avg_kmh":8.4, "tool_left_kmh":9.2, "tool_right_kmh":7.6,
        //   "sections":[{"i":0,"kmh":9.2,"rev":false}, ...] }
        private static string BuildPayload(AogStateSnapshot snap)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append("{\"ts\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            sb.Append(",\"avg_kmh\":").Append(snap.AvgSpeed.ToString("F2", inv));
            sb.Append(",\"tool_left_kmh\":").Append(snap.ToolFarLeftSpeedKmh.ToString("F2", inv));
            sb.Append(",\"tool_right_kmh\":").Append(snap.ToolFarRightSpeedKmh.ToString("F2", inv));
            sb.Append(",\"sections\":[");
            var sp = snap.SectionSpeedsKmh;
            for (int i = 0; i < sp.Length; i++)
            {
                if (i > 0) sb.Append(',');
                double v = sp[i];
                sb.Append("{\"i\":").Append(i)
                  .Append(",\"kmh\":").Append(v.ToString("F2", inv))
                  .Append(",\"rev\":").Append(v < 0 ? "true" : "false")
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
    }
}
