// ============================================================================
// CoreXDiagController.cs — Diagnóstico de CoreX vía web.
//
// Endpoints (todos bajo /api):
//   GET /api/corex/gps     → detalle GPS/IMU + sentencias NMEA del snapshot.
//                            Cada request renueva el keep-alive de captura de
//                            sentencias (a los 5 s sin polling se apaga sola).
//   GET /api/corex/eventos → log de eventos: histórico en disco + buffer de
//                            la sesión actual (port de FormEventViewer).
//   GET  /api/corex/monitor/udp        → drena el log de tráfico UDP/PGN
//                                        (port de FormUDPMonitor) + flags.
//   POST /api/corex/monitor/udp/flags  → body {log_nmea, log_ntrip}.
//   GET  /api/corex/monitor/gps        → drena el log crudo de sentencias
//                                        (port de FormSerialMonitor).
//
// Los tres monitores usan keep-alive: cada GET renueva la captura y a los
// 5 s sin polling el snapshot @1Hz la apaga y limpia el buffer.
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using AgLibrary.Logging;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    public sealed class CoreXDiagController : AgpControllerBase
    {
        private readonly FormLoop _form;

        public CoreXDiagController(FormLoop form)
        {
            _form = form;
        }

        // ── GET /api/corex/gps ────────────────────────────────────────────────
        // Devuelve el bloque gps del snapshot @1Hz. El campo nmea llega null
        // durante los primeros segundos (hasta que el keep-alive enciende la
        // captura y el próximo snapshot la incluye).
        [Route(HttpVerbs.Get, "/corex/gps")]
        public Task GpsDetail()
        {
            _form.KeepGpsSentencesAliveFromWeb();
            return WriteJsonAsync(new GpsDetailResponse
            {
                Gps = CoreXState.Instance.Snapshot().Gps,
            });
        }

        // ── GET /api/corex/eventos ────────────────────────────────────────────
        // Igual que FormEventViewer: archivo persistido + Log.sbEvents de la
        // sesión. El StringBuilder se snapshotea en el hilo UI (no es
        // thread-safe leerlo mientras el hilo UI le appendea).
        [Route(HttpVerbs.Get, "/corex/eventos")]
        public async Task Eventos()
        {
            string path = Path.Combine(RegistrySettings.logsDirectory, "AgIO_Events_Log.txt");

            // Solo la cola del archivo: el log crece a cientos de KB y al
            // operario le sirve lo reciente (el archivo completo queda en disco).
            const int maxBytes = 256 * 1024;
            string historico = "";
            try
            {
                if (File.Exists(path))
                {
                    using (var fs = new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite))
                    {
                        bool truncado = fs.Length > maxBytes;
                        if (truncado) fs.Seek(-maxBytes, SeekOrigin.End);
                        using (var sr = new StreamReader(fs))
                            historico = await sr.ReadToEndAsync().ConfigureAwait(false);

                        if (truncado)
                        {
                            // Arrancar en una línea completa.
                            int nl = historico.IndexOf('\n');
                            if (nl >= 0 && nl + 1 < historico.Length)
                                historico = historico.Substring(nl + 1);
                            historico = "(…mostrando el final del log; el archivo completo queda en "
                                + path + ")\n\n" + historico;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                historico = "(no se pudo leer el archivo de log: " + ex.Message + ")";
            }

            string sesion = await _form.RunOnUiAsync(() => Log.sbEvents.ToString())
                .ConfigureAwait(false);

            await WriteJsonAsync(new EventosResponse
            {
                Archivo = path,
                Historico = historico,
                Sesion = sesion,
            }).ConfigureAwait(false);
        }

        // ── GET /api/corex/monitor/udp ────────────────────────────────────────
        // Drena lo acumulado desde el último poll (igual que el timer1_Tick de
        // FormUDPMonitor). La primera respuesta llega vacía: la captura se
        // enciende recién en el próximo snapshot @1Hz.
        [Route(HttpVerbs.Get, "/corex/monitor/udp")]
        public async Task MonitorUdp()
        {
            _form.KeepUdpMonitorAliveFromWeb();
            var drain = await _form.RunOnUiAsync(() => _form.DrainUdpMonitorFromWeb())
                .ConfigureAwait(false);
            await WriteJsonAsync(drain).ConfigureAwait(false);
        }

        // ── POST /api/corex/monitor/udp/flags ─────────────────────────────────
        [Route(HttpVerbs.Post, "/corex/monitor/udp/flags")]
        public async Task MonitorUdpFlags()
        {
            var req = await ReadJsonBodyAsync<UdpMonitorFlagsRequest>().ConfigureAwait(false);
            await _form.RunOnUiAsync<object>(() =>
            {
                _form.SetUdpMonitorFlagsFromWeb(req.LogNmea, req.LogNtrip);
                return null;
            }).ConfigureAwait(false);
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }

        // ── GET /api/corex/monitor/gps ────────────────────────────────────────
        [Route(HttpVerbs.Get, "/corex/monitor/gps")]
        public async Task MonitorGpsRaw()
        {
            _form.KeepRawMonitorAliveFromWeb();
            string data = await _form.RunOnUiAsync(() => _form.DrainRawMonitorFromWeb())
                .ConfigureAwait(false);
            await WriteJsonAsync(new RawMonitorDrainDto { Data = data }).ConfigureAwait(false);
        }

        // AgpJson serializa a snake_case (Gps→gps, Historico→historico, …).
        public sealed class UdpMonitorDrainDto
        {
            public bool Ok { get; set; } = true;
            public string Data { get; set; } = "";
            public bool LogNmea { get; set; }
            public bool LogNtrip { get; set; }
        }

        public sealed class RawMonitorDrainDto
        {
            public bool Ok { get; set; } = true;
            public string Data { get; set; } = "";
        }

        // [JsonPropertyName] explícito: la deserialización del body snake_case
        // lo requiere (misma convención que CoreXConfigController).
        internal sealed class UdpMonitorFlagsRequest
        {
            [System.Text.Json.Serialization.JsonPropertyName("log_nmea")]
            public bool LogNmea { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("log_ntrip")]
            public bool LogNtrip { get; set; }
        }

        public sealed class GpsDetailResponse
        {
            public bool Ok { get; set; } = true;
            public CoreXGpsDto Gps { get; set; }
        }

        public sealed class EventosResponse
        {
            public bool Ok { get; set; } = true;
            public string Archivo { get; set; } = "";
            public string Historico { get; set; } = "";
            public string Sesion { get; set; } = "";
        }
    }
}
