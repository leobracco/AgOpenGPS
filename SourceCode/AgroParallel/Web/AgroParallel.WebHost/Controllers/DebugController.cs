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
using System.IO;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Swan.Formatters;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class DebugController : WebApiController
    {
        private readonly IDebugLogService _log;

        public DebugController(IDebugLogService log)
        {
            _log = log;
        }

        [Route(HttpVerbs.Get, "/debug/snapshot")]
        public object Snapshot([QueryField] int max)
        {
            if (_log == null) return Unavailable();
            return _log.GetSnapshot(max > 0 ? max : 500);
        }

        [Route(HttpVerbs.Get, "/debug/entries")]
        public object Entries([QueryField] long since, [QueryField] string modules)
        {
            if (_log == null) return Unavailable();
            List<string> mods = null;
            if (!string.IsNullOrEmpty(modules))
            {
                mods = new List<string>(modules.Split(','));
            }
            var list = _log.GetEntriesSince(since, mods);
            return new { ok = true, count = list.Count, entries = list };
        }

        [Route(HttpVerbs.Get, "/debug/config")]
        public object GetConfig()
        {
            if (_log == null) return Unavailable();
            return _log.GetConfig();
        }

        [Route(HttpVerbs.Put, "/debug/config")]
        public async System.Threading.Tasks.Task<object> PutConfig()
        {
            if (_log == null) return Unavailable();
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            DebugConfigDto cfg;
            try { cfg = Json.Deserialize<DebugConfigDto>(body); }
            catch { return new { ok = false, error = "invalid-json" }; }
            _log.SaveConfig(cfg);
            return new { ok = true };
        }

        [Route(HttpVerbs.Post, "/debug/module")]
        public object ToggleModule([QueryField] string name, [QueryField] bool on)
        {
            if (_log == null) return Unavailable();
            if (string.IsNullOrEmpty(name)) return new { ok = false, error = "missing-name" };
            _log.SetModuleEnabled(name, on);
            return new { ok = true, name, on };
        }

        [Route(HttpVerbs.Post, "/debug/clear")]
        public object Clear()
        {
            if (_log == null) return Unavailable();
            _log.Clear();
            return new { ok = true };
        }

        [Route(HttpVerbs.Post, "/debug/record")]
        public object Record([QueryField] bool on)
        {
            if (_log == null) return Unavailable();
            if (on)
            {
                string p = _log.StartRecording();
                return new { ok = !string.IsNullOrEmpty(p), file = p };
            }
            _log.StopRecording();
            return new { ok = true };
        }

        [Route(HttpVerbs.Post, "/debug/append")]
        public async System.Threading.Tasks.Task<object> Append()
        {
            if (_log == null) return Unavailable();
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            try
            {
                var dto = Json.Deserialize<DebugEntryDto>(body);
                _log.Append(dto?.Module, dto?.Level, dto?.Message);
                return new { ok = true };
            }
            catch
            {
                return new { ok = false, error = "invalid-json" };
            }
        }

        private object Unavailable() => new { ok = false, error = "service-unavailable" };
    }
}
