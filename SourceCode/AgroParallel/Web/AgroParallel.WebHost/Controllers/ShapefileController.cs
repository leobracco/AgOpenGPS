// ============================================================================
// ShapefileController.cs
// REST para subir/quitar el shapefile activo del lote.
//
//   POST   /api/aog/shape    body JSON: { "files":[{"name":"x.shp","b64":"..."},
//                                                  {"name":"x.shx","b64":"..."},
//                                                  {"name":"x.dbf","b64":"..."}] }
//   DELETE /api/aog/shape    quita la capa activa y borra persistencia.
//
// JSON+base64 en vez de multipart/form-data: simplifica el server (EmbedIO no
// trae parser de multipart) y para shapefiles agrícolas (<1MB típico) la
// sobrecarga del b64 es trivial. La UI HTML usa FileReader.readAsDataURL para
// armar el payload sin libs extras.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ShapefileController : AgpControllerBase
    {
        private readonly IShapefileService _shape;

        public ShapefileController(IShapefileService shape)
        {
            _shape = shape;
        }

        // DTO interno solo para deserializar el body. Mantiene la firma del
        // wire format estable; el Models DTO (ShapefileUploadFile) usa byte[].
        private sealed class FileWire
        {
            public string name { get; set; }
            public string b64 { get; set; }
        }
        private sealed class UploadBody
        {
            public List<FileWire> files { get; set; }
        }

        [Route(HttpVerbs.Post, "/aog/shape")]
        public async Task UploadShape()
        {
            ShapefileUploadResult result;
            try
            {
                if (_shape == null)
                {
                    result = new ShapefileUploadResult { Ok = false, Error = "Servicio de shapefile no disponible." };
                    await WriteJsonAsync(result);
                    return;
                }

                string body = await ReadBodyAsync().ConfigureAwait(false);

                UploadBody parsed = null;
                try { parsed = AgpJson.Deserialize<UploadBody>(body); }
                catch (Exception ex)
                {
                    result = new ShapefileUploadResult { Ok = false, Error = "JSON inválido: " + ex.Message };
                    await WriteJsonAsync(result);
                    return;
                }

                if (parsed?.files == null || parsed.files.Count == 0)
                {
                    result = new ShapefileUploadResult { Ok = false, Error = "Body sin archivos." };
                    await WriteJsonAsync(result);
                    return;
                }

                var files = new List<ShapefileUploadFile>(parsed.files.Count);
                foreach (var f in parsed.files)
                {
                    if (f == null || string.IsNullOrEmpty(f.name) || string.IsNullOrEmpty(f.b64)) continue;
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(f.b64); }
                    catch (Exception ex)
                    {
                        result = new ShapefileUploadResult { Ok = false, Error = "Base64 inválido en " + f.name + ": " + ex.Message };
                        await WriteJsonAsync(result);
                        return;
                    }
                    files.Add(new ShapefileUploadFile(f.name, bytes));
                }

                result = await _shape.UploadAsync(files).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new ShapefileUploadResult { Ok = false, Error = "Excepción server: " + ex.Message };
            }
            await WriteJsonAsync(result);
        }

        [Route(HttpVerbs.Delete, "/aog/shape")]
        public async Task RemoveShape()
        {
            bool ok = false;
            string err = null;
            try
            {
                if (_shape == null) err = "Servicio no disponible.";
                else ok = await _shape.RemoveAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { err = ex.Message; }
            await WriteJsonAsync(new { ok, error = err });
        }
    }
}
