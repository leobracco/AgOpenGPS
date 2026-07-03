// ============================================================================
// ConfiguracionController.cs
//
// Export/Import de TODA la configuración de PilotX a/desde un pendrive, desde el
// Hub. Resuelve el caso "se reinstaló la PC / se pasó a otra máquina y hay que
// reconfigurar todo de cero": el operario baja un .zip con la config y lo
// restaura en el equipo nuevo.
//
//   GET  /api/config/export   → descarga config-pilotx-<fecha>.zip
//   POST /api/config/import    → restaura desde el .zip (body = bytes del zip)
//
// El set de archivos y la lógica viven en ConfigBackupService (mismo que usa el
// backup automático a %ProgramData%). Antes de importar se hace un backup de
// seguridad del estado actual.
// ============================================================================

using AgroParallel.Services;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ConfiguracionController : AgpControllerBase
    {
        // Cap del zip de import (config son KBs; 64 MB cubre de sobra y frena
        // que suban un archivo gigante por error).
        private const long MaxZipBytes = 64L * 1024 * 1024;

        [Route(HttpVerbs.Get, "/config/export")]
        public async Task Export()
        {
            byte[] zip;
            try
            {
                zip = ConfigBackupService.CreateExportZip(
                    ConfigBackupService.ConfiguredExtraDirs);
            }
            catch (Exception ex)
            {
                // serialización especial a propósito: error 500 escrito directo al stream
                // antes de cambiar el Content-Type a zip.
                HttpContext.Response.StatusCode = 500;
                await WriteJsonAsync(new { ok = false, error = "export-failed", detail = ex.Message });
                return;
            }

            string name = "config-pilotx-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = "application/zip";
            HttpContext.Response.ContentLength64 = zip.Length;
            HttpContext.Response.Headers["Cache-Control"] = "no-store";
            HttpContext.Response.Headers["Content-Disposition"] =
                "attachment; filename=\"" + name + "\"";
            // serialización especial a propósito: binario ZIP, no JSON.
            await HttpContext.Response.OutputStream.WriteAsync(zip, 0, zip.Length)
                .ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/config/backup")]
        public Task Backup([QueryField] string tipo)
        {
            try
            {
                // tipo == "instalador" → copia permanente (no rota con los
                // diarios). Cualquier otro valor → backup normal forzado.
                bool installer = string.Equals(
                    tipo, "instalador", StringComparison.OrdinalIgnoreCase);

                // force: true → un backup manual no se saltea por el dedup, el
                // operario tiene que ver la copia aparecer aunque nada cambió.
                string dir = ConfigBackupService.RunBackup(
                    ConfigBackupService.ConfiguredExtraDirs,
                    force: true, installer: installer);
                return WriteJsonAsync(new
                {
                    ok = dir != null,
                    carpeta = dir,
                    nombre = dir != null ? Path.GetFileName(dir) : null
                });
            }
            catch (Exception ex)
            {
                return WriteJsonAsync(new { ok = false, error = "backup-failed", detail = ex.Message });
            }
        }

        [Route(HttpVerbs.Get, "/config/backups")]
        public Task Backups()
        {
            try
            {
                var list = ConfigBackupService.ListBackups()
                    .Select(b => new
                    {
                        nombre = b.Name,
                        tipo = b.Kind,
                        fecha = b.When.ToString("yyyy-MM-dd HH:mm:ss"),
                        archivos = b.FileCount,
                        bytes = b.TotalBytes
                    })
                    .ToArray();
                return WriteJsonAsync(new { ok = true, backups = list });
            }
            catch (Exception ex)
            {
                return WriteJsonAsync(new { ok = false, error = ex.Message });
            }
        }

        [Route(HttpVerbs.Get, "/config/backup-detalle")]
        public Task BackupDetalle([QueryField] string nombre)
        {
            if (string.IsNullOrWhiteSpace(nombre))
                return WriteJsonAsync(new { ok = false, error = "falta-nombre" });

            try
            {
                var files = ConfigBackupService.ListBackupFiles(nombre)
                    .Select(f => new { ruta = f.Path, bytes = f.Bytes })
                    .ToArray();
                return WriteJsonAsync(new { ok = true, nombre, archivos = files });
            }
            catch (Exception ex)
            {
                return WriteJsonAsync(new { ok = false, error = ex.Message });
            }
        }

        [Route(HttpVerbs.Post, "/config/restore")]
        public Task Restore([QueryField] string nombre)
        {
            if (string.IsNullOrWhiteSpace(nombre))
                return WriteJsonAsync(new { ok = false, error = "falta-nombre" });

            var r = ConfigBackupService.RestoreFromBackup(
                nombre, ConfigBackupService.ConfiguredExtraDirs);

            return WriteJsonAsync(new
            {
                ok = r.Ok,
                archivos_restaurados = r.FilesRestored,
                error = r.Error
            });
        }

        [Route(HttpVerbs.Post, "/config/import")]
        public async Task Import()
        {
            byte[] data;
            try
            {
                using (var input = HttpContext.Request.InputStream)
                using (var buf = new MemoryStream())
                {
                    byte[] chunk = new byte[64 * 1024];
                    int n;
                    long total = 0;
                    while ((n = await input.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > MaxZipBytes)
                        {
                            await WriteJsonAsync(new { ok = false, error = "file-too-large", max_bytes = MaxZipBytes });
                            return;
                        }
                        await buf.WriteAsync(chunk, 0, n).ConfigureAwait(false);
                    }
                    data = buf.ToArray();
                }
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(new { ok = false, error = "read-failed", detail = ex.Message });
                return;
            }

            if (data.Length < 22) // tamaño mínimo de un zip vacío (EOCD)
            {
                await WriteJsonAsync(new { ok = false, error = "empty-or-invalid" });
                return;
            }

            var result = ConfigBackupService.ImportFromZip(
                data, ConfigBackupService.ConfiguredExtraDirs);

            await WriteJsonAsync(new
            {
                ok = result.Ok,
                archivos_restaurados = result.FilesRestored,
                error = result.Error
            });
        }
    }
}
