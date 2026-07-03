// ============================================================================
// SectionXController.cs
// Endpoints REST del módulo SectionX:
//   GET  /api/sectionx/config   → SectionXConfigDto
//   POST /api/sectionx/config   (body = SectionXConfigDto) → { ok }
//   GET  /api/sectionx/status   → estado del bridge (chip semáforo UI)
//   GET  /api/sectionx/debug    → snapshot debug (panel colapsable UI)
//   POST /api/sectionx/test/{uid}  → test de relés fire-and-forget
//
// Serialización: AgpControllerBase → AgpJson → snake_case via SnakeCaseLower.
// Todos los DTOs ya tienen [JsonPropertyName] completo. Los anónimos que antes
// salían camelCase ahora salen snake_case (nodo_count, last_publish_ms_ago,
// last_by_nodo, log_tail, ms_ago). El JS se actualizó en el mismo commit.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AgroParallel.Cut;
using AgroParallel.Models;
using AgroParallel.SectionX;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SectionXController : AgpControllerBase
    {
        private readonly ISectionXConfigService _cfg;

        public SectionXController(ISectionXConfigService cfg)
        {
            _cfg = cfg;
        }

        [Route(HttpVerbs.Get, "/sectionx/config")]
        public async Task GetConfig()
        {
            if (_cfg == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            await WriteJsonAsync(_cfg.Load());
        }

        [Route(HttpVerbs.Post, "/sectionx/config")]
        public async Task SaveConfig()
        {
            if (_cfg == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            SectionXConfigDto dto;
            try { dto = await ReadJsonBodyAsync<SectionXConfigDto>(); }
            catch { dto = null; }
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            _cfg.Save(dto);
            await WriteJsonAsync(new { ok = true });
        }

        // ---------------------------------------------------------------------
        // Status del bridge — alimenta el chip semáforo de la UI.
        // Devuelve nulls/false coherentes si no hay bridge corriendo (typically
        // porque la config tiene nodos:[] o enabled:false). El JS interpreta:
        //   !connected => broker caído
        //   !running || nodo_count==0 => sin nodos
        //   last_publish_ms_ago < 3000 => publicando
        //   else => inactivo (típico tractor parado)
        // ---------------------------------------------------------------------
        [Route(HttpVerbs.Get, "/sectionx/status")]
        public async Task GetStatus()
        {
            var br = CutDispatcher.Current;
            if (br == null)
            {
                await WriteJsonAsync(new
                {
                    running = false,
                    connected = false,
                    nodo_count = 0,
                    messages_sent = 0,
                    last_publish_ms_ago = (long?)null
                });
                return;
            }
            var s = br.GetStatus("sectionx");
            await WriteJsonAsync(new
            {
                running = s.Running,
                connected = s.Connected,
                nodo_count = s.NodeCount,
                messages_sent = s.MessagesSent,
                last_publish_ms_ago = s.LastPublishMsAgo
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
                await WriteJsonAsync(new
                {
                    last_by_nodo = new Dictionary<string, object>(),
                    log_tail = new string[0]
                });
                return;
            }
            var snap = br.GetDebugSnapshot("sectionx", 30);
            var last = new Dictionary<string, object>();
            foreach (var kv in snap.LastByNodo)
            {
                last[kv.Key] = new
                {
                    topic = kv.Value.Topic,
                    payload = kv.Value.Payload,
                    bits = kv.Value.Bits,
                    ms_ago = kv.Value.MsAgo
                };
            }
            await WriteJsonAsync(new { last_by_nodo = last, log_tail = snap.LogTail });
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
                await WriteJsonAsync(new { ok = false, error = "bridge-not-running" });
                return;
            }
            TestRequestDto dto = null;
            try { dto = await ReadJsonBodyAsync<TestRequestDto>(); } catch { }
            if (dto == null || dto.Cables == null || dto.Cables.Length == 0)
            {
                await WriteJsonAsync(new { ok = false, error = "no-cables" });
                return;
            }
            int stepMs = dto.StepMs > 0 ? dto.StepMs : 1000;
            // Fire-and-forget: el JS ya hizo setTimeout para mostrar el toast
            // "Test completo" después de cables.length * stepMs.
            _ = br.RunRelayTestAsync(uid, dto.Cables, stepMs);
            await WriteJsonAsync(new { ok = true, cables = dto.Cables.Length, step_ms = stepMs });
        }

        // DTO interno del POST /sectionx/test/{uid}.
        private sealed class TestRequestDto
        {
            [JsonPropertyName("cables")]
            public int[] Cables { get; set; }
            [JsonPropertyName("step_ms")]
            public int StepMs { get; set; }
        }
    }
}
