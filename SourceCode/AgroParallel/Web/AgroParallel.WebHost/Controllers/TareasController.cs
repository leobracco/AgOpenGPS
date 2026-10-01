// ============================================================================
// TareasController.cs — REST de las "Tareas de trabajo" del lote abierto.
//   GET  /api/tareas/estado                         → TareasEstado
//   POST /api/tareas/crear     {cultivo, tipo_trabajo, notas} → TareasEstado
//   POST /api/tareas/pausar                         → TareasEstado
//   POST /api/tareas/reanudar                       → TareasEstado
//   POST /api/tareas/cerrar                         → TareasEstado
//   POST /api/tareas/exportar  {id, destino}        → TareaExportResultado
// Wire snake_case por AgpJson. Si el host no inyectó el servicio (p. ej. el
// head Android hoy) el controller no se registra y las rutas dan 404.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Tareas;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class TareasController : AgpControllerBase
    {
        private readonly TareasService _svc;

        public TareasController(TareasService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/tareas/estado")]
        public Task GetEstado() => WriteJsonAsync(_svc.Estado());

        [Route(HttpVerbs.Post, "/tareas/crear")]
        public async Task PostCrear()
        {
            var body = await LeerAsync<CrearBody>();
            await WriteJsonAsync(_svc.Crear(new TareaCrearPedido
            {
                Cultivo = body?.Cultivo,
                TipoTrabajo = body?.TipoTrabajo,
                Notas = body?.Notas,
            }));
        }

        [Route(HttpVerbs.Post, "/tareas/pausar")]
        public Task PostPausar() => WriteJsonAsync(_svc.Pausar());

        [Route(HttpVerbs.Post, "/tareas/reanudar")]
        public Task PostReanudar() => WriteJsonAsync(_svc.Reanudar());

        [Route(HttpVerbs.Post, "/tareas/cerrar")]
        public Task PostCerrar() => WriteJsonAsync(_svc.Cerrar());

        [Route(HttpVerbs.Post, "/tareas/exportar")]
        public async Task PostExportar()
        {
            var body = await LeerAsync<ExportarBody>();
            if (body == null || string.IsNullOrEmpty(body.Id))
            {
                await WriteJsonAsync(new TareaExportResultado { Ok = false, Error = "Falta la tarea a exportar." });
                return;
            }
            await WriteJsonAsync(_svc.Exportar(body.Id, body.Destino));
        }

        private async Task<T> LeerAsync<T>() where T : class
        {
            try { return await ReadJsonBodyAsync<T>(); }
            catch { return null; }
        }

        private sealed class CrearBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("cultivo")]
            public string Cultivo { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("tipo_trabajo")]
            public string TipoTrabajo { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("notas")]
            public string Notas { get; set; }
        }

        private sealed class ExportarBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string Id { get; set; }

            /// <summary>Ruta del .html a escribir (la elige el operario en el
            /// explorador). Vacío = Documentos\PilotX\Tareas.</summary>
            [System.Text.Json.Serialization.JsonPropertyName("destino")]
            public string Destino { get; set; }
        }
    }
}
