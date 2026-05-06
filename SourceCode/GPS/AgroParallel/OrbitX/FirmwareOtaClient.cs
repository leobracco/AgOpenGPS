// ============================================================================
// FirmwareOtaClient.cs - Cliente MQTT para disparar OTA en nodos ESP32
//
// Patrón unificado AgroParallel para TODOS los nodos:
//   PC→ESP   topic: agp/{producto}/{UID}/cmd
//            payload: {"cmd":"ota","url":"http://<LAN>:8088/firmware/...","version":"x.y.z"}
//   ESP→PC   topic: agp/{producto}/{UID}/ota/resultado
//            payload: {"uid":"...","status":"iniciando|ok|error","version":"...","detalle":"..."}
//
// VistaX firmware ≥ v2.4 también acepta agp/vistax/{UID}/cmd (convive con
// vistax/nodos/comando/<UID> legacy del propio vistax-server para reiniciar/borrar_wifi).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.OrbitX
{
    public class FirmwareOtaProgress
    {
        public string Uid { get; set; }
        public string Producto { get; set; }
        public string Status { get; set; }   // iniciando | ok | error
        public string Version { get; set; }
        public string Detalle { get; set; }
        public DateTime Ts { get; set; }
    }

    public class FirmwareOtaClient : IDisposable
    {
        private IMqttClient _mqtt;
        private bool _connected;

        public event EventHandler<FirmwareOtaProgress> OnProgress;
        public event EventHandler<string> OnLog;

        // ── Topics por producto (unificado para todos los nodos) ────────────
        private static (string cmdTopic, string resultTopic, string resultFilter)
            TopicsFor(string producto, string uid)
        {
            string pl = (producto ?? "").Trim().ToLowerInvariant();
            return (
                cmdTopic:     $"agp/{pl}/{uid}/cmd",
                resultTopic:  $"agp/{pl}/{uid}/ota/resultado",
                resultFilter: $"agp/{pl}/+/ota/resultado");
        }

        // ── Connect ─────────────────────────────────────────────────────────
        public async Task ConnectAsync()
        {
            if (_connected) return;

            var vistaXCfg = VistaXConfig.Load();
            string broker = string.IsNullOrEmpty(vistaXCfg.BrokerAddress) ? "127.0.0.1" : vistaXCfg.BrokerAddress;
            int port      = vistaXCfg.BrokerPort > 0 ? vistaXCfg.BrokerPort : 1883;

            var factory = new MqttFactory();
            _mqtt = factory.CreateMqttClient();

            var opts = new MqttClientOptionsBuilder()
                .WithTcpServer(broker, port)
                .WithClientId("AOG_OTA_" + Guid.NewGuid().ToString("N").Substring(0, 6))
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .Build();

            _mqtt.ApplicationMessageReceivedAsync += OnMessageAsync;
            await _mqtt.ConnectAsync(opts);
            _connected = true;

            Log($"OTA MQTT conectado a {broker}:{port}");
        }

        // ── Disparar OTA en un nodo ─────────────────────────────────────────
        public async Task<bool> SendOtaAsync(string producto, string uid, string url, string version)
        {
            if (!_connected) await ConnectAsync();
            var t = TopicsFor(producto, uid);

            // Suscribirse al topic de resultado (idempotente).
            await _mqtt.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(t.resultFilter).Build());

            // Payload común a todos los productos.
            var payload = new Dictionary<string, object>
            {
                { "cmd",     "ota" },
                { "url",     url },
                { "version", version },
            };
            string json = JsonSerializer.Serialize(payload);

            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(t.cmdTopic)
                    .WithPayload(Encoding.UTF8.GetBytes(json))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                await _mqtt.PublishAsync(msg);
                Log($"OTA → {t.cmdTopic}  v{version}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"OTA publish error: {ex.Message}");
                return false;
            }
        }

        // ── Recepción de resultado ──────────────────────────────────────────
        private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                string topic = e.ApplicationMessage.Topic ?? "";
                var seg = e.ApplicationMessage.PayloadSegment;
                string json = Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string producto = "";
                string uid = "";

                // Parsear topic agp/{producto}/{UID}/ota/resultado
                var parts = topic.Split('/');
                if (parts.Length >= 5 && parts[0] == "agp" && parts[3] == "ota" && parts[4] == "resultado")
                {
                    producto = parts[1];
                    uid = parts[2];
                }
                else
                {
                    return Task.CompletedTask;
                }

                var p = new FirmwareOtaProgress
                {
                    Producto = producto,
                    Uid      = uid,
                    Status   = root.TryGetProperty("status",  out var s) ? s.GetString()  : "",
                    Version  = root.TryGetProperty("version", out var v) ? v.GetString()  : "",
                    Detalle  = root.TryGetProperty("detalle", out var d) ? d.GetString()  : "",
                    Ts       = DateTime.Now,
                };
                OnProgress?.Invoke(this, p);
            }
            catch (Exception ex)
            {
                Log($"OTA result parse: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        // ── LAN IP del PC para armar la URL del firmware HTTP ───────────────
        public static string ResolveLanIp(string fallback = null)
        {
            // 1) Si hay broker configurado distinto de loopback, usarlo (es el mismo PC).
            try
            {
                var cfg = VistaXConfig.Load();
                string b = (cfg.BrokerAddress ?? "").Trim();
                if (!string.IsNullOrEmpty(b) && b != "127.0.0.1" && b != "localhost"
                    && System.Net.IPAddress.TryParse(b, out _))
                    return b;
            }
            catch { }

            // 2) Auto-detectar la primera IPv4 no-loopback.
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork
                            && !System.Net.IPAddress.IsLoopback(ua.Address))
                            return ua.Address.ToString();
                    }
                }
            }
            catch { }

            return fallback ?? "127.0.0.1";
        }

        public static string BuildFirmwareUrl(string producto, string version, int httpPort)
        {
            string ip = ResolveLanIp();
            return $"http://{ip}:{httpPort}/firmware/{Uri.EscapeDataString(producto)}/{Uri.EscapeDataString(version)}/firmware.bin";
        }

        private void Log(string msg) => OnLog?.Invoke(this, msg);

        public void Dispose()
        {
            try { _mqtt?.DisconnectAsync().Wait(500); } catch { }
            try { _mqtt?.Dispose(); } catch { }
            _mqtt = null;
            _connected = false;
        }
    }
}
