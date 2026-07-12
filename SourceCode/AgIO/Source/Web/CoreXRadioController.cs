// ============================================================================
// CoreXRadioController.cs — Radio RTCM de CoreX vía web (port de FormRadio).
//
// Endpoints (todos bajo /api):
//   GET  /api/corex/config/radio  → config + lista de canales con distancia.
//   POST /api/corex/config/radio  → guarda config y canales; aplica en caliente
//                                   (ConfigureNTRIP). Radio ON apaga NTRIP y
//                                   serial-pass (fuentes RTCM excluyentes).
//   POST /api/corex/radio/comando → body {texto} → {ok, respuesta}. Manda un
//                                   comando a la radio (sintonizar = SL&F=freq).
//
// Todo el acceso a Settings/spRadio pasa por RunOnUiAsync (hilo UI).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    public sealed class CoreXRadioController : AgpControllerBase
    {
        private readonly FormLoop _form;

        public CoreXRadioController(FormLoop form)
        {
            _form = form;
        }

        // ── GET /api/corex/config/radio ───────────────────────────────────────
        [Route(HttpVerbs.Get, "/corex/config/radio")]
        public async Task GetConfig()
        {
            var dto = await _form.RunOnUiAsync(() => _form.GetRadioConfigForWeb())
                .ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        // ── POST /api/corex/config/radio ──────────────────────────────────────
        [Route(HttpVerbs.Post, "/corex/config/radio")]
        public async Task SaveConfig()
        {
            var req = await ReadJsonBodyAsync<RadioConfigSaveRequest>().ConfigureAwait(false);
            if (req == null)
            {
                await WriteErrorAsync(400, "bad_request", "Body inválido.").ConfigureAwait(false);
                return;
            }

            string error = await _form.RunOnUiAsync(() => _form.SaveRadioConfigFromWeb(req))
                .ConfigureAwait(false);

            if (error != null)
            {
                await WriteErrorAsync(400, "validacion", error).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }

        // ── POST /api/corex/radio/comando ─────────────────────────────────────
        [Route(HttpVerbs.Post, "/corex/radio/comando")]
        public async Task Comando()
        {
            var req = await ReadJsonBodyAsync<RadioComandoRequest>().ConfigureAwait(false);
            if (req == null || string.IsNullOrWhiteSpace(req.Texto))
            {
                await WriteErrorAsync(400, "bad_request", "Falta el comando.").ConfigureAwait(false);
                return;
            }

            try
            {
                string respuesta = await _form
                    .RunOnUiAsync(() => _form.SendRadioCommandFromWeb(req.Texto.Trim()))
                    .ConfigureAwait(false);
                await WriteJsonAsync(new ComandoResponse { Respuesta = respuesta })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(500, "radio", "No se pudo hablar con la radio.",
                    ex.Message).ConfigureAwait(false);
            }
        }

        // ── GET /api/corex/config/pass ────────────────────────────────────────
        // Paso serial RTCM (port de FormSerialPass). Comparte puerto/baud con
        // la radio (mismos settings).
        [Route(HttpVerbs.Get, "/corex/config/pass")]
        public async Task GetPass()
        {
            var dto = await _form.RunOnUiAsync(() =>
            {
                var s = AgIO.Properties.Settings.Default;
                return new
                {
                    Ok = true,
                    IsOn = s.setPass_isOn,
                    Port = s.setPort_portNameRadio ?? "",
                    Baud = s.setPort_baudRateRadio ?? "9600",
                    SendToSerial = s.setNTRIP_sendToSerial,
                    SendToUdp = s.setNTRIP_sendToUDP,
                    SendToUdpPort = s.setNTRIP_sendToUDPPort,
                };
            }).ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        // ── POST /api/corex/config/pass ───────────────────────────────────────
        // Guarda y SIEMPRE reinicia CoreX (igual que el form). Pass ON apaga
        // NTRIP y radio.
        [Route(HttpVerbs.Post, "/corex/config/pass")]
        public async Task SavePass()
        {
            var req = await ReadJsonBodyAsync<PassConfigSaveRequest>().ConfigureAwait(false);
            if (req == null)
            {
                await WriteErrorAsync(400, "bad_request", "Body inválido.").ConfigureAwait(false);
                return;
            }

            if (req.IsOn && string.IsNullOrEmpty(req.Port))
            {
                await WriteErrorAsync(400, "validacion",
                    "El paso serial está encendido pero no hay puerto elegido.")
                    .ConfigureAwait(false);
                return;
            }

            // SaveSerialPassFromWeb llama RestartFromWeb() (timer 800 ms): la
            // respuesta HTTP sale antes de que el proceso termine.
            await _form.RunOnUiAsync<object>(() =>
            {
                _form.SaveSerialPassFromWeb(req.IsOn, req.Port, req.Baud,
                    req.SendToSerial, req.SendToUdp, req.SendToUdpPort);
                return null;
            }).ConfigureAwait(false);

            await WriteJsonAsync(new { ok = true, restart = true }).ConfigureAwait(false);
        }

        // AgpJson serializa a snake_case (IsOn→is_on, DistanceKm→distance_km…).
        public sealed class RadioConfigDto
        {
            public bool Ok { get; set; } = true;
            public bool IsOn { get; set; }
            public string Port { get; set; } = "";
            public string Baud { get; set; } = "";
            public string Channel { get; set; } = "";
            public bool PortOpen { get; set; }
            public List<string> AvailablePorts { get; set; } = new List<string>();
            public List<RadioChannelDto> Channels { get; set; } = new List<RadioChannelDto>();
        }

        // RadioChannelDto viaja en las DOS direcciones → [JsonPropertyName]
        // explícito (la deserialización del body snake_case lo requiere).
        public sealed class RadioChannelDto
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("frequency")] public string Frequency { get; set; } = "";
            [JsonPropertyName("location")] public string Location { get; set; } = "";
            // -1 = sin ubicación o sin fix GPS.
            [JsonPropertyName("distance_km")] public double DistanceKm { get; set; } = -1;
        }

        public sealed class RadioConfigSaveRequest
        {
            [JsonPropertyName("is_on")] public bool IsOn { get; set; }
            [JsonPropertyName("port")] public string Port { get; set; }
            [JsonPropertyName("baud")] public string Baud { get; set; }
            [JsonPropertyName("channel")] public string Channel { get; set; }
            [JsonPropertyName("channels")] public List<RadioChannelDto> Channels { get; set; }
        }

        internal sealed class RadioComandoRequest
        {
            [JsonPropertyName("texto")] public string Texto { get; set; }
        }

        internal sealed class PassConfigSaveRequest
        {
            [JsonPropertyName("is_on")] public bool IsOn { get; set; }
            [JsonPropertyName("port")] public string Port { get; set; }
            [JsonPropertyName("baud")] public string Baud { get; set; }
            [JsonPropertyName("send_to_serial")] public bool SendToSerial { get; set; }
            [JsonPropertyName("send_to_udp")] public bool SendToUdp { get; set; }
            [JsonPropertyName("send_to_udp_port")] public int SendToUdpPort { get; set; }
        }

        public sealed class ComandoResponse
        {
            public bool Ok { get; set; } = true;
            public string Respuesta { get; set; } = "";
        }
    }
}
