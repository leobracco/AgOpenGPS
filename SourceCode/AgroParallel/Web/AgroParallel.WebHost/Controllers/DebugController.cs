// ============================================================================
// DebugController.cs — REST del módulo Debug.
//   GET  /api/debug/snapshot?max=N      → buffer + config + estado grabación
//   GET  /api/debug/entries?since=S&modules=quantix,vistax
//   GET  /api/debug/config
//   PUT  /api/debug/config              body: DebugConfigDto
//   POST /api/debug/module?name=X&on=true|false
//   POST /api/debug/clear
//   POST /api/debug/record?on=true|false
//   POST /api/debug/append              body: { module, level, message }
//                                       (para que el UI pueda emitir logs)
// ============================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class DebugController : AgpControllerBase
    {
        private readonly IDebugLogService _log;

        public DebugController(IDebugLogService log)
        {
            _log = log;
        }

        [Route(HttpVerbs.Get, "/debug/snapshot")]
        public Task Snapshot([QueryField] int max)
        {
            if (_log == null) return Unavailable();
            return WriteJsonAsync(_log.GetSnapshot(max > 0 ? max : 500));
        }

        [Route(HttpVerbs.Get, "/debug/entries")]
        public Task Entries([QueryField] long since, [QueryField] string modules)
        {
            if (_log == null) return Unavailable();
            List<string> mods = null;
            if (!string.IsNullOrEmpty(modules))
            {
                mods = new List<string>(modules.Split(','));
            }
            var list = _log.GetEntriesSince(since, mods);
            return WriteJsonAsync(new { ok = true, count = list.Count, entries = list });
        }

        [Route(HttpVerbs.Get, "/debug/config")]
        public Task GetConfig()
        {
            if (_log == null) return Unavailable();
            return WriteJsonAsync(_log.GetConfig());
        }

        [Route(HttpVerbs.Put, "/debug/config")]
        public async Task PutConfig()
        {
            if (_log == null) { await Unavailable(); return; }
            DebugConfigDto cfg;
            try { cfg = await ReadJsonBodyAsync<DebugConfigDto>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-json" }); return; }
            _log.SaveConfig(cfg);
            await WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/debug/module")]
        public Task ToggleModule([QueryField] string name, [QueryField] bool on)
        {
            if (_log == null) return Unavailable();
            if (string.IsNullOrEmpty(name)) return WriteJsonAsync(new { ok = false, error = "missing-name" });
            _log.SetModuleEnabled(name, on);
            return WriteJsonAsync(new { ok = true, name, on });
        }

        [Route(HttpVerbs.Post, "/debug/clear")]
        public Task Clear()
        {
            if (_log == null) return Unavailable();
            _log.Clear();
            return WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/debug/record")]
        public Task Record([QueryField] bool on)
        {
            if (_log == null) return Unavailable();
            if (on)
            {
                string p = _log.StartRecording();
                return WriteJsonAsync(new { ok = !string.IsNullOrEmpty(p), file = p });
            }
            _log.StopRecording();
            return WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/debug/append")]
        public async Task Append()
        {
            if (_log == null) { await Unavailable(); return; }
            DebugEntryDto dto;
            try { dto = await ReadJsonBodyAsync<DebugEntryDto>(); }
            catch
            {
                await WriteJsonAsync(new { ok = false, error = "invalid-json" });
                return;
            }
            _log.Append(dto?.Module, dto?.Level, dto?.Message);
            await WriteJsonAsync(new { ok = true });
        }

        private Task Unavailable() => WriteJsonAsync(new { ok = false, error = "service-unavailable" });
    }
}
