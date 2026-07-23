// ============================================================================
// EngineLotesService.cs — adaptador ILotesService sobre GuidanceEngineHost.
// Gemelo headless de FormGpsLotesService (GPS/AgroParallel/Common): mismo
// listado/apertura/cierre de lote, pero contra el host net9.0 en vez de
// FormGPS. Puerto directo de PilotX.Android/GuidanceEngineServices.cs
// (GuidanceEngineLotesService) — esa clase ya era 100% portable (sin ninguna
// API de Android), verificada en runtime real en el emulador; acá solo cambia
// el namespace/ubicación para que PilotX.Desktop la use vía EngineWebHost.
//
// Abrir/cerrar lote reusa GuidanceEngineHost.Job.cs (OpenField/CloseField),
// ya hecho y verificado desde el bloque 14. Crear/borrar/importar quedan en
// false (mismo comportamiento que el stub que reemplazan, no regresión):
// necesitan portar FileCreateField y el resto de SaveOpen.Designer.cs, que no
// es parte de este pedido (item 3 del PEDIDO taller).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineLotesService : ILotesService
    {
        private readonly GuidanceEngineHost _host;

        public EngineLotesService(GuidanceEngineHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public IList<FieldInfo> ListFields()
        {
            var result = new List<FieldInfo>();
            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            string current = null;
            try { current = _host.currentFieldDirectory; } catch { }

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
                    result.Add(info);
                }
                catch { /* skip broken dir */ }
            }
            result.Sort((a, b) =>
            {
                if (a.IsCurrent != b.IsCurrent) return a.IsCurrent ? -1 : 1;
                return b.LastModifiedUtc.CompareTo(a.LastModifiedUtc);
            });
            return result;
        }

        public string GetCurrentFieldName()
            => _host.IsJobStarted ? _host.currentFieldDirectory : null;

        public string GetCurrentFieldDirectory()
        {
            if (!_host.IsJobStarted) return null;
            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(_host.currentFieldDirectory)) return null;
            return Path.Combine(root, _host.currentFieldDirectory);
        }

        public Task<bool> OpenFieldAsync(string name)
            => Task.FromResult(_host.OpenField(name));

        public Task<bool> CloseFieldAsync()
        {
            _host.CloseField();
            return Task.FromResult(true);
        }

        // Crear/borrar/importar lotes todavía no están portados al guidance
        // engine headless (necesitan el flujo completo de FileCreateField +
        // los demás FileCreate* de SaveOpen.Designer.cs) — mismo comportamiento
        // que el stub que reemplazan (false), no una regresión.
        public Task<bool> DeleteFieldAsync(string name) => Task.FromResult(false);
        public Task<bool> CreateFieldAsync(string name) => Task.FromResult(false);
        public Task<bool> CreateFromExistingAsync(string templateName, string newName,
                                                  bool copyApplied, bool copyFlags,
                                                  bool copyGuidance, bool copyHeadland)
            => Task.FromResult(false);
        public Task<bool> ImportKmlAsync() => Task.FromResult(false);
        public Task<bool> ImportIsoXmlAsync() => Task.FromResult(false);

        // Shoelace sobre Boundary.txt — copiado de FormGpsLotesService (mismo
        // parseo tolerante a formatos viejos: hasta 2 líneas True/False antes
        // del count).
        private static double ComputeBoundaryAreaHa(string[] lines)
        {
            try
            {
                int i = 1; // saltear header "$Boundary"
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
