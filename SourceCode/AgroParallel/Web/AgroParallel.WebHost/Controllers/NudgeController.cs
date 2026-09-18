// ============================================================================
// NudgeController.cs — REST del widget "Mover guía" (mover-guia.html).
//   GET  /api/nudge/state            → estado (offset, pasos, sesión ref)
//   POST /api/nudge/move   {dir}     → mueve guía activa (dir=-1|+1)
//   POST /api/nudge/half   {dir}     → mueve media herramienta
//   POST /api/nudge/zero             → corrimiento a cero
//   POST /api/nudge/pivot            → ajustar al pivote
//   POST /api/nudge/step   {value}   → paso guía activa (cm o in display)
//   POST /api/nudge/close            → persistir guías (cierre del widget)
//   POST /api/nudge/ref/open         → abrir sesión referencia (backup)
//   POST /api/nudge/ref/move {dir}   → mueve guía de referencia
//   POST /api/nudge/ref/half {dir}   → media herramienta (referencia)
//   POST /api/nudge/ref/step {value} → paso referencia
//   POST /api/nudge/ref/save         → guardar y cerrar sesión
//   POST /api/nudge/ref/cancel       → restaurar backup y cerrar sesión
// Wire snake_case por AgpJson. Si el servicio no está inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class NudgeController : AgpControllerBase
    {
        private readonly INudgeService _svc;

        public NudgeController(INudgeService svc)
        {
            _svc = svc;
        }

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Get, "/nudge/state")]
        public Task GetState() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.GetState());

        [Route(HttpVerbs.Post, "/nudge/move")]
        public async Task PostMove()
        {
            if (_svc == null) { await Unavailable(); return; }
            int dir = await ReadDirAsync();
            await WriteJsonAsync(_svc.Move(dir));
        }

        [Route(HttpVerbs.Post, "/nudge/half")]
        public async Task PostHalf()
        {
            if (_svc == null) { await Unavailable(); return; }
            int dir = await ReadDirAsync();
            await WriteJsonAsync(_svc.Half(dir));
        }

        [Route(HttpVerbs.Post, "/nudge/zero")]
        public Task PostZero() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Zero());

        [Route(HttpVerbs.Post, "/nudge/pivot")]
        public Task PostPivot() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.ToPivot());

        [Route(HttpVerbs.Post, "/nudge/step")]
        public async Task PostStep()
        {
            if (_svc == null) { await Unavailable(); return; }
            double v = await ReadValueAsync();
            await WriteJsonAsync(_svc.SetStep(v));
        }

        [Route(HttpVerbs.Post, "/nudge/close")]
        public Task PostClose() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Close());

        [Route(HttpVerbs.Post, "/nudge/ref/open")]
        public Task PostRefOpen() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RefOpen());

        [Route(HttpVerbs.Post, "/nudge/ref/move")]
        public async Task PostRefMove()
        {
            if (_svc == null) { await Unavailable(); return; }
            int dir = await ReadDirAsync();
            await WriteJsonAsync(_svc.RefMove(dir));
        }

        [Route(HttpVerbs.Post, "/nudge/ref/half")]
        public async Task PostRefHalf()
        {
            if (_svc == null) { await Unavailable(); return; }
            int dir = await ReadDirAsync();
            await WriteJsonAsync(_svc.RefHalf(dir));
        }

        [Route(HttpVerbs.Post, "/nudge/ref/step")]
        public async Task PostRefStep()
        {
            if (_svc == null) { await Unavailable(); return; }
            double v = await ReadValueAsync();
            await WriteJsonAsync(_svc.RefSetStep(v));
        }

        [Route(HttpVerbs.Post, "/nudge/ref/save")]
        public Task PostRefSave() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RefSave());

        [Route(HttpVerbs.Post, "/nudge/ref/cancel")]
        public Task PostRefCancel() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RefCancel());

        private async Task<int> ReadDirAsync()
        {
            try
            {
                var body = await ReadJsonBodyAsync<DirBody>();
                return body != null ? body.Dir : 0;
            }
            catch { return 0; }
        }

        private async Task<double> ReadValueAsync()
        {
            try
            {
                var body = await ReadJsonBodyAsync<ValueBody>();
                return body != null ? body.Value : 0;
            }
            catch { return 0; }
        }

        private sealed class DirBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("dir")]
            public int Dir { get; set; }
        }

        private sealed class ValueBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("value")]
            public double Value { get; set; }
        }
    }
}
