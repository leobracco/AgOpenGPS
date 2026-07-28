// ============================================================================
// FormGpsLotesService.cs
// Ubicación: SourceCode/GPS/AgroParallel/Common/FormGpsLotesService.cs
// Target: net48
//
// Implementación PilotX-side de ILotesService. Lee Fields/ del disco y delega
// las operaciones de open/close/create a FormGPS (única clase que puede
// abrir un job en PilotX). Marshalled vía Invoke a la UI thread cuando hace
// falta, ya que se invoca desde threads HTTP/WS de EmbedIO.
//
// Fase D · primer paso real de migración de UI a HTML. Replica el
// comportamiento de FormFieldDir/FormFieldExisting/FormJob sin abrirlas.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsLotesService : ILotesService
    {
        private readonly FormGPS _form;
        // Mismo regex que FormFieldDir.tboxFieldName_TextChanged usa para
        // validar nombre de lote (glm.fileRegex).
        private static readonly Regex InvalidChars = new Regex(@"[\\/:*?""<>|\.]");

        public FormGpsLotesService(FormGPS form)
        {
            _form = form ?? throw new ArgumentNullException(nameof(form));
        }

        public IList<FieldInfo> ListFields()
        {
            var result = new List<FieldInfo>();
            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            string current = null;
            try { current = _form.currentFieldDirectory; } catch { }

            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (!File.Exists(Path.Combine(di.FullName, "Field.txt"))) continue;

                    var info = new FieldInfo
                    {
                        Name = di.Name,
                        LastModifiedUtc = di.LastWriteTimeUtc,
                        IsCurrent = !string.IsNullOrEmpty(current) &&
                                    string.Equals(current, di.Name, StringComparison.OrdinalIgnoreCase),
                    };
                    string boundary = Path.Combine(di.FullName, "Boundary.txt");
                    if (File.Exists(boundary))
                    {
                        try
                        {
                            var lines = File.ReadAllLines(boundary);
                            info.HasBoundary = lines.Length > 2;
                            info.AreaHa = ComputeBoundaryAreaHa(lines);
                        }
                        catch { }
                    }
                    // Distancia al StartFix (línea 9 de Field.txt) — misma
                    // columna que FormJob (Drive In) / FormFieldExisting.
                    info.DistanceKm = ComputeStartFixDistanceKm(
                        Path.Combine(di.FullName, "Field.txt"));
                    // Hectáreas trabajadas: mismo cálculo que AOG al abrir el
                    // lote (SaveOpen.Designer: suma de triángulos de Sections).
                    info.WorkedHa = ComputeWorkedHa(di.FullName);
                    result.Add(info);
                }
                catch { /* skip broken dir */ }
            }
            // Orden: actual primero, después por fecha desc.
            result.Sort((a, b) =>
            {
                if (a.IsCurrent != b.IsCurrent) return a.IsCurrent ? -1 : 1;
                return b.LastModifiedUtc.CompareTo(a.LastModifiedUtc);
            });
            return result;
        }

        public string GetCurrentFieldName()
        {
            try
            {
                if (_form.isJobStarted) return _form.currentFieldDirectory;
            }
            catch { }
            return null;
        }

        public string GetCurrentFieldDirectory()
        {
            try
            {
                if (!_form.isJobStarted) return null;
                string root = RegistrySettings.fieldsDirectory;
                if (string.IsNullOrEmpty(root)) return null;
                if (string.IsNullOrEmpty(_form.currentFieldDirectory)) return null;
                return Path.Combine(root, _form.currentFieldDirectory);
            }
            catch { return null; }
        }

        public Task<bool> OpenFieldAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(false);

            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root)) return Task.FromResult(false);
            string dir = Path.Combine(root, name);
            string fieldFile = Path.Combine(dir, "Field.txt");
            if (!File.Exists(fieldFile)) return Task.FromResult(false);

            var tcs = new TaskCompletionSource<bool>();
            try
            {
                _form.BeginInvoke((MethodInvoker)(async () =>
                {
                    try
                    {
                        bool wasJobStarted = _form.isJobStarted;
                        string prevDir = _form.currentFieldDirectory;
                        if (_form.isJobStarted)
                        {
                            try { await _form.FileSaveEverythingBeforeClosingField(); } catch { }
                        }
                        // FileOpenField acepta la ruta completa al Field.txt como "openType".
                        _form.FileOpenField(fieldFile);
                        _form.Lotes_PostOpenFixup(wasJobStarted, prevDir);
                        tcs.TrySetResult(_form.isJobStarted);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Lotes] OpenFieldAsync: " + ex.Message);
                        tcs.TrySetResult(false);
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lotes] OpenFieldAsync invoke: " + ex.Message);
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        public Task<bool> CloseFieldAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                _form.BeginInvoke((MethodInvoker)(async () =>
                {
                    try
                    {
                        if (!_form.isJobStarted) { tcs.TrySetResult(true); return; }
                        await _form.FileSaveEverythingBeforeClosingField();
                        _form.Lotes_PostOpenFixup(wasJobStarted: false, prevFieldDir: null);
                        tcs.TrySetResult(!_form.isJobStarted);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Lotes] CloseFieldAsync: " + ex.Message);
                        tcs.TrySetResult(false);
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lotes] CloseFieldAsync invoke: " + ex.Message);
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        public Task<bool> DeleteFieldAsync(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(false);
                string root = RegistrySettings.fieldsDirectory;
                string dir = System.IO.Path.Combine(root, name);
                if (!System.IO.Directory.Exists(dir)) return Task.FromResult(false);
                // No borrar el lote abierto: cerrarlo antes desde la UI.
                if (_form.isJobStarted &&
                    string.Equals(_form.currentFieldDirectory, name, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(false);
                System.IO.Directory.Delete(dir, true);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lotes] DeleteFieldAsync: " + ex.Message);
                return Task.FromResult(false);
            }
        }

        public Task<bool> CreateFieldAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(false);
            string clean = InvalidChars.Replace(name, "").Trim();
            if (string.IsNullOrEmpty(clean)) return Task.FromResult(false);

            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root)) return Task.FromResult(false);
            string dir = Path.Combine(root, clean);
            if (Directory.Exists(dir)) return Task.FromResult(false);

            var tcs = new TaskCompletionSource<bool>();
            try
            {
                _form.BeginInvoke((MethodInvoker)(async () =>
                {
                    try
                    {
                        // Mismo flujo que FormFieldDir.btnSave_Click:
                        bool wasJobStarted = _form.isJobStarted;
                        string prevDir = _form.currentFieldDirectory;
                        if (_form.isJobStarted)
                        {
                            try { await _form.FileSaveEverythingBeforeClosingField(); } catch { }
                        }

                        _form.currentFieldDirectory = clean;
                        var dirNew = new DirectoryInfo(dir);

                        _form.JobNew();

                        if (dirNew.Exists)
                        {
                            tcs.TrySetResult(false);
                            return;
                        }

                        _form.pn.DefineLocalPlane(_form.AppModel.CurrentLatLon, false);
                        dirNew.Create();
                        _form.FileCreateField();
                        _form.FileCreateSections();
                        _form.FileCreateRecPath();
                        _form.FileCreateContour();
                        _form.FileCreateElevation();
                        _form.FileSaveFlags();
                        _form.FileCreateBoundary();

                        _form.Lotes_PostOpenFixup(wasJobStarted, prevDir);
                        tcs.TrySetResult(_form.isJobStarted);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Lotes] CreateFieldAsync: " + ex.Message);
                        tcs.TrySetResult(false);
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lotes] CreateFieldAsync invoke: " + ex.Message);
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        public Task<bool> CreateFromExistingAsync(string templateName, string newName,
                                                  bool copyApplied, bool copyFlags,
                                                  bool copyGuidance, bool copyHeadland)
        {
            if (string.IsNullOrWhiteSpace(templateName) || string.IsNullOrWhiteSpace(newName))
                return Task.FromResult(false);
            string clean = InvalidChars.Replace(newName, "").Trim();
            if (string.IsNullOrEmpty(clean)) return Task.FromResult(false);

            var tcs = new TaskCompletionSource<bool>();
            try
            {
                _form.BeginInvoke((MethodInvoker)(async () =>
                {
                    try
                    {
                        bool wasJobStarted = _form.isJobStarted;
                        string prevDir = _form.currentFieldDirectory;
                        if (_form.isJobStarted)
                        {
                            try { await _form.FileSaveEverythingBeforeClosingField(); } catch { }
                        }
                        bool ok = _form.Lotes_CreateFromExisting(
                            templateName, clean, copyApplied, copyFlags, copyGuidance, copyHeadland);
                        _form.Lotes_PostOpenFixup(wasJobStarted, prevDir);
                        tcs.TrySetResult(ok);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Lotes] CreateFromExistingAsync: " + ex.Message);
                        tcs.TrySetResult(false);
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lotes] CreateFromExistingAsync invoke: " + ex.Message);
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        /// <summary>Import por ruta (sin dialogo): en el host WinForms se sigue
        /// usando el dialogo nativo; esta variante la resuelve el motor headless,
        /// que es el backend de PilotX.Desktop.</summary>
        public Task<bool> ImportKmlAsync(string nombre, string rutaArchivo) => Task.FromResult(false);

        public Task<bool> ImportKmlAsync() => ImportNativeAsync(kml: true);

        public Task<bool> ImportIsoXmlAsync() => ImportNativeAsync(kml: false);

        // Diálogos nativos de import (ex FormJob DialogResult.No / Abort). El
        // ShowDialog corre en el hilo UI; el HTTP thread espera el resultado.
        private Task<bool> ImportNativeAsync(bool kml)
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                _form.BeginInvoke((MethodInvoker)(() =>
                {
                    try
                    {
                        bool wasJobStarted = _form.isJobStarted;
                        string prevDir = _form.currentFieldDirectory;
                        bool ok = kml ? _form.Lotes_ImportKml() : _form.Lotes_ImportIsoXml();
                        _form.Lotes_PostOpenFixup(wasJobStarted, prevDir);
                        tcs.TrySetResult(ok);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Lotes] ImportNativeAsync: " + ex.Message);
                        tcs.TrySetResult(false);
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lotes] ImportNativeAsync invoke: " + ex.Message);
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        // ── Helpers de lectura de archivos de lote ─────────────────────────────

        // Distancia km desde la posición actual al StartFix (línea 9 de
        // Field.txt, formato "lat,lon"). -1 si no se puede calcular.
        private double ComputeStartFixDistanceKm(string fieldFile)
        {
            try
            {
                var lines = File.ReadAllLines(fieldFile);
                if (lines.Length < 9) return -1;
                string[] words = lines[8].Split(',');
                if (words.Length < 2) return -1;
                double lat = double.Parse(words[0], CultureInfo.InvariantCulture);
                double lon = double.Parse(words[1], CultureInfo.InvariantCulture);
                var start = new Wgs84(lat, lon);
                return Math.Round(start.DistanceInKiloMeters(_form.AppModel.CurrentLatLon), 2);
            }
            catch { return -1; }
        }

        // Hectáreas trabajadas: replica el cálculo de AOG al abrir el lote
        // (SaveOpen.Designer.cs) — suma del área de los triángulos de cada
        // patch de Sections.txt. Reusa el parser portable SectionsFiles.Load.
        // 0 si no hay cobertura o el archivo no existe.
        private static double ComputeWorkedHa(string fieldDir)
        {
            try
            {
                if (!File.Exists(Path.Combine(fieldDir, "Sections.txt"))) return 0;
                var patches = AgOpenGPS.IO.SectionsFiles.Load(fieldDir);
                double m2 = 0;
                foreach (var patch in patches)
                {
                    int verts = patch.Count - 2;
                    for (int j = 1; j < verts; j++)
                    {
                        double temp = patch[j].easting * (patch[j + 1].northing - patch[j + 2].northing)
                                    + patch[j + 1].easting * (patch[j + 2].northing - patch[j].northing)
                                    + patch[j + 2].easting * (patch[j].northing - patch[j + 1].northing);
                        m2 += Math.Abs(temp * 0.5);
                    }
                }
                return Math.Round(m2 * 0.0001, 1);
            }
            catch { return 0; }
        }

        // Shoelace sobre Boundary.txt (mismo parseo tolerante a formatos viejos
        // que FormFieldExisting). Devuelve hectáreas; 0 si no hay contorno.
        private static double ComputeBoundaryAreaHa(string[] lines)
        {
            try
            {
                int i = 1; // saltear header "$Boundary"
                // Formatos viejos: hasta dos líneas True/False antes del count.
                while (i < lines.Length && (lines[i] == "True" || lines[i] == "False")) i++;
                if (i >= lines.Length) return 0;
                int numPoints = int.Parse(lines[i], CultureInfo.InvariantCulture);
                if (numPoints < 6 || i + numPoints >= lines.Length) return 0;

                double area = 0;
                double prevE = 0, prevN = 0, firstE = 0, firstN = 0;
                for (int p = 0; p < numPoints; p++)
                {
                    string[] words = lines[i + 1 + p].Split(',');
                    double e = double.Parse(words[0], CultureInfo.InvariantCulture);
                    double n = double.Parse(words[1], CultureInfo.InvariantCulture);
                    if (p == 0) { firstE = e; firstN = n; }
                    else area += (prevE + e) * (prevN - n);
                    prevE = e; prevN = n;
                }
                area += (prevE + firstE) * (prevN - firstN);
                return Math.Round(Math.Abs(area / 2) * 0.0001, 1);
            }
            catch { return 0; }
        }
    }
}
