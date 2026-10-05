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

        /// <summary>Cómo se resolvió la última IP, para el log del OTA. Lo que
        /// costó horas en Las Gringas (2026-09-25) fue justamente no saber POR QUÉ
        /// la pantalla había elegido la IP que eligió.</summary>
        public static string UltimaResolucion { get; private set; } = "(sin resolver)";

        /// <summary>
        /// IP del PC que ESTE nodo tiene que poder alcanzar, elegida por SUBRED.
        ///
        /// Por qué existe (Las Gringas, 2026-09-25): la tablet del tractor tiene
        /// DOS redes —el hotspot de datos (10.140.241.x) y la Ethernet de la
        /// sembradora (192.168.5.10)— y ResolveLanIp devolvía la PRIMERA que
        /// encontraba, que resultó ser la del hotspot. El nodo recibía una URL
        /// que no podía alcanzar, la descarga moría con http_-1 y el nodo
        /// terminaba en panic → safe_mode → rechazaba la OTA siguiente. Hubo que
        /// falsear el broker_address de VistaX para desempatar a mano.
        ///
        /// El nodo sabe su propia IP y la publica en el announcement. Con eso la
        /// elección deja de ser una adivinanza: de todas las IP del PC, la que
        /// sirve es la que está en la MISMA SUBRED que el nodo. Si ninguna lo
        /// está (el nodo está detrás de un router, o no sabemos su IP), se cae al
        /// comportamiento de antes.
        /// </summary>
        public static string ResolveLanIpPara(string ipDelNodo, string fallback = null)
        {
            try
            {
                string porSubred = ElegirPorSubred(IpsLocales(), ipDelNodo);
                if (!string.IsNullOrEmpty(porSubred))
                {
                    UltimaResolucion = "subred del nodo " + ipDelNodo + " → " + porSubred;
                    return porSubred;
                }
            }
            catch { } // silencioso a propósito: si la enumeración de NICs falla, queda el camino viejo

            string vieja = ResolveLanIp(fallback);
            UltimaResolucion = string.IsNullOrEmpty(ipDelNodo)
                ? "sin IP del nodo → " + vieja
                : "ninguna NIC comparte subred con " + ipDelNodo + " → " + vieja;
            return vieja;
        }

        /// <summary>IPv4 del equipo con su prefijo de red, una por NIC activa.</summary>
        private static List<KeyValuePair<string, int>> IpsLocales()
        {
            var res = new List<KeyValuePair<string, int>>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (System.Net.IPAddress.IsLoopback(ua.Address)) continue;

                    // PrefixLength es lo portable; IPv4Mask tira
                    // PlatformNotSupportedException en algunos runtimes.
                    int prefijo = 24;
                    try
                    {
                        if (ua.PrefixLength > 0 && ua.PrefixLength <= 32) prefijo = ua.PrefixLength;
                        else if (ua.IPv4Mask != null) prefijo = PrefijoDeMascara(ua.IPv4Mask);
                    }
                    catch { } // silencioso a propósito: sin máscara se asume /24, que cubre la LAN del tractor

                    res.Add(new KeyValuePair<string, int>(ua.Address.ToString(), prefijo));
                }
            }
            return res;
        }

        private static int PrefijoDeMascara(System.Net.IPAddress mascara)
        {
            var b = mascara.GetAddressBytes();
            int bits = 0;
            foreach (byte x in b)
            {
                byte v = x;
                while (v != 0) { bits += v & 1; v >>= 1; }
            }
            return bits;
        }

        /// <summary>
        /// De las IP locales, la que comparte subred con el destino. null si
        /// ninguna. Cuando hay varias candidatas gana la de prefijo MÁS LARGO:
        /// la red más específica es la que de verdad llega al nodo.
        /// </summary>
        internal static string ElegirPorSubred(List<KeyValuePair<string, int>> locales, string ipDestino)
        {
            System.Net.IPAddress destino;
            if (string.IsNullOrWhiteSpace(ipDestino)) return null;
            if (!System.Net.IPAddress.TryParse(ipDestino.Trim(), out destino)) return null;
            if (destino.AddressFamily != AddressFamily.InterNetwork) return null;
            if (locales == null) return null;

            uint d = ATreintaYDos(destino);
            string mejor = null;
            int mejorPrefijo = -1;

            foreach (var kv in locales)
            {
                System.Net.IPAddress local;
                if (!System.Net.IPAddress.TryParse(kv.Key ?? "", out local)) continue;
                if (local.AddressFamily != AddressFamily.InterNetwork) continue;

                int prefijo = kv.Value;
                if (prefijo <= 0 || prefijo > 32) continue;

                uint mascara = prefijo == 32 ? 0xFFFFFFFFu : ~((1u << (32 - prefijo)) - 1u);
                uint l = ATreintaYDos(local);
                if ((l & mascara) != (d & mascara)) continue;

                if (prefijo > mejorPrefijo) { mejorPrefijo = prefijo; mejor = kv.Key; }
            }
            return mejor;
        }

        private static uint ATreintaYDos(System.Net.IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

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
            catch { } // silencioso a propósito: auto-detección best-effort de IP LAN

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
            catch { } // silencioso a propósito: auto-detección best-effort de IP LAN

            return fallback ?? "127.0.0.1";
        }

        // Puerto del WebHost EmbedIO (AgpWebHost). EmbedIO bindea por Sockets en
        // 0.0.0.0:5180 sin requerir URL ACL — eso garantiza acceso desde LAN sin
        // admin, a diferencia del antiguo FirmwareLanServer (HttpListener).
        // Si en el futuro AgpWebHost cambia de puerto, sincronizar este valor.
        public const int EmbedIoFirmwarePort = 5180;

        // Construye la URL HTTP que el ESP32 usa para bajar el .bin.
        // El parámetro httpPort queda por compatibilidad con callers viejos pero
        // se ignora — siempre se sirve desde el endpoint EmbedIO en :5180
        // (/api/firmwares/{producto}/{version}/firmware.bin), que es accesible
        // desde LAN sin URL ACL.
        public static string BuildFirmwareUrl(string producto, string version, int httpPort)
            => BuildFirmwareUrlPara(null, producto, version, httpPort);

        /// <summary>Igual que BuildFirmwareUrl pero eligiendo la IP por la subred
        /// del nodo que va a bajar el .bin. Es la que hay que usar siempre que se
        /// sepa a quién se le está mandando el OTA.</summary>
        public static string BuildFirmwareUrlPara(string ipDelNodo, string producto, string version, int httpPort)
        {
            string ip = ResolveLanIpPara(ipDelNodo);
            return $"http://{ip}:{EmbedIoFirmwarePort}/api/firmwares/{Uri.EscapeDataString(producto)}/{Uri.EscapeDataString(version)}/firmware.bin";
        }

        private void Log(string msg) => OnLog?.Invoke(this, msg);

        public void Dispose()
        {
            try { _mqtt?.DisconnectAsync().Wait(500); } catch { } // silencioso a propósito: Disconnect MQTT en Dispose
            try { _mqtt?.Dispose(); } catch { } // silencioso a propósito: Dispose MQTT en shutdown
            _mqtt = null;
            _connected = false;
        }
    }
}
