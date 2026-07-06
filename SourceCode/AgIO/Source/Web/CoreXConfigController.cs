// ============================================================================
// CoreXConfigController.cs — Endpoints de configuración de puertos serie
// para la UI web de CoreX. Toda lectura/escritura de FormLoop se hace vía
// RunOnUiAsync porque los SerialPort y Settings no son thread-safe.
//
// Rutas:
//   GET  /api/corex/config/serial   → lista puertos disponibles + estado por canal
//   POST /api/corex/serial/open     → body {channel, port, baud} → {ok}
//   POST /api/corex/serial/close    → body {channel}             → {ok:true}
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

            bool opened = await _form.RunOnUiAsync(
                () => _form.OpenSerialFromWeb(req.Channel, req.Port ?? "", req.Baud)
            ).ConfigureAwait(false);

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

            await _form.RunOnUiAsync<bool>(() =>
            {
                _form.CloseSerialFromWeb(req.Channel);
                return true;
            }).ConfigureAwait(false);

            await WriteJsonAsync(new { Ok = true }).ConfigureAwait(false);
        }
    }

    // ── DTOs de entrada ───────────────────────────────────────────────────────
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
}
