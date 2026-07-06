// ============================================================================
// CoreXConfigController.cs — Endpoints de configuración de CoreX (web UI).
// Toda lectura/escritura de FormLoop se hace vía RunOnUiAsync porque los
// SerialPort y Settings no son thread-safe.
//
// Rutas:
//   GET  /api/corex/config/serial        → lista puertos disponibles + estado por canal
//   POST /api/corex/serial/open          → body {channel, port, baud} → {ok}
//   POST /api/corex/serial/close         → body {channel}             → {ok:true}
//   GET  /api/corex/config/ntrip         → configuración NTRIP actual
//   POST /api/corex/config/ntrip         → guarda config; {ok, restart}
//   GET  /api/corex/config/red           → estado UDP + subnet + IP local
//   POST /api/corex/config/red/udp       → cambia UDP on/off; siempre reinicia
//   POST /api/corex/config/red/subnet    → envía subnet a módulos; no reinicia
// ============================================================================

using System;
using System.IO.Ports;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    /// <summary>
    /// Configuración de puertos serie de CoreX: espejo web de los diálogos
    /// WinForms FormCommSetGPS/FormCommSetModule.
    /// </summary>
    public sealed class CoreXConfigController : AgpControllerBase
    {
        private readonly FormLoop _form;

        public CoreXConfigController(FormLoop form)
        {
            _form = form;
        }

        // ── GET /api/corex/config/serial ──────────────────────────────────────
        // Devuelve los puertos COM disponibles en el sistema y el estado actual
        // de cada uno de los 6 canales serie. Se ejecuta en el hilo UI para
        // leer los SerialPort.IsOpen de forma segura.
        [Route(HttpVerbs.Get, "/corex/config/serial")]
        public async Task GetSerial()
        {
            var data = await _form.RunOnUiAsync(() =>
            {
                return new
                {
                    Ok = true,
                    PortsAvailable = SerialPort.GetPortNames(),
                    Channels = new
                    {
                        Gps = new
                        {
                            Port    = FormLoop.portNameGPS,
                            Baud    = FormLoop.baudRateGPS,
                            IsOpen  = _form.spGPS.IsOpen,
                        },
                        Gps2 = new
                        {
                            Port    = FormLoop.portNameGPS2,
                            Baud    = FormLoop.baudRateGPS2,
                            IsOpen  = _form.spGPS2.IsOpen,
                        },
                        Rtcm = new
                        {
                            Port    = FormLoop.portNameRtcm,
                            Baud    = FormLoop.baudRateRtcm,
                            IsOpen  = _form.spRtcm.IsOpen,
                        },
                        Imu = new
                        {
                            Port    = FormLoop.portNameIMU,
                            Baud    = FormLoop.baudRateIMU,
                            IsOpen  = _form.spIMU.IsOpen,
                        },
                        Steer = new
                        {
                            Port    = FormLoop.portNameSteerModule,
                            Baud    = FormLoop.baudRateSteerModule,
                            IsOpen  = _form.spSteerModule.IsOpen,
                        },
                        Machine = new
                        {
                            Port    = FormLoop.portNameMachineModule,
                            Baud    = FormLoop.baudRateMachineModule,
                            IsOpen  = _form.spMachineModule.IsOpen,
                        },
                    },
                };
            }).ConfigureAwait(false);

            await WriteJsonAsync(data).ConfigureAwait(false);
        }

        // ── POST /api/corex/serial/open ───────────────────────────────────────
        // Body: { "channel": "gps", "port": "COM3", "baud": 115200 }
        // AgpJson deserializa con SnakeCaseLower, así que el body snake_case
        // del JS se mapea a propiedades snake_case del DTO de entrada.
        [Route(HttpVerbs.Post, "/corex/serial/open")]
        public async Task OpenSerial()
        {
            var req = await ReadJsonBodyAsync<SerialOpenRequest>().ConfigureAwait(false);
            if (req == null || string.IsNullOrEmpty(req.Channel))
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Falta channel, port o baud").ConfigureAwait(false);
                return;
            }

            // Canal desconocido u otro argumento inválido: respondemos 400 JSON
            // en vez de dejar que EmbedIO tire un 500 de texto plano.
            bool opened;
            try
            {
                opened = await _form.RunOnUiAsync(
                    () => _form.OpenSerialFromWeb(req.Channel, req.Port ?? "", req.Baud)
                ).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteErrorAsync(400, "BAD_CHANNEL", ex.Message).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(new { Ok = opened }).ConfigureAwait(false);
        }

        // ── POST /api/corex/serial/close ──────────────────────────────────────
        // Body: { "channel": "gps" }
        [Route(HttpVerbs.Post, "/corex/serial/close")]
        public async Task CloseSerial()
        {
            var req = await ReadJsonBodyAsync<SerialCloseRequest>().ConfigureAwait(false);
            if (req == null || string.IsNullOrEmpty(req.Channel))
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Falta channel").ConfigureAwait(false);
                return;
            }

            try
            {
                await _form.RunOnUiAsync<bool>(() =>
                {
                    _form.CloseSerialFromWeb(req.Channel);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteErrorAsync(400, "BAD_CHANNEL", ex.Message).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(new { Ok = true }).ConfigureAwait(false);
        }

        // ── GET /api/corex/config/ntrip ───────────────────────────────────────
        // Devuelve la configuración NTRIP actual desde Settings. Se ejecuta en
        // el hilo UI para leer Settings de forma segura.
        [Route(HttpVerbs.Get, "/corex/config/ntrip")]
        public async Task GetNtrip()
        {
            var data = await _form.RunOnUiAsync(() =>
            {
                var s = Properties.Settings.Default;
                return new NtripConfigDto
                {
                    IsOn            = s.setNTRIP_isOn,
                    CasterUrl       = s.setNTRIP_casterURL   ?? "",
                    CasterIp        = s.setNTRIP_casterIP    ?? "",
                    CasterPort      = s.setNTRIP_casterPort,
                    Mount           = s.setNTRIP_mount        ?? "",
                    UserName        = s.setNTRIP_userName     ?? "",
                    UserPassword    = s.setNTRIP_userPassword ?? "",
                    SendGgaInterval = s.setNTRIP_sendGGAInterval,
                    IsGgaManual     = s.setNTRIP_isGGAManual,
                    ManualLat       = s.setNTRIP_manualLat,
                    ManualLon       = s.setNTRIP_manualLon,
                    IsTcp           = s.setNTRIP_isTCP,
                    IsHttp10        = s.setNTRIP_isHTTP10,
                    PacketSize      = s.setNTRIP_packetSize,
                    SendToSerial    = s.setNTRIP_sendToSerial,
                    SendToUdp       = s.setNTRIP_sendToUDP,
                    SendToUdpPort   = s.setNTRIP_sendToUDPPort,
                };
            }).ConfigureAwait(false);

            await WriteJsonAsync(data).ConfigureAwait(false);
        }

        // ── POST /api/corex/config/ntrip ──────────────────────────────────────
        // Guarda la configuración NTRIP. Responde {ok, restart}.
        // Si restart=true, inicia un reinicio diferido de CoreX.
        [Route(HttpVerbs.Post, "/corex/config/ntrip")]
        public async Task PostNtrip()
        {
            var dto = await ReadJsonBodyAsync<NtripConfigDto>().ConfigureAwait(false);
            if (dto == null)
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Body requerido").ConfigureAwait(false);
                return;
            }

            // Validar IP del caster si no está vacía (misma lógica que FormNtrip.CheckIPValid)
            if (!string.IsNullOrEmpty(dto.CasterIp) && !CheckCasterIpValid(dto.CasterIp))
            {
                await WriteErrorAsync(400, "AGP-CFG-002", "IP del caster inválida", dto.CasterIp)
                    .ConfigureAwait(false);
                return;
            }

            bool restart = await _form.RunOnUiAsync(
                () => _form.SaveNtripConfigFromWeb(dto)
            ).ConfigureAwait(false);

            await WriteJsonAsync(new { Ok = true, Restart = restart }).ConfigureAwait(false);

            // Reinicio diferido: la respuesta HTTP ya salió antes de esta línea.
            if (restart)
            {
                await _form.RunOnUiAsync<object>(() =>
                {
                    _form.RestartFromWeb();
                    return null;
                }).ConfigureAwait(false);
            }
        }

        // ── GET /api/corex/config/red ─────────────────────────────────────────
        // Devuelve estado UDP on/off, subnet configurada e IP local del host.
        [Route(HttpVerbs.Get, "/corex/config/red")]
        public async Task GetRed()
        {
            var data = await _form.RunOnUiAsync(() =>
            {
                var s = Properties.Settings.Default;
                return new
                {
                    UdpIsOn  = s.setUDP_isOn,
                    Subnet   = new int[] { s.etIP_SubnetOne, s.etIP_SubnetTwo, s.etIP_SubnetThree },
                    IpActual = _form.GetLocalIpForWeb(),
                };
            }).ConfigureAwait(false);

            await WriteJsonAsync(data).ConfigureAwait(false);
        }

        // ── POST /api/corex/config/red/udp ───────────────────────────────────
        // Body: { "on": true/false }. Guarda y reinicia CoreX (siempre).
        [Route(HttpVerbs.Post, "/corex/config/red/udp")]
        public async Task PostRedUdp()
        {
            var req = await ReadJsonBodyAsync<UdpOnOffRequest>().ConfigureAwait(false);
            if (req == null)
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Body requerido").ConfigureAwait(false);
                return;
            }

            // SetUdpOnOffFromWeb ya llama RestartFromWeb() internamente (timer 800 ms).
            // La respuesta HTTP sale antes de que el proceso termine.
            await _form.RunOnUiAsync<object>(() =>
            {
                _form.SetUdpOnOffFromWeb(req.On);
                return null;
            }).ConfigureAwait(false);

            await WriteJsonAsync(new { Ok = true, Restart = true }).ConfigureAwait(false);
        }

        // ── POST /api/corex/config/red/subnet ────────────────────────────────
        // Body: { "o1": 192, "o2": 168, "o3": 5 }. Envía subnet a módulos;
        // NO reinicia CoreX.
        [Route(HttpVerbs.Post, "/corex/config/red/subnet")]
        public async Task PostRedSubnet()
        {
            var req = await ReadJsonBodyAsync<SubnetRequest>().ConfigureAwait(false);
            if (req == null)
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Body requerido").ConfigureAwait(false);
                return;
            }

            try
            {
                await _form.RunOnUiAsync<object>(() =>
                {
                    _form.SendSubnetFromWeb(req.O1, req.O2, req.O3);
                    return null;
                }).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteErrorAsync(400, "BAD_REQUEST", ex.Message).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(new { Ok = true, Restart = false }).ConfigureAwait(false);
        }

        // Valida formato IPv4: 4 octetos, cada uno 0-255, máx 3 dígitos.
        // Acepta también strings con "COM" (puerto serie directo, igual que el form).
        private static bool CheckCasterIpValid(string ip)
        {
            if (ip.Contains("COM")) return true;
            var parts = ip.Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 3) return false;
                if (!int.TryParse(part, out int val)) return false;
                if (val < 0 || val > 255) return false;
            }
            return true;
        }

        // ── DTO de configuración NTRIP ────────────────────────────────────────
        // Anidado en CoreXConfigController para que FormLoop pueda referenciarlo
        // como CoreXConfigController.NtripConfigDto sin namespace adicional.
        // public: FormLoop.SaveNtripConfigFromWeb lo recibe directamente.
        // [JsonPropertyName] explícito en cada prop para que la deserialización
        // del body JS snake_case funcione sin ambigüedad.
        public sealed class NtripConfigDto
        {
            [JsonPropertyName("is_on")]              public bool   IsOn            { get; set; }
            [JsonPropertyName("caster_url")]         public string CasterUrl       { get; set; }
            [JsonPropertyName("caster_ip")]          public string CasterIp        { get; set; }
            [JsonPropertyName("caster_port")]        public int    CasterPort      { get; set; }
            [JsonPropertyName("mount")]              public string Mount           { get; set; }
            [JsonPropertyName("user_name")]          public string UserName        { get; set; }
            [JsonPropertyName("user_password")]      public string UserPassword    { get; set; }
            [JsonPropertyName("send_gga_interval")]  public int    SendGgaInterval { get; set; }
            [JsonPropertyName("is_gga_manual")]      public bool   IsGgaManual     { get; set; }
            [JsonPropertyName("manual_lat")]         public double ManualLat       { get; set; }
            [JsonPropertyName("manual_lon")]         public double ManualLon       { get; set; }
            [JsonPropertyName("is_tcp")]             public bool   IsTcp           { get; set; }
            [JsonPropertyName("is_http10")]          public bool   IsHttp10        { get; set; }
            [JsonPropertyName("packet_size")]        public int    PacketSize      { get; set; }
            [JsonPropertyName("send_to_serial")]     public bool   SendToSerial    { get; set; }
            [JsonPropertyName("send_to_udp")]        public bool   SendToUdp       { get; set; }
            [JsonPropertyName("send_to_udp_port")]   public int    SendToUdpPort   { get; set; }
        }
    }

    // ── DTOs de entrada de puertos serie ─────────────────────────────────────
    // AgpJson usa SnakeCaseLower como NamingPolicy de escritura Y lectura
    // (PropertyNameCaseInsensitive = true, pero eso solo cubre mayúsculas/
    // minúsculas, no guiones bajos). Para garantizar que el body JS snake_case
    // deserialice correctamente, los DTOs usan [JsonPropertyName] explícito.

    internal sealed class SerialOpenRequest
    {
        [JsonPropertyName("channel")] public string Channel { get; set; }
        [JsonPropertyName("port")]    public string Port    { get; set; }
        [JsonPropertyName("baud")]    public int    Baud    { get; set; }
    }

    internal sealed class SerialCloseRequest
    {
        [JsonPropertyName("channel")] public string Channel { get; set; }
    }

    // ── DTOs de red UDP ───────────────────────────────────────────────────────

    internal sealed class UdpOnOffRequest
    {
        [JsonPropertyName("on")] public bool On { get; set; }
    }

    internal sealed class SubnetRequest
    {
        [JsonPropertyName("o1")] public byte O1 { get; set; }
        [JsonPropertyName("o2")] public byte O2 { get; set; }
        [JsonPropertyName("o3")] public byte O3 { get; set; }
    }
}
