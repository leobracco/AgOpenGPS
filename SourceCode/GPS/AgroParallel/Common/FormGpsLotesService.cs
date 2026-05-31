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
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
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
                        }
                        catch { }
                    }
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
                        if (_form.isJobStarted)
                        {
                            try { await _form.FileSaveEverythingBeforeClosingField(); } catch { }
                        }
                        // FileOpenField acepta la ruta completa al Field.txt como "openType".
                        _form.FileOpenField(fieldFile);
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
    }
}
