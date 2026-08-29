// ============================================================================
// RedIpController.cs — config de IP (DHCP/fija) de los adaptadores:
//   GET  /api/red/adaptadores       → { ok, adaptadores:[{ if_index, nombre, tipo, dhcp, ip, prefix, gateway, dns[], up }] }
//   POST /api/red/ip                ← { if_index, mode:"dhcp"|"static", ip, prefix, gateway, dns[] }  → { ok, error? }
// El cambio real lo aplica el helper SYSTEM (NetApplyWatcher); PilotX corre
// limitado y solo puede leer + dejar el pedido (ver RedIpService).
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AgroParallel.Services;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class RedIpController : AgpControllerBase
    {
        private readonly RedIpService _red;

        public RedIpController(RedIpService red) { _red = red; }

        internal sealed class AplicarRequest
        {
            [JsonPropertyName("if_index")] public int IfIndex { get; set; }
            [JsonPropertyName("mode")] public string Mode { get; set; }
            [JsonPropertyName("ip")] public string Ip { get; set; }
            [JsonPropertyName("prefix")] public int Prefix { get; set; }
            [JsonPropertyName("gateway")] public string Gateway { get; set; }
            [JsonPropertyName("dns")] public List<string> Dns { get; set; }
        }

        [Route(HttpVerbs.Get, "/red/adaptadores")]
        public Task Listar()
        {
            if (_red == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var lista = _red.Listar();
            return WriteJsonAsync(new
            {
                ok = true,
                adaptadores = lista.Select(a => new
                {
                    if_index = a.IfIndex,
                    nombre = a.Nombre,
                    tipo = a.Tipo,
                    dhcp = a.Dhcp,
                    ip = a.Ip,
                    prefix = a.Prefix,
                    gateway = a.Gateway,
                    dns = a.Dns,
                    up = a.Up,
                    metric = a.Metric,
                }).ToList(),
            });
        }

        [Route(HttpVerbs.Post, "/red/ip")]
        public async Task Aplicar()
        {
            if (_red == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" }).ConfigureAwait(false);
                return;
            }
            var req = await ReadJsonBodyAsync<AplicarRequest>().ConfigureAwait(false);
            if (req == null || req.IfIndex <= 0 || string.IsNullOrWhiteSpace(req.Mode))
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Falta if_index o mode").ConfigureAwait(false);
                return;
            }
            bool ok = _red.Aplicar(req.IfIndex, req.Mode.Trim().ToLowerInvariant(),
                                   req.Ip, req.Prefix, req.Gateway, req.Dns, out string error);
            await WriteJsonAsync(new { ok, error }).ConfigureAwait(false);
        }
    }
}
