// ============================================================================
// SectionXController.cs
// Endpoints REST del módulo SectionX:
//   GET  /api/sectionx/config   → SectionXConfigDto
//   POST /api/sectionx/config   (body = SectionXConfigDto) → { ok }
//
// IMPORTANTE: serializamos a mano con System.Text.Json (no Swan): el
// ResponseSerializer default de EmbedIO ignora [JsonPropertyName] y emite
// PascalCase (Nodos/Cables/SeccionAog/...). El JS espera snake_case
// (nodos/cables/seccion_aog). Si dejábamos Swan, el GET devolvía PascalCase,
// loadCfg() veía `cfg.nodos === undefined`, lo reseteaba a [], y el operario
// percibía que "habilitas, salís, volvés y se deshabilitó".
// ============================================================================

using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Cut;
using AgroParallel.Models;
using AgroParallel.SectionX;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SectionXController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonInOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Sin policy: cada propiedad usa su [JsonPropertyName] explícito.
        private static readonly JsonSerializerOptions JsonOutOpts = new JsonSerializerOptions();

        private readonly ISectionXConfigService _cfg;

        public SectionXController(ISectionXConfigService cfg)
        {
            _cfg = cfg;
        }

        private async Task SendJsonAsync(object obj)
        {
            string json = JsonSerializer.Serialize(obj, JsonOutOpts);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/sectionx/config")]
        public async Task GetConfig()
        {
            if (_cfg == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            await SendJsonAsync(_cfg.Load());
        }

        [Route(HttpVerbs.Post, "/sectionx/config")]
        public async Task SaveConfig()
        {
            if (_cfg == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            SectionXConfigDto dto;
            try { dto = JsonSerializer.Deserialize<SectionXConfigDto>(body, JsonInOpts); }
            catch { dto = null; }
            if (dto == null) { await SendJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            _cfg.Save(dto);
            await SendJsonAsync(new { ok = true });
        }

        // ---------------------------------------------------------------------
        // Status del bridge — alimenta el chip semáforo de la UI.
        // Devuelve nulls/false coherentes si no hay bridge corriendo (typically
        // porque la config tiene nodos:[] o enabled:false). El JS interpreta:
        //   !connected => 🔴 broker caído
        //   !running || nodoCount==0 => 🟡 sin nodos
        //   lastPublishMsAgo < 3000 => 🟢 publicando
        //   else => 🟡 inactivo (típico tractor parado)
        // ---------------------------------------------------------------------
        [Route(HttpVerbs.Get, "/sectionx/status")]
        public async Task GetStatus()
        {
            var br = CutDispatcher.Current;
            if (br == null)
            {
                await SendJsonAsync(new
                {
                    running = false,
                    connected = false,
                    nodoCount = 0,
                    messagesSent = 0,
                    lastPublishMsAgo = (long?)null
                });
                return;
            }
            var s = br.GetStatus("sectionx");
            await SendJsonAsync(new
            {
                running = s.Running,
                connected = s.Connected,
                nodoCount = s.NodeCount,
                messagesSent = s.MessagesSent,
                lastPublishMsAgo = s.LastPublishMsAgo
            });
        }

        // ---------------------------------------------------------------------
        // Debug snapshot — alimenta el panel colapsable de la UI. Polling 2 Hz
        // sólo si el operario abrió el <details>. Si no hay bridge, devolvemos
        // shape válida con todo vacío para que el JS no rompa.
        // ---------------------------------------------------------------------
        [Route(HttpVerbs.Get, "/sectionx/debug")]
        public async Task GetDebug()
        {
            var br = CutDispatcher.Current;
            if (br == null)
            {
                await SendJsonAsync(new
                {
                    lastByNodo = new System.Collections.Generic.Dictionary<string, object>(),
                    logTail = new string[0]
                });
                return;
            }
            var snap = br.GetDebugSnapshot("sectionx", 30);
            // Reproyectar a snake_case-ish keys que espera el JS sin atar el
            // tipo del Core a System.Text.Json attrs.
            var last = new System.Collections.Generic.Dictionary<string, object>();
            foreach (var kv in snap.LastByNodo)
            {
                last[kv.Key] = new
                {
                    topic = kv.Value.Topic,
                    payload = kv.Value.Payload,
                    bits = kv.Value.Bits,
                    msAgo = kv.Value.MsAgo
                };
            }
            await SendJsonAsync(new { lastByNodo = last, logTail = snap.LogTail });
        }

        // ---------------------------------------------------------------------
        // Test de relés: ejecuta la secuencia en background (no esperamos el
        // resultado completo desde el HTTP — el JS confirma con un toast
        // demorado). Si no hay bridge corriendo, error explícito.
        // ---------------------------------------------------------------------
        [Route(HttpVerbs.Post, "/sectionx/test/{uid}")]
        public async Task RunTest(string uid)
        {
            var br = CutDispatcher.Current;
            if (br == null)
            {
                await SendJsonAsync(new { ok = false, error = "bridge-not-running" });
                return;
            }
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            TestRequestDto dto = null;
            try { dto = JsonSerializer.Deserialize<TestRequestDto>(body, JsonInOpts); } catch { }
            if (dto == null || dto.Cables == null || dto.Cables.Length == 0)
            {
                await SendJsonAsync(new { ok = false, error = "no-cables" });
                return;
            }
            int stepMs = dto.StepMs > 0 ? dto.StepMs : 1000;
            // Fire-and-forget: el JS ya hizo setTimeout para mostrar el toast
            // "Test completo" después de cables.length * stepMs.
            _ = br.RunRelayTestAsync(uid, dto.Cables, stepMs);
            await SendJsonAsync(new { ok = true, cables = dto.Cables.Length, stepMs = stepMs });
        }

        // DTO interno del POST /sectionx/test/{uid}.
        private sealed class TestRequestDto
        {
            [System.Text.Json.Serialization.JsonPropertyName("cables")]
            public int[] Cables { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("stepMs")]
            public int StepMs { get; set; }
        }
    }
}
