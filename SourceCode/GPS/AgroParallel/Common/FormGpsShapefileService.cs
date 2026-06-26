// ============================================================================
// FormGpsShapefileService.cs
// Ubicación: SourceCode/GPS/AgroParallel/Common/FormGpsShapefileService.cs
// Target: net48
//
// Implementación PilotX-side de IShapefileService. Recibe los archivos crudos
// (.shp + .shx + .dbf + .prj/.cpg opcionales) desde el endpoint multipart,
// los persiste en <Fields>/<currentField>/Shapefile/ y dispara la carga real
// invocando FormGPS.LoadShapefileFromExternal en la UI thread.
//
// Por qué este servicio y no IAogCommandSink: el sink es whitelist estrecha
// (solo on/off de secciones, reload tool). Subir bytes a disco + parseo es
// otro concern. Se inyecta separado en AgpWebHost.
//
// Requisitos enforzados:
//   · Job abierto (currentFieldDirectory != null). PilotX no carga shape sin lote.
//   · El set debe incluir .shp + .shx + .dbf como mínimo. .prj/.cpg opcionales.
//   · Si hay un shapefile previo cargado en el mismo lote, los .shp/.shx/.dbf
//     viejos se sobreescriben (mantiene un único shape activo por lote).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsShapefileService : IShapefileService
    {
        private readonly FormGPS _form;
        private static readonly string[] RequiredExt = { ".shp", ".shx", ".dbf" };
        private static readonly string[] AcceptedExt = { ".shp", ".shx", ".dbf", ".prj", ".cpg" };

        public FormGpsShapefileService(FormGPS form)
        {
            _form = form ?? throw new ArgumentNullException(nameof(form));
        }

        public Task<ShapefileUploadResult> UploadAsync(IReadOnlyList<ShapefileUploadFile> files)
        {
            var result = new ShapefileUploadResult { Ok = false, Fields = new List<string>() };

            if (files == null || files.Count == 0)
            {
                result.Error = "No se recibieron archivos.";
                return Task.FromResult(result);
            }

            // Validar extensiones.
            var byExt = new Dictionary<string, ShapefileUploadFile>(StringComparer.OrdinalIgnoreCase);
            string baseName = null;
            foreach (var f in files)
            {
                if (f == null || string.IsNullOrEmpty(f.Name) || f.Bytes == null || f.Bytes.Length == 0)
                    continue;
                string ext = (Path.GetExtension(f.Name) ?? string.Empty).ToLowerInvariant();
                if (Array.IndexOf(AcceptedExt, ext) < 0) continue;
                byExt[ext] = f;
                if (ext == ".shp") baseName = Path.GetFileNameWithoutExtension(f.Name);
            }
            foreach (var req in RequiredExt)
            {
                if (!byExt.ContainsKey(req))
                {
                    result.Error = "Falta el archivo " + req + " (necesarios: .shp + .shx + .dbf).";
                    return Task.FromResult(result);
                }
            }
            if (string.IsNullOrEmpty(baseName))
            {
                result.Error = "No se pudo determinar el nombre base del shapefile.";
                return Task.FromResult(result);
            }

            // Lote activo.
            // OJO: currentFieldDirectory puede tener nombre seteado en
            // Settings.Default sin que haya un job realmente abierto. La carga
            // posterior (LoadShapefileFromPath) chequea isJobStarted y falla
            // silenciosa si no hay ActiveField. Doble validación acá:
            string fieldName = null;
            bool jobOpen = false;
            try { fieldName = _form.currentFieldDirectory; jobOpen = _form.isJobStarted; } catch { }
            if (string.IsNullOrEmpty(fieldName) || !jobOpen)
            {
                result.Error = "Abrí un lote en PilotX antes de subir un shapefile (no hay job activo).";
                return Task.FromResult(result);
            }
            string fieldsRoot = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(fieldsRoot))
            {
                result.Error = "No hay carpeta Fields configurada en PilotX.";
                return Task.FromResult(result);
            }
            string targetDir = Path.Combine(fieldsRoot, fieldName, "Shapefile");
            try { Directory.CreateDirectory(targetDir); }
            catch (Exception ex)
            {
                result.Error = "No se pudo crear " + targetDir + ": " + ex.Message;
                return Task.FromResult(result);
            }

            // Limpieza: borrar set previo bajo el mismo baseName (no toca otros archivos).
            foreach (var ext in AcceptedExt)
            {
                try
                {
                    string p = Path.Combine(targetDir, baseName + ext);
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { /* best-effort */ }
            }

            // Escribir bytes.
            string shpPath = null;
            try
            {
                foreach (var kv in byExt)
                {
                    string outPath = Path.Combine(targetDir, baseName + kv.Key);
                    File.WriteAllBytes(outPath, kv.Value.Bytes);
                    if (kv.Key == ".shp") shpPath = outPath;
                }
            }
            catch (Exception ex)
            {
                result.Error = "Error escribiendo archivos: " + ex.Message;
                return Task.FromResult(result);
            }

            // Invocar carga en UI thread del FormGPS. BeginInvoke para no bloquear
            // el thread HTTP — pero esperamos el resultado vía TCS porque la UI
            // espera saber si la capa quedó cargada.
            var tcs = new TaskCompletionSource<ShapefileUploadResult>();
            try
            {
                _form.BeginInvoke((MethodInvoker)(() =>
                {
                    try
                    {
                        string loadError;
                        bool ok = _form.LoadShapefileFromExternal(shpPath, out loadError);
                        var layer = _form.ShapefileLayerForAdapters;
                        result.Ok = ok && layer != null && !layer.IsEmpty;
                        result.FileName = Path.GetFileName(shpPath);
                        if (layer != null)
                        {
                            result.PolygonCount = layer.PolygonCount;
                            if (layer.FieldNames != null)
                                result.Fields = layer.FieldNames.ToList();
                        }
                        if (!result.Ok)
                        {
                            result.Error = !string.IsNullOrEmpty(loadError)
                                ? loadError
                                : "PilotX rechazó el shapefile (sin detalle).";
                        }
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        result.Ok = false;
                        result.Error = "Carga en UI thread falló: " + ex.Message;
                        tcs.TrySetResult(result);
                    }
                }));
            }
            catch (Exception ex)
            {
                result.Error = "No se pudo marshallear a UI thread: " + ex.Message;
                tcs.TrySetResult(result);
            }
            return tcs.Task;
        }

        public Task<bool> RemoveAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                _form.BeginInvoke((MethodInvoker)(() =>
                {
                    try
                    {
                        _form.ClearShapefileLayer();
                        // Borrar persistencia del lote para que al reabrir no se re-cargue.
                        string fieldName = _form.currentFieldDirectory;
                        if (!string.IsNullOrEmpty(fieldName) && !string.IsNullOrEmpty(RegistrySettings.fieldsDirectory))
                        {
                            string fieldDir = Path.Combine(RegistrySettings.fieldsDirectory, fieldName);
                            AgroParallel.Common.ShapefilePersistence.Delete(fieldDir);
                        }
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Shapefile] Remove: " + ex);
                        tcs.TrySetResult(false);
                    }
                }));
            }
            catch
            {
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }
    }
}
