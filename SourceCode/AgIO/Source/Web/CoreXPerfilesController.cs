// ============================================================================
// CoreXPerfilesController.cs — Gestión de perfiles de CoreX desde la web UI.
// Espejo de FormProfiles (WinForms): los perfiles son XML en
// RegistrySettings.profileDirectory y el activo vive en el Registry.
//
// Rutas:
//   GET  /api/corex/config/perfiles   → { ok, activo, perfiles[] }
//   POST /api/corex/perfil/guardar    → guarda la config actual en el perfil activo
//   POST /api/corex/perfil/cargar     → body {nombre}; cambia perfil y REINICIA
//   POST /api/corex/perfil/crear      → body {nombre, desde_actual}
//                                       desde_actual=true: copia config (sin reinicio)
//                                       desde_actual=false: fábrica (REINICIA)
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    public sealed class CoreXPerfilesController : AgpControllerBase
    {
        private readonly FormLoop _form;

        public CoreXPerfilesController(FormLoop form)
        {
            _form = form;
        }

        // ── GET /api/corex/config/perfiles ────────────────────────────────────
        // Lista los XML del directorio de perfiles + el activo. Solo lectura de
        // disco y de un static string: no necesita el hilo UI.
        [Route(HttpVerbs.Get, "/corex/config/perfiles")]
        public async Task GetPerfiles()
        {
            string[] perfiles;
            try
            {
                perfiles = Directory.GetFiles(RegistrySettings.profileDirectory, "*.xml")
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception)
            {
                perfiles = new string[0];
            }

            await WriteJsonAsync(new
            {
                Ok = true,
                Activo = RegistrySettings.profileName,
                Perfiles = perfiles,
            }).ConfigureAwait(false);
        }

        // ── POST /api/corex/perfil/guardar ────────────────────────────────────
        // Guarda la configuración vigente en el XML del perfil activo.
        [Route(HttpVerbs.Post, "/corex/perfil/guardar")]
        public async Task Guardar()
        {
            if (string.IsNullOrEmpty(RegistrySettings.profileName))
            {
                await WriteErrorAsync(400, "SIN_PERFIL",
                    "No hay perfil activo. Creá uno primero.").ConfigureAwait(false);
                return;
            }

            await _form.RunOnUiAsync<object>(() =>
            {
                _form.SaveProfileFromWeb();
                return null;
            }).ConfigureAwait(false);

            await WriteJsonAsync(new { Ok = true }).ConfigureAwait(false);
        }

        // ── POST /api/corex/perfil/cargar ─────────────────────────────────────
        // Body: { "nombre": "..." }. Valida que el XML exista, cambia el
        // perfil activo y reinicia CoreX (diferido 800 ms).
        [Route(HttpVerbs.Post, "/corex/perfil/cargar")]
        public async Task Cargar()
        {
            var req = await ReadJsonBodyAsync<PerfilRequest>().ConfigureAwait(false);
            var nombre = Sanitizar(req?.Nombre);
            if (string.IsNullOrEmpty(nombre))
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Falta el nombre del perfil")
                    .ConfigureAwait(false);
                return;
            }

            if (!File.Exists(PathDe(nombre)))
            {
                await WriteErrorAsync(404, "PERFIL_INEXISTENTE",
                    "El perfil no existe: " + nombre).ConfigureAwait(false);
                return;
            }

            await _form.RunOnUiAsync<object>(() =>
            {
                _form.LoadProfileFromWeb(nombre);
                return null;
            }).ConfigureAwait(false);

            await WriteJsonAsync(new { Ok = true, Restart = true }).ConfigureAwait(false);
        }

        // ── POST /api/corex/perfil/crear ──────────────────────────────────────
        // Body: { "nombre": "...", "desde_actual": true/false }.
        [Route(HttpVerbs.Post, "/corex/perfil/crear")]
        public async Task Crear()
        {
            var req = await ReadJsonBodyAsync<PerfilCrearRequest>().ConfigureAwait(false);
            var nombre = Sanitizar(req?.Nombre);
            if (string.IsNullOrEmpty(nombre))
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Falta el nombre del perfil")
                    .ConfigureAwait(false);
                return;
            }

            if (File.Exists(PathDe(nombre)))
            {
                await WriteErrorAsync(409, "PERFIL_YA_EXISTE",
                    "Ya existe un perfil con ese nombre: " + nombre).ConfigureAwait(false);
                return;
            }

            bool restart = await _form.RunOnUiAsync(
                () => _form.CreateProfileFromWeb(nombre, req.DesdeActual)
            ).ConfigureAwait(false);

            await WriteJsonAsync(new { Ok = true, Restart = restart }).ConfigureAwait(false);
        }

        // Misma sanitización que FormProfiles (sin <>:"/\|?*) + trim.
        private static string Sanitizar(string nombre)
        {
            if (nombre == null) return "";
            return FormProfiles.SanitizeFileName(nombre).Trim();
        }

        private static string PathDe(string nombre)
        {
            return Path.Combine(RegistrySettings.profileDirectory, nombre + ".XML");
        }
    }

    // ── DTOs de entrada de perfiles ───────────────────────────────────────────

    internal sealed class PerfilRequest
    {
        [JsonPropertyName("nombre")] public string Nombre { get; set; }
    }

    internal sealed class PerfilCrearRequest
    {
        [JsonPropertyName("nombre")] public string Nombre { get; set; }
        [JsonPropertyName("desde_actual")] public bool DesdeActual { get; set; }
    }
}
