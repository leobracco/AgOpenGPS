// ============================================================================
// RedWifiController.cs — WiFi propio de PilotX (página wifi.html del Hub):
//   GET  /api/red/wifi                → { ok, estado:{conectado,ssid,ip}, redes:[{ssid,senal_pct,segura,conectada}] }
//   POST /api/red/wifi/conectar       ← { ssid, clave }   → { ok, error? }
//   POST /api/red/wifi/desconectar    → { ok, error? }
// Reemplaza el viejo "abrir ms-settings de Windows": acá el operario ve las
// redes CON su estado real y teclea la clave con el teclado de PilotX.
// Si no se inyectó IWifiService (host sin WiFi), contesta service-unavailable.
// ============================================================================

using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class RedWifiController : AgpControllerBase
    {
        private readonly IWifiService _wifi;

        public RedWifiController(IWifiService wifi) { _wifi = wifi; }

        internal sealed class ConectarRequest
        {
            [JsonPropertyName("ssid")] public string Ssid { get; set; }
            [JsonPropertyName("clave")] public string Clave { get; set; }
        }

        [Route(HttpVerbs.Get, "/red/wifi")]
        public Task Listar()
        {
            if (_wifi == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });

            // Escanear ya trae la conectada marcada (merge con el estado).
            var redes = _wifi.Escanear();
            var estado = _wifi.Estado();
            return WriteJsonAsync(new
            {
                ok = true,
                estado = new { conectado = estado.Conectado, ssid = estado.Ssid, ip = estado.Ip },
                redes = redes.Select(r => new
                {
                    ssid = r.Ssid,
                    senal_pct = r.SenalPct,
                    segura = r.Segura,
                    conectada = r.Conectada,
                }).ToList(),
            });
        }

        [Route(HttpVerbs.Post, "/red/wifi/conectar")]
        public async Task Conectar()
        {
            if (_wifi == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" }).ConfigureAwait(false);
                return;
            }
            var req = await ReadJsonBodyAsync<ConectarRequest>().ConfigureAwait(false);
            if (req == null || string.IsNullOrWhiteSpace(req.Ssid))
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Falta el ssid").ConfigureAwait(false);
                return;
            }
            // Conectar bloquea hasta confirmar (~máx 15 s): es un POST que el
            // operario dispara una vez y espera mirando el spinner de la página.
            bool ok = _wifi.Conectar(req.Ssid.Trim(), req.Clave, out string error);
            var estado = _wifi.Estado();
            await WriteJsonAsync(new
            {
                ok,
                error,
                estado = new { conectado = estado.Conectado, ssid = estado.Ssid, ip = estado.Ip },
            }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/red/wifi/desconectar")]
        public Task Desconectar()
        {
            if (_wifi == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            bool ok = _wifi.Desconectar(out string error);
            return WriteJsonAsync(new { ok, error });
        }
    }
}
