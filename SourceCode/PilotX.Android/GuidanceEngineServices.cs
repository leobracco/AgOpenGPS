// ============================================================================
// GuidanceEngineServices.cs
// Implementaciones reales de IGuidanceCalculator/ILotesService respaldadas
// por PilotX.GuidanceEngine.Core.GuidanceEngineHost (bloque 14), en vez de
// los stubs de Fase 1 (StubGuidanceCalculator/StubLotesService en
// Fase1Stubs.cs). Mismo patrón que FormGpsGuidanceCalculator/
// FormGpsLotesService (carril taller, GPS/AgroParallel/Common/) pero
// envolviendo GuidanceEngineHost en vez de FormGPS.
//
// Todavía NO hay fix GPS real llegando a este proceso en Android (eso
// necesita CoreX/serial por USB-OTG, bloque 8 pendiente de hardware): estas
// clases sirven para que el Hub pueda abrir/cerrar lotes y ejecutar comandos
// de guiado (autosteer/uturn/pick) de verdad ya, en vez de contra un stub
// que siempre devuelve vacío/false.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using PilotXCore = global::AgOpenGPS;

namespace PilotX.Droid
{
    internal sealed class GuidanceEngineGuidanceCalculator : IGuidanceCalculator
    {
        private readonly PilotXCore.GuidanceEngineHost _engine;

        // Cache de revision, mismo criterio que FormGpsGuidanceCalculator:
        // el cliente HTTP salta el re-upload del VBO cuando no cambio nada.
        private string _lastMode = "Off";
        private int _lastCount;
        private long _revision;

        public GuidanceEngineGuidanceCalculator(PilotXCore.GuidanceEngineHost engine)
        {
            _engine = engine;
        }

        public GuidanceSnapshot GetSnapshot()
        {
            var snap = new GuidanceSnapshot
            {
                Mode = "Off",
                IsLineSet = false,
                IsAutoSteerOn = false,
                XteMeters = 0,
                HeadingErrorRad = 0,
                SteerAngleCommandDeg = 0,
                DistanceToEndM = -1,
                LookAhead = null
            };
            if (_engine == null) return snap;

            try
            {
                snap.IsAutoSteerOn = _engine.isBtnAutoSteerOn;

                // guidanceLineDistanceOff = 32000 -> "sin guidance" (sentinel PilotX).
                short raw = _engine.guidanceLineDistanceOff;
                if (raw != 32000) snap.XteMeters = raw / 1000.0; // mm -> m

                snap.SteerAngleCommandDeg = _engine.guidanceLineSteerAngle / 100.0;

                bool ab = _engine.ABLineField != null && _engine.ABLineField.isABValid;
                bool cu = _engine.CurveField != null && _engine.CurveField.isCurveValid;
                bool ct = _engine.Ct != null && _engine.Ct.isContourBtnOn;

                if (ab) { snap.Mode = "AB"; snap.IsLineSet = true; }
                else if (cu) { snap.Mode = "Curve"; snap.IsLineSet = true; }
                else if (ct) { snap.Mode = "Contour"; snap.IsLineSet = true; }
                else { snap.Mode = "Off"; snap.IsLineSet = false; }
            }
            catch
            {
                // defensivo: jamas romper el snapshot por estado parcial.
            }

            return snap;
        }

        public GuidanceGeometrySnapshot GetGeometry()
        {
            var geom = new GuidanceGeometrySnapshot
            {
                Mode = "Off",
                Points = new List<FieldPoint>(),
                Revision = _revision
            };
            if (_engine == null) return geom;

            try
            {
                bool ab = _engine.ABLineField != null && _engine.ABLineField.isABValid;
                bool cu = _engine.CurveField != null && _engine.CurveField.isCurveValid;
                bool ct = _engine.Ct != null && _engine.Ct.isContourBtnOn;

                if (ab)
                {
                    geom.Mode = "AB";
                    geom.Points.Add(new FieldPoint(_engine.ABLineField.currentLinePtA.easting, _engine.ABLineField.currentLinePtA.northing));
                    geom.Points.Add(new FieldPoint(_engine.ABLineField.currentLinePtB.easting, _engine.ABLineField.currentLinePtB.northing));
                }
                else if (cu)
                {
                    geom.Mode = "Curve";
                    var src = _engine.CurveField.curList;
                    if (src != null)
                    {
                        for (int i = 0; i < src.Count; i++)
                            geom.Points.Add(new FieldPoint(src[i].easting, src[i].northing));
                    }
                }
                else if (ct)
                {
                    geom.Mode = "Contour";
                    var src = _engine.Ct.ctList;
                    if (src != null)
                    {
                        for (int i = 0; i < src.Count; i++)
                            geom.Points.Add(new FieldPoint(src[i].easting, src[i].northing));
                    }
                }

                if (geom.Mode != _lastMode || geom.Points.Count != _lastCount)
                {
                    _revision++;
                    _lastMode = geom.Mode;
                    _lastCount = geom.Points.Count;
                }
                geom.Revision = _revision;
            }
            catch
            {
                geom.Mode = "Off";
                geom.Points = new List<FieldPoint>();
                geom.Revision = _revision;
            }

            return geom;
        }

        public bool ExecuteCommand(string command)
        {
            try { return _engine != null && _engine.ExecuteCommand(command); }
            catch { return false; }
        }
    }

    internal sealed class GuidanceEngineLotesService : ILotesService
    {
        private readonly PilotXCore.GuidanceEngineHost _engine;

        public GuidanceEngineLotesService(PilotXCore.GuidanceEngineHost engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public IList<FieldInfo> ListFields()
        {
            var result = new List<FieldInfo>();
            string root = PilotXCore.RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            string current = null;
            try { current = _engine.currentFieldDirectory; } catch { }

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
            => _engine.IsJobStarted ? _engine.currentFieldDirectory : null;

        public string GetCurrentFieldDirectory()
        {
            if (!_engine.IsJobStarted) return null;
            string root = PilotXCore.RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(_engine.currentFieldDirectory)) return null;
            return Path.Combine(root, _engine.currentFieldDirectory);
        }

        public Task<bool> OpenFieldAsync(string name)
            => Task.FromResult(_engine.OpenField(name));

        public Task<bool> CloseFieldAsync()
        {
            _engine.CloseField();
            return Task.FromResult(true);
        }

        // Crear/borrar/importar lotes todavia no estan portados al guidance
        // engine headless (necesitan el flujo completo de FileCreateField +
        // los demas FileCreate* de SaveOpen.Designer.cs, ver GPS/Forms/
        // SaveOpen.Designer.cs) — mismo comportamiento que el stub que
        // reemplazan (false), no una regresion.
        public Task<bool> DeleteFieldAsync(string name) => Task.FromResult(false);
        public Task<bool> CreateFieldAsync(string name) => Task.FromResult(false);
        public Task<bool> CreateFromExistingAsync(string templateName, string newName,
                                                  bool copyApplied, bool copyFlags,
                                                  bool copyGuidance, bool copyHeadland)
            => Task.FromResult(false);
        public Task<bool> ImportKmlAsync() => Task.FromResult(false);
        public Task<bool> ImportIsoXmlAsync() => Task.FromResult(false);

        // Shoelace sobre Boundary.txt — copiado de FormGpsLotesService (mismo
        // parseo tolerante a formatos viejos: hasta 2 lineas True/False antes
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
