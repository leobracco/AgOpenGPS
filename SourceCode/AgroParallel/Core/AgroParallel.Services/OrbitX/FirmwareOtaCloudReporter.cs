// ============================================================================
// FirmwareOtaCloudReporter.cs
//
// POST a /api/ota/resultado del OrbitX cloud cuando un nodo termina de
// actualizar (ok|falla). Cierra el loop de auditoría: hoy el resultado vive
// en memoria del coordinator (NodoOtaState) y se pierde al reiniciar la PC;
// con esto queda registrado en el server como `ota_log_<ts>_<deviceId>` doc
// en la DB global y, si el resultado fue "ok", el server además actualiza la
// versión del device y borra el pendiente.
//
// Auth: igual que FirmwareMirror — X-Device-ID + X-Auth-Token del PC tractor.
// El device_id del log es el de la PC (no el UID del nodo ESP32). El UID del
// nodo va dentro del payload como `nodo_uid` para no perder qué nodo concreto
// actualizó (CouchDB es schemaless, el server acepta campos extra).
//
// Mapping de status:
//   coordinator "ok"    → resultado "ok"
//   coordinator "error" → resultado "falla"
//   "iniciando" y "sent" → NO se reportan (no son finales)
// ============================================================================

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AgroParallel.OrbitX
{
    public static class FirmwareOtaCloudReporter
    {
        /// <summary>
        /// Postea el resultado final al cloud. Fire-and-forget: si OrbitX está
        /// offline o devuelve error, se loguea y se sigue — el resultado local
        /// (NodoOtaState) y el flow del nodo no dependen del cloud.
        /// </summary>
        /// <param name="status">"ok" o "error" del coordinator (otros se ignoran).</param>
        public static async Task ReportAsync(
            HttpClient http,
            OrbitXConfig cfg,
            string producto,
            string nodoUid,
            string version,
            string versionAnterior,
            string status,
            string detalle,
            Action<string> log = null)
        {
            log = log ?? (_ => { });

            if (cfg == null || !cfg.Enabled) return;
            if (string.IsNullOrWhiteSpace(cfg.ServerUrl)) return;
            if (string.IsNullOrWhiteSpace(cfg.DeviceId) || string.IsNullOrWhiteSpace(cfg.DeviceToken)) return;
            if (string.IsNullOrWhiteSpace(producto) || string.IsNullOrWhiteSpace(version)) return;

            string resultado;
            switch ((status ?? "").ToLowerInvariant())
            {
                case "ok":    resultado = "ok"; break;
                case "error": resultado = "falla"; break;
                default: return; // no es estado final
            }

            try
            {
                if (http == null) return;
                string url = cfg.ServerUrl.TrimEnd('/') + "/api/ota/resultado";

                // Body: el server espera producto/version/resultado obligatorios;
                // version_anterior y error son opcionales. nodo_uid lo agregamos
                // para no perder qué nodo concreto se actualizó (varios QuantiX
                // pueden convivir en el mismo tractor).
                var body = new
                {
                    producto = producto,
                    version = version,
                    version_anterior = string.IsNullOrEmpty(versionAnterior) ? null : versionAnterior,
                    resultado = resultado,
                    error = string.IsNullOrEmpty(detalle) ? null : detalle,
                    nodo_uid = nodoUid // extra — el router lo guarda en el doc CouchDB
                };
                string json = JsonSerializer.Serialize(body);

                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            log($"OTA result report HTTP {(int)resp.StatusCode}: " + Trunc(respBody, 160));
                            return;
                        }
                        log($"OTA result reportado a cloud: {producto} v{version} → {resultado} (nodo {nodoUid})");
                    }
                }
            }
            catch (Exception ex)
            {
                // No queremos que un cloud caído rompa el flow OTA local.
                log("OTA result report error: " + ex.Message);
            }
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
