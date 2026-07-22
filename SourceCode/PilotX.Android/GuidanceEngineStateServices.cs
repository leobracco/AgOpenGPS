// ============================================================================
// GuidanceEngineStateServices.cs
// Segunda tanda de stubs Fase 1 reemplazados por implementaciones reales
// respaldadas por GuidanceEngineHost (bloque 14): IAogStateProvider (solo
// GetSnapshot/GetEventLog — el resto queda pendiente, ver nota abajo),
// ICoverageService, ISectionControlService, IVehicleToolService e
// IQuantiXRuntimeService. Mismo patron que GuidanceEngineServices.cs:
// copian la logica de los adaptadores FormGps*.cs (carril taller,
// GPS/AgroParallel/Common/) pero contra GuidanceEngineHost en vez de FormGPS.
//
// IAogStateProvider.GetSnapshot() es el mas grande e importante (alimenta el
// dashboard principal del Hub): posicion, velocidad, area trabajada, estado
// de autosteer/secciones/track/youturn/contour, boundary/headland,
// hidraulico, tram. Los demas metodos de la interfaz (GetAllSettings —
// volcado completo "todos los ajustes", los 4 graficos en vivo XTE/heading/
// steer/correccion, ShiftPos, SimCoords, colores de seccion/display,
// shapefile) NO se portaron en esta pasada — devuelven el mismo default
// vacio que el stub que reemplazan (no es una regresion, es alcance
// pendiente: los graficos necesitan buffers rodantes que no existen todavia
// en el motor headless, y shapefile es una capa que GuidanceEngineHost no
// carga en absoluto).
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.QuantiX;
using AgroParallel.Services.Abstractions;
using PilotXCore = global::AgOpenGPS;

namespace PilotX.Droid
{
    internal sealed class GuidanceEngineStateProvider : IAogStateProvider
    {
        private readonly PilotXCore.GuidanceEngineHost _engine;

        public GuidanceEngineStateProvider(PilotXCore.GuidanceEngineHost engine)
        {
            _engine = engine;
        }

        public AogStateSnapshot GetSnapshot()
        {
            var snap = new AogStateSnapshot();
            if (_engine == null) return snap;

            try
            {
                snap.IsJobStarted = _engine.IsJobStarted;
                snap.CurrentFieldDirectory = _engine.currentFieldDirectory;
                snap.FieldsDirectory = PilotXCore.RegistrySettings.fieldsDirectory;
                snap.AvgSpeed = _engine.avgSpeed;
                snap.FixQuality = _engine.Pn != null ? _engine.Pn.fixQuality : 0;
                // No hay equivalente portable a SystemInformation.PowerStatus
                // (WinForms) en este proceso; Android tiene su propia API de
                // batería (BatteryManager) que vive en la capa nativa, no acá.
                snap.PowerOnline = true;
                snap.Heading = _engine.pivotAxlePos.heading;
                snap.PivotEasting = _engine.pivotAxlePos.easting;
                snap.PivotNorthing = _engine.pivotAxlePos.northing;

                if (_engine.AppModelField != null)
                {
                    snap.Latitude = _engine.AppModelField.CurrentLatLon.Latitude;
                    snap.Longitude = _engine.AppModelField.CurrentLatLon.Longitude;
                }

                snap.IsAutoSteerOn = _engine.isBtnAutoSteerOn;
                snap.IsSectionAutoOn = _engine.autoBtnState == PilotXCore.btnStates.Auto;
                snap.IsSectionManualOn = _engine.manualBtnState == PilotXCore.btnStates.On;
                if (_engine.Trk != null)
                {
                    snap.IsAutoSnapToPivot = _engine.Trk.isAutoSnapToPivot;
                    snap.IsAutoTrackOn = _engine.Trk.isAutoTrack;
                    snap.TrackIdx = _engine.Trk.idx;
                    if (_engine.Trk.gArr != null)
                    {
                        snap.TracksTotal = _engine.Trk.gArr.Count;
                        int vis = 0;
                        for (int i = 0; i < _engine.Trk.gArr.Count; i++)
                            if (_engine.Trk.gArr[i].isVisible) vis++;
                        snap.TracksVisible = vis;
                    }
                }
                if (_engine.Yt != null) snap.IsYouTurnOn = _engine.Yt.isYouTurnBtnOn;
                if (_engine.Ct != null)
                {
                    snap.IsContourOn = _engine.Ct.isContourBtnOn;
                    snap.IsContourLocked = _engine.Ct.isLocked;
                }
                if (_engine.Isobus != null)
                {
                    snap.IsobusAlive = _engine.Isobus.IsAlive();
                    snap.IsobusOn = _engine.Isobus.SectionControlEnabled;
                }
                snap.HasBoundary = _engine.Bnd != null && _engine.Bnd.bndList != null
                    && _engine.Bnd.bndList.Count > 0;

                snap.FlagColor = _engine.flagColor;
                snap.IsNudgeOn = _engine.isNudgeOn;
                if (_engine.Bnd != null)
                {
                    snap.HasHeadland = _engine.Bnd.bndList != null && _engine.Bnd.bndList.Count > 0
                        && _engine.Bnd.bndList[0].hdLine != null && _engine.Bnd.bndList[0].hdLine.Count > 0;
                    snap.IsHeadlandOn = _engine.Bnd.isHeadlandOn;
                    snap.IsSectionControlledByHeadland = _engine.Bnd.isSectionControlledByHeadland;
                }
                snap.HasHydLift = (PilotXCore.Properties.Settings.Default.setArdMac_setting0 & 2) == 2;
                if (_engine.Vehicle != null) snap.IsHydLiftOn = _engine.Vehicle.isHydLiftOn;
                if (_engine.Tram != null)
                {
                    snap.HasTram = (_engine.Tram.tramList != null ? _engine.Tram.tramList.Count : 0)
                        + (_engine.Tram.tramBndOuterArr != null ? _engine.Tram.tramBndOuterArr.Count : 0) > 0;
                    snap.TramDisplayMode = (int)_engine.Tram.displayMode;
                }
                if (_engine.Yt != null)
                {
                    snap.YouSkipMode = (int)_engine.Yt.skipMode;
                    snap.RowSkipsWidth = _engine.Yt.rowSkipsWidth;
                }

                snap.ToolEasting = _engine.toolPos.easting;
                snap.ToolNorthing = _engine.toolPos.northing;
                snap.ToolHeading = _engine.toolPos.heading;

                if (_engine.Fd != null)
                {
                    snap.WorkedAreaTotalM2 = _engine.Fd.workedAreaTotal;
                    snap.ActualAreaCoveredM2 = _engine.Fd.actualAreaCovered;
                }

                if (_engine.Tool != null)
                {
                    int n = _engine.Tool.numOfSections;
                    snap.NumSections = n;
                    snap.ToolWidth = _engine.Tool.width;
                    snap.ToolOffset = _engine.Tool.offset;
                    if (n > 0 && _engine.Sections != null)
                    {
                        var arr = new bool[n];
                        var pos = new List<SectionExtent>(n);
                        var speeds = new double[n];
                        for (int i = 0; i < n && i < _engine.Sections.Length; i++)
                        {
                            var sec = _engine.Sections[i];
                            arr[i] = sec != null && sec.sectionOnRequest;
                            if (sec != null)
                            {
                                pos.Add(new SectionExtent(i, sec.positionLeft, sec.positionRight));
                                speeds[i] = sec.speedPixels * 0.36;
                            }
                        }
                        snap.SectionOnRequest = arr;
                        snap.SectionPositions = pos;
                        snap.SectionSpeedsKmh = speeds;
                    }
                    snap.ToolFarLeftSpeedKmh = _engine.Tool.farLeftSpeed * 3.6;
                    snap.ToolFarRightSpeedKmh = _engine.Tool.farRightSpeed * 3.6;
                }

                // Shapefile: GuidanceEngineHost no carga esa capa todavia (out
                // of scope, ver StubShapefileService) — queda en 0/false.

                try
                {
                    if (_engine.Bnd != null && _engine.Bnd.bndList != null)
                    {
                        var bnds = new List<List<FieldPoint>>();
                        var hdls = new List<List<FieldPoint>>();
                        foreach (var b in _engine.Bnd.bndList)
                        {
                            if (b == null) continue;
                            bnds.Add(DecimateVec3(b.fenceLine, 0.5));
                            hdls.Add(DecimateVec3(b.hdLine, 0.5));
                        }
                        snap.Boundaries = bnds;
                        snap.Headlands = hdls;

                        if (_engine.Bnd.bndList.Count > 0)
                        {
                            double areaM2 = _engine.Bnd.bndList[0].area;
                            for (int i = 1; i < _engine.Bnd.bndList.Count; i++)
                                areaM2 -= _engine.Bnd.bndList[i].area;
                            snap.BoundaryAreaM2 = areaM2;
                        }
                    }

                    if (_engine.Trk != null && _engine.Trk.gArr != null
                        && _engine.Trk.idx >= 0 && _engine.Trk.idx < _engine.Trk.gArr.Count)
                    {
                        var t = _engine.Trk.gArr[_engine.Trk.idx];
                        if (t != null)
                        {
                            snap.ActiveTrack = new TrackInfo
                            {
                                Name = t.name,
                                Mode = t.mode.ToString(),
                                Heading = t.heading,
                                A = new FieldPoint(t.ptA.easting, t.ptA.northing),
                                B = new FieldPoint(t.ptB.easting, t.ptB.northing),
                                CurvePts = DecimateVec3(t.curvePts, 1.0)
                            };
                        }
                    }
                }
                catch { /* no romper snapshot por geometria */ }

                try
                {
                    var s = PilotXCore.Properties.Settings.Default;
                    switch (s.setVehicle_vehicleType)
                    {
                        case 0: snap.VehicleType = "Tractor"; snap.VehicleBrand = s.setBrand_TBrand.ToString(); break;
                        case 1: snap.VehicleType = "Harvester"; snap.VehicleBrand = s.setBrand_HBrand.ToString(); break;
                        case 2: snap.VehicleType = "Articulated"; snap.VehicleBrand = s.setBrand_WDBrand.ToString(); break;
                        default: snap.VehicleType = "Tractor"; snap.VehicleBrand = "AGOpenGPS"; break;
                    }
                }
                catch { snap.VehicleType = "Tractor"; snap.VehicleBrand = "AGOpenGPS"; }
            }
            catch
            {
                // Defensivo: jamas romper a un service por estado parcial.
            }

            return snap;
        }

        // Los que siguen no se portaron en esta pasada (ver nota de cabecera):
        // devuelven el mismo default vacio que StubAogStateProvider.
        public AllSettingsSnapshot GetAllSettings() => new AllSettingsSnapshot();
        public XteGraphSample GetXteGraphSample() => new XteGraphSample();
        public HeadingGraphSample GetHeadingGraphSample() => new HeadingGraphSample();
        public SteerGraphSample GetSteerGraphSample() => new SteerGraphSample();
        public CorrectionGraphSample GetCorrectionGraphSample() => new CorrectionGraphSample();
        public ShiftPosSnapshot GetShiftPos() => new ShiftPosSnapshot();
        public SimCoordsSnapshot GetSimCoords() => new SimCoordsSnapshot();
        public SectionColorsSnapshot GetSectionColors() => new SectionColorsSnapshot();
        public DisplayColorsSnapshot GetDisplayColors() => new DisplayColorsSnapshot();
        public double GetShapeFieldDose(string fieldName) => 0;
        public ShapeSnapshot GetShape() => null;
        public ShapeFieldsSnapshot GetShapeFields() => new ShapeFieldsSnapshot();

        public EventLogSnapshot GetEventLog()
        {
            string path = System.IO.Path.Combine(
                PilotXCore.RegistrySettings.logsDirectory, "AgOpenGPS_Events_Log.txt");

            const int maxBytes = 256 * 1024;
            string history = "";
            try
            {
                if (System.IO.File.Exists(path))
                {
                    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                        System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    {
                        bool truncado = fs.Length > maxBytes;
                        if (truncado) fs.Seek(-maxBytes, System.IO.SeekOrigin.End);
                        using (var sr = new System.IO.StreamReader(fs))
                            history = sr.ReadToEnd();

                        if (truncado)
                        {
                            int nl = history.IndexOf('\n');
                            if (nl >= 0 && nl + 1 < history.Length)
                                history = history.Substring(nl + 1);
                            history = "(…mostrando el final del log; el archivo completo queda en "
                                + path + ")\n\n" + history;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                history = "(no se pudo leer el archivo de log: " + ex.Message + ")";
            }

            // Sin hilo de UI que marshalar: leemos Log.sbEvents directo (mismo
            // riesgo de concurrencia que ya asumia la version FormGPS al
            // marshalar — sbEvents no tiene lock propio).
            string session;
            try { session = AgLibrary.Logging.Log.sbEvents.ToString(); }
            catch (Exception ex) { session = "(no se pudo leer la sesión: " + ex.Message + ")"; }

            return new EventLogSnapshot
            {
                File = path,
                History = history,
                Session = session,
            };
        }

        private static List<FieldPoint> DecimateVec3(List<PilotXCore.vec3> src, double minStepM)
        {
            var dst = new List<FieldPoint>();
            if (src == null || src.Count == 0) return dst;
            var last = src[0];
            dst.Add(new FieldPoint(last.easting, last.northing));
            double step2 = minStepM * minStepM;
            for (int i = 1; i < src.Count; i++)
            {
                var p = src[i];
                double dx = p.easting - last.easting;
                double dy = p.northing - last.northing;
                if (dx * dx + dy * dy >= step2)
                {
                    dst.Add(new FieldPoint(p.easting, p.northing));
                    last = p;
                }
            }
            return dst;
        }
    }

    internal sealed class GuidanceEngineCoverageService : ICoverageService
    {
        private readonly PilotXCore.GuidanceEngineHost _engine;

        public GuidanceEngineCoverageService(PilotXCore.GuidanceEngineHost engine)
        {
            _engine = engine;
        }

        public CoverageSnapshot GetSnapshot()
        {
            var snap = new CoverageSnapshot
            {
                FieldDirectory = _engine != null ? _engine.currentFieldDirectory : null,
                Sections = new List<CoverageSection>()
            };
            if (_engine == null || _engine.TriStripField == null) return snap;

            long rev = 0;
            for (int j = 0; j < _engine.TriStripField.Count; j++)
            {
                var strip = _engine.TriStripField[j];
                var sec = new CoverageSection
                {
                    Index = j,
                    Enabled = strip != null && strip.isDrawing,
                    Strips = new List<CoverageStrip>()
                };
                if (strip != null && strip.patchList != null)
                {
                    for (int k = 0; k < strip.patchList.Count; k++)
                    {
                        var tri = strip.patchList[k];
                        // patchList[k][0] es un HEADER (contador+color), no una
                        // coordenada — la geometria real arranca en [1] (mismo
                        // bug ya resuelto del lado FormGpsCoverageService).
                        if (tri == null || tri.Count < 4) continue;
                        var cs = new CoverageStrip { Vertices = new List<CoverageVertex>(tri.Count - 1) };
                        for (int v = 1; v < tri.Count; v++)
                            cs.Vertices.Add(new CoverageVertex(tri[v].easting, tri[v].northing));
                        sec.Strips.Add(cs);
                        rev += tri.Count;
                    }
                }
                snap.Sections.Add(sec);
            }
            snap.Revision = rev;
            return snap;
        }

        public void Reset()
        {
            if (_engine == null || _engine.TriStripField == null) return;
            for (int j = 0; j < _engine.TriStripField.Count; j++)
                _engine.TriStripField[j]?.patchList?.Clear();
        }
    }

    internal sealed class GuidanceEngineSectionControlService : ISectionControlService
    {
        private readonly PilotXCore.GuidanceEngineHost _engine;

        public GuidanceEngineSectionControlService(PilotXCore.GuidanceEngineHost engine)
        {
            _engine = engine;
        }

        public SectionControlSnapshot GetSnapshot()
        {
            var snap = new SectionControlSnapshot
            {
                NumSections = 0,
                OnRequest = Array.Empty<bool>(),
                IsAuto = false,
                IsManualOn = false
            };
            if (_engine == null) return snap;

            int n = _engine.Tool != null ? _engine.Tool.numOfSections : 0;
            snap.NumSections = n;
            if (n > 0 && _engine.Sections != null)
            {
                var arr = new bool[n];
                for (int i = 0; i < n && i < _engine.Sections.Length; i++)
                {
                    var sec = _engine.Sections[i];
                    arr[i] = sec != null && sec.sectionOnRequest;
                }
                snap.OnRequest = arr;
            }
            snap.IsAuto = _engine.autoBtnState == PilotXCore.btnStates.Auto;
            snap.IsManualOn = _engine.manualBtnState == PilotXCore.btnStates.On;
            return snap;
        }
    }

    internal sealed class GuidanceEngineVehicleToolService : IVehicleToolService
    {
        private readonly PilotXCore.GuidanceEngineHost _engine;

        public GuidanceEngineVehicleToolService(PilotXCore.GuidanceEngineHost engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public VehicleConfigDto GetVehicle()
        {
            var s = PilotXCore.Properties.Settings.Default;
            return new VehicleConfigDto
            {
                VehicleType = s.setVehicle_vehicleType,
                Wheelbase = s.setVehicle_wheelbase,
                TrackWidth = s.setVehicle_trackWidth,
                AntennaHeight = s.setVehicle_antennaHeight,
                AntennaPivot = s.setVehicle_antennaPivot,
                AntennaOffset = s.setVehicle_antennaOffset,
                MaxSteerAngle = s.setVehicle_maxSteerAngle,
                SlowSpeedCutoff = s.setVehicle_slowSpeedCutoff
            };
        }

        public ToolConfigDto GetTool()
        {
            var s = PilotXCore.Properties.Settings.Default;
            bool sectionsMode = s.setTool_isSectionsNotZones;
            int numSec = sectionsMode ? s.setVehicle_numSections : s.setTool_numSectionsMulti;

            double[] widths = null;
            if (sectionsMode)
            {
                decimal[] pos = ReadSectionPositions(s);
                int n = Math.Max(1, Math.Min(16, numSec));
                widths = new double[n];
                for (int i = 0; i < n; i++)
                    widths[i] = Math.Abs((double)(pos[i + 1] - pos[i]));
            }

            int zones = 0; int[] zoneRanges = null;
            try
            {
                string[] words = (s.setTool_zones ?? "").Split(',');
                if (words.Length > 0) int.TryParse(words[0], out zones);
                if (zones > 0)
                {
                    zoneRanges = new int[zones];
                    for (int i = 1; i <= zones && i < words.Length; i++)
                        int.TryParse(words[i], out zoneRanges[i - 1]);
                }
            }
            catch { /* string corrupta: la UI cae a defaults */ }

            return new ToolConfigDto
            {
                Width = s.setVehicle_toolWidth,
                Overlap = s.setVehicle_toolOverlap,
                Offset = s.setVehicle_toolOffset,
                NumSections = numSec,
                IsSectionsNotZones = sectionsMode,
                SectionWidths = widths,
                SectionWidthMulti = s.setTool_sectionWidthMulti,
                Zones = zones,
                ZoneRanges = zoneRanges,
                HitchLength = s.setVehicle_hitchLength,
                TrailingHitchLength = s.setTool_toolTrailingHitchLength,
                TrailingToolToPivotLength = s.setTool_trailingToolToPivotLength,
                LookAheadOn = s.setVehicle_toolLookAheadOn,
                LookAheadOff = s.setVehicle_toolLookAheadOff,
                TurnOffDelay = s.setVehicle_toolOffDelay,
                IsToolTrailing = s.setTool_isToolTrailing,
                IsToolTBT = s.setTool_isToolTBT,
                IsToolRearFixed = s.setTool_isToolRearFixed,
                IsToolFrontFixed = s.setTool_isToolFront,
                IsSectionOffWhenOut = s.setTool_isSectionOffWhenOut
            };
        }

        public VehicleToolBundleDto GetBundle()
            => new VehicleToolBundleDto { Vehicle = GetVehicle(), Tool = GetTool() };

        public bool SaveVehicle(VehicleConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = PilotXCore.Properties.Settings.Default;
                s.setVehicle_vehicleType = ClampInt(cfg.VehicleType, 0, 2);
                s.setVehicle_wheelbase = ClampD(cfg.Wheelbase, 0.5, 20.0);
                s.setVehicle_trackWidth = ClampD(cfg.TrackWidth, 0.5, 10.0);
                s.setVehicle_antennaHeight = ClampD(cfg.AntennaHeight, 0.1, 10.0);
                s.setVehicle_antennaPivot = ClampD(cfg.AntennaPivot, -10.0, 10.0);
                s.setVehicle_antennaOffset = ClampD(cfg.AntennaOffset, -5.0, 5.0);
                s.setVehicle_maxSteerAngle = ClampD(cfg.MaxSteerAngle, 5.0, 60.0);
                s.setVehicle_slowSpeedCutoff = ClampD(cfg.SlowSpeedCutoff, 0.0, 10.0);
                s.Save();

                // Sin hilo de UI que marshalar: reload directo. Opacity/Color/
                // IsImage no viven en el ctor (mismo gotcha que FormGPS) — se
                // reaplican despues de construir.
                try
                {
                    _engine.Vehicle = new PilotXCore.CVehicle(_engine);
                    _engine.Vehicle.VehicleConfig.Opacity = s.setDisplay_vehicleOpacity * 0.01;
                    _engine.Vehicle.VehicleConfig.Color =
                        (AgOpenGPS.Core.Models.ColorRgba)PilotXCore.ColorExtensions.CheckColorFor255(s.setDisplay_colorVehicle);
                    _engine.Vehicle.VehicleConfig.IsImage = s.setDisplay_isVehicleImage;
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        public bool SaveTool(ToolConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = PilotXCore.Properties.Settings.Default;
                s.setVehicle_toolOverlap = ClampD(cfg.Overlap, -2.0, 2.0);
                s.setVehicle_toolOffset = ClampD(cfg.Offset, -10.0, 10.0);

                s.setTool_isSectionsNotZones = cfg.IsSectionsNotZones;
                if (cfg.IsSectionsNotZones)
                {
                    int n = ClampInt(cfg.NumSections, 1, 16);
                    s.setVehicle_numSections = n;

                    double defW = n > 0 && cfg.Width > 0 ? cfg.Width / n : 0.5;
                    decimal[] w = new decimal[16];
                    for (int i = 0; i < 16; i++)
                    {
                        double wi = (cfg.SectionWidths != null && i < cfg.SectionWidths.Length && cfg.SectionWidths[i] > 0)
                            ? cfg.SectionWidths[i] : defW;
                        w[i] = (decimal)ClampD(wi, 0.01, 10.0);
                    }

                    decimal total = 0;
                    for (int j = 0; j < n; j++) total += w[j];

                    decimal[] pos = new decimal[17];
                    pos[0] = total * -0.5m;
                    for (int j = 1; j < 17; j++)
                        pos[j] = j <= n ? pos[j - 1] + w[j - 1] : 0m;
                    WriteSectionPositions(s, pos);

                    s.setVehicle_toolWidth = (double)total;
                    s.setColor_isMultiColorSections = false;
                }
                else
                {
                    int n = ClampInt(cfg.NumSections, 1, 64);
                    s.setTool_numSectionsMulti = n;

                    double sw = cfg.SectionWidthMulti > 0
                        ? cfg.SectionWidthMulti
                        : (cfg.Width > 0 ? cfg.Width / n : 0.5);
                    sw = ClampD(sw, 0.01, 10.0);
                    s.setTool_sectionWidthMulti = sw;
                    s.setVehicle_toolWidth = n * sw;

                    int z = ClampInt(cfg.Zones, 1, 8);
                    if (z > n) z = n;
                    int[] zr = new int[9];
                    zr[0] = z;
                    int prev = 0;
                    for (int i = 1; i <= z; i++)
                    {
                        int r = (cfg.ZoneRanges != null && i - 1 < cfg.ZoneRanges.Length)
                            ? cfg.ZoneRanges[i - 1] : n;
                        r = ClampInt(r, prev + 1, n);
                        zr[i] = r;
                        prev = r;
                    }
                    zr[z] = n;
                    s.setTool_zones = string.Join(",", zr);
                }

                s.setVehicle_hitchLength = ClampD(cfg.HitchLength, -10.0, 10.0);
                s.setTool_toolTrailingHitchLength = ClampD(cfg.TrailingHitchLength, -20.0, 5.0);
                s.setTool_trailingToolToPivotLength = ClampD(cfg.TrailingToolToPivotLength, -20.0, 20.0);
                s.setVehicle_toolLookAheadOn = ClampD(cfg.LookAheadOn, 0.0, 20.0);
                s.setVehicle_toolLookAheadOff = ClampD(cfg.LookAheadOff, 0.0, 20.0);
                s.setVehicle_toolOffDelay = ClampD(cfg.TurnOffDelay, 0.0, 10.0);

                bool trailing = cfg.IsToolTrailing && !cfg.IsToolRearFixed && !cfg.IsToolFrontFixed;
                bool rear = !trailing && cfg.IsToolRearFixed && !cfg.IsToolFrontFixed;
                bool front = !trailing && !rear && cfg.IsToolFrontFixed;
                if (!trailing && !rear && !front) rear = true;

                s.setTool_isToolTrailing = trailing;
                s.setTool_isToolRearFixed = rear;
                s.setTool_isToolFront = front;
                s.setTool_isToolTBT = cfg.IsToolTBT && trailing;
                s.setTool_isSectionOffWhenOut = cfg.IsSectionOffWhenOut;
                s.Save();

                // Sin las funciones de layout de botones (LineUpIndividualSectionBtns/
                // LineUpAllZoneButtons, WinForms) ni FixSectionLooks: solo reload
                // de CTool + recalculo de posiciones/anchos (logica pura, ya
                // portada en bloque 9 como SectionCalculator).
                try
                {
                    _engine.Tool = new PilotXCore.CTool(_engine);
                    if (cfg.IsSectionsNotZones)
                    {
                        _engine.SectionCalculator.SectionSetPosition();
                        _engine.SectionCalculator.SectionCalcWidths();
                    }
                    else
                    {
                        _engine.SectionCalculator.SectionCalcMulti();
                    }
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        public string GetVehiculoCustom()
        {
            try { return PilotXCore.Properties.Settings.Default.setBrand_VehiculoCustom ?? ""; }
            catch { return ""; }
        }

        public bool SetVehiculoCustom(string archivo)
        {
            try
            {
                archivo = (archivo ?? "").Trim();
                if (archivo.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;

                PilotXCore.Properties.Settings.Default.setBrand_VehiculoCustom = archivo;
                PilotXCore.Properties.Settings.Default.Save();
                // Sin AplicarVehiculoCustom (carga el sprite en el render GL —
                // GuidanceEngineHost no tiene render, eso vive en PilotX.Desktop/
                // el render Android, bloque 6). Persistir el setting ya alcanza
                // para que el siguiente render lo lea.
                return true;
            }
            catch { return false; }
        }

        public ImuConfigDto GetImu()
        {
            var s = PilotXCore.Properties.Settings.Default;
            return new ImuConfigDto
            {
                HeadingSource = s.setGPS_headingFromWhichSource,
                FusionGpsPercent = ClampInt((int)Math.Round(s.setIMU_fusionWeight2 * 500.0), 0, 100),
                MinGpsStep10Cm = s.setF_minHeadingStepDistance == 1.0,
                DualHeadingOffset = s.setGPS_dualHeadingOffset,
                DualReverseDistance = s.setGPS_dualReverseDetectionDistance,
                IsRtkAlarm = s.setGPS_isRTK,
                IsRtkKillAutosteer = s.setGPS_isRTK_KillAutoSteer,
                FixJumpAlarmDistance = s.setGPS_jumpFixAlarmDistance,
                IsReverseOn = s.setIMU_isReverseOn,
                AutoSwitchDualFix = s.setAutoSwitchDualFixOn,
                AutoSwitchDualFixSpeed = s.setAutoSwitchDualFixSpeed,
                RollFilterPercent = ClampInt((int)Math.Round(s.setIMU_rollFilter * 100.0), 0, 100),
                InvertRoll = s.setIMU_invertRoll
            };
        }

        public bool SaveImu(ImuConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = PilotXCore.Properties.Settings.Default;

                string src = cfg.HeadingSource == "Dual" ? "Dual" : "Fix";
                s.setGPS_headingFromWhichSource = src;

                double fusion = ClampInt(cfg.FusionGpsPercent, 5, 60) * 0.002;
                s.setIMU_fusionWeight2 = fusion;

                s.setF_minHeadingStepDistance = cfg.MinGpsStep10Cm ? 1.0 : 0.5;
                s.setGPS_minimumStepLimit = cfg.MinGpsStep10Cm ? 0.1 : 0.05;

                s.setGPS_dualHeadingOffset = ClampD(cfg.DualHeadingOffset, -100.0, 100.0);
                s.setGPS_dualReverseDetectionDistance = ClampD(cfg.DualReverseDistance, 0.0, 10.0);
                s.setGPS_isRTK = cfg.IsRtkAlarm;
                s.setGPS_isRTK_KillAutoSteer = cfg.IsRtkKillAutosteer;
                s.setGPS_jumpFixAlarmDistance = ClampInt(cfg.FixJumpAlarmDistance, 0, 1000);
                s.setIMU_isReverseOn = cfg.IsReverseOn;
                s.setAutoSwitchDualFixOn = cfg.AutoSwitchDualFix;
                s.setAutoSwitchDualFixSpeed = ClampD(cfg.AutoSwitchDualFixSpeed, 1.0, 10.0);

                double rollFilter = ClampInt(cfg.RollFilterPercent, 0, 98) * 0.01;
                s.setIMU_rollFilter = rollFilter;
                s.setIMU_invertRoll = cfg.InvertRoll;
                s.Save();

                try
                {
                    _engine.headingFromSource = src;
                    _engine.Ahrs.fusionWeight = fusion;
                    _engine.minHeadingStepDist = s.setF_minHeadingStepDistance;
                    _engine.gpsMinimumStepDistance = s.setGPS_minimumStepLimit;
                    _engine.isFirstHeadingSet = false;
                    _engine.Pn.headingTrueDualOffset = s.setGPS_dualHeadingOffset;
                    _engine.dualReverseDetectionDistance = s.setGPS_dualReverseDetectionDistance;
                    _engine.Ahrs.isReverseOn = s.setIMU_isReverseOn;
                    _engine.Ahrs.autoSwitchDualFixOn = s.setAutoSwitchDualFixOn;
                    _engine.Ahrs.autoSwitchDualFixSpeed = s.setAutoSwitchDualFixSpeed;
                    _engine.Ahrs.rollFilter = rollFilter;
                    _engine.Ahrs.isRollInvert = s.setIMU_invertRoll;
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        public ImuLiveDto GetImuLive()
        {
            var a = _engine.Ahrs;
            bool hasRoll = a.imuRoll != 88888;
            return new ImuLiveDto
            {
                HasImuHeading = a.imuHeading != 99999,
                HasImuRoll = hasRoll,
                ImuRoll = hasRoll ? a.imuRoll : 0.0,
                RollZero = a.rollZero
            };
        }

        public bool ZeroRoll()
        {
            if (_engine.Ahrs.imuRoll == 88888) return false;
            try
            {
                _engine.Ahrs.imuRoll += _engine.Ahrs.rollZero;
                _engine.Ahrs.rollZero = _engine.Ahrs.imuRoll;
                PersistRollZero();
            }
            catch { }
            return true;
        }

        public bool AdjustRollZero(double delta)
        {
            if (_engine.Ahrs.imuRoll == 88888) return false;
            if (double.IsNaN(delta) || double.IsInfinity(delta)) return false;
            double d = ClampD(delta, -5.0, 5.0);
            try { _engine.Ahrs.rollZero += d; PersistRollZero(); } catch { }
            return true;
        }

        public bool RemoveRollZero()
        {
            try { _engine.Ahrs.rollZero = 0; PersistRollZero(); } catch { }
            return true;
        }

        public bool ResetImu()
        {
            try
            {
                _engine.Ahrs.imuHeading = 99999;
                _engine.Ahrs.imuRoll = 88888;
            }
            catch { }
            return true;
        }

        private void PersistRollZero()
        {
            PilotXCore.Properties.Settings.Default.setIMU_rollZero = _engine.Ahrs.rollZero;
            PilotXCore.Properties.Settings.Default.Save();
        }

        private static decimal[] ReadSectionPositions(PilotXCore.Properties.Settings s)
        {
            return new[]
            {
                s.setSection_position1,  s.setSection_position2,  s.setSection_position3,
                s.setSection_position4,  s.setSection_position5,  s.setSection_position6,
                s.setSection_position7,  s.setSection_position8,  s.setSection_position9,
                s.setSection_position10, s.setSection_position11, s.setSection_position12,
                s.setSection_position13, s.setSection_position14, s.setSection_position15,
                s.setSection_position16, s.setSection_position17
            };
        }

        private static void WriteSectionPositions(PilotXCore.Properties.Settings s, decimal[] p)
        {
            s.setSection_position1 = p[0];   s.setSection_position2 = p[1];
            s.setSection_position3 = p[2];   s.setSection_position4 = p[3];
            s.setSection_position5 = p[4];   s.setSection_position6 = p[5];
            s.setSection_position7 = p[6];   s.setSection_position8 = p[7];
            s.setSection_position9 = p[8];   s.setSection_position10 = p[9];
            s.setSection_position11 = p[10]; s.setSection_position12 = p[11];
            s.setSection_position13 = p[12]; s.setSection_position14 = p[13];
            s.setSection_position15 = p[14]; s.setSection_position16 = p[15];
            s.setSection_position17 = p[16];
        }

        private static double ClampD(double v, double lo, double hi)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
        private static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }

    internal sealed class GuidanceEngineQuantiXRuntimeService : IQuantiXRuntimeService
    {
        private readonly IAogStateProvider _state;

        public GuidanceEngineQuantiXRuntimeService(IAogStateProvider state)
        {
            _state = state;
        }

        public QuantiXRuntimeSnapshot GetSnapshot()
        {
            var snap = new QuantiXRuntimeSnapshot
            {
                Motores = new List<QuantiXMotorRuntime>(),
                CurrentSpeedKmh = 0,
                CurrentToolWidthM = 0
            };

            var aog = _state != null ? _state.GetSnapshot() : null;
            double vel = aog != null ? aog.AvgSpeed : 0;
            double anchoTotal = aog != null && aog.ToolWidth > 0 ? aog.ToolWidth : 0;
            snap.CurrentSpeedKmh = vel;
            snap.CurrentToolWidthM = anchoTotal;

            MotoresConfig mc;
            try { mc = MotoresConfig.Load(); }
            catch { mc = new MotoresConfig(); }
            if (mc == null || mc.Nodos == null) return snap;

            foreach (var nodo in mc.Nodos)
            {
                if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;
                if (nodo.Motores == null) continue;
                for (int mi = 0; mi < nodo.Motores.Length; mi++)
                {
                    var motor = nodo.Motores[mi];
                    if (motor == null) continue;

                    double dosis;
                    if (motor.ManualMode)
                    {
                        dosis = motor.ManualDosis;
                    }
                    else
                    {
                        dosis = motor.DosisFija;
                        if (dosis <= 0)
                        {
                            if (!string.IsNullOrEmpty(motor.CampoDosis) && _state != null)
                                dosis = _state.GetShapeFieldDose(motor.CampoDosis);
                            else if (aog != null && aog.ShapeIsInside)
                                dosis = aog.ShapeCurrentDose;
                        }
                    }

                    double meterCal = motor.MeterCal > 0 ? motor.MeterCal : 1;
                    double ppr = motor.DientesEngranaje > 0 ? motor.DientesEngranaje : 24;
                    double maxHz = motor.MaxHz > 0 ? motor.MaxHz : 0;

                    double targetPps = 0;
                    if (dosis > 0 && vel > 0.5 && anchoTotal > 0)
                    {
                        double velMs = vel / 3.6;
                        double gPorSeg = (dosis * 1000.0 * anchoTotal * velMs) / 10000.0;
                        targetPps = gPorSeg / meterCal;
                    }

                    snap.Motores.Add(new QuantiXMotorRuntime
                    {
                        NodoUid = nodo.Uid,
                        MotorIndex = mi,
                        Nombre = motor.Nombre,
                        Habilitado = (motor.Cortes != null && motor.Cortes.Count > 0)
                                     || motor.DosisFija > 0
                                     || !string.IsNullOrEmpty(motor.CampoDosis),
                        DosisObjetivo = dosis,
                        TargetPps = targetPps,
                        TargetRpm = ppr > 0 ? targetPps * 60.0 / ppr : 0,
                        MaxHz = maxHz,
                        MaxRpm = (maxHz > 0 && ppr > 0) ? maxHz * 60.0 / ppr : 0,
                        MaxOutputPerSec = maxHz * meterCal,
                        MaxDoseAtCurrentSpeed = (maxHz > 0 && anchoTotal > 0 && vel > 0.5)
                            ? (maxHz * meterCal * 36.0) / (anchoTotal * vel)
                            : -1,
                        MaxDoseCurve = BuildDoseCurve(maxHz, meterCal, anchoTotal)
                    });
                }
            }
            return snap;
        }

        private static List<QuantiXMaxDosePoint> BuildDoseCurve(double maxHz, double meterCal, double ancho)
        {
            var list = new List<QuantiXMaxDosePoint>();
            if (maxHz <= 0 || meterCal <= 0 || ancho <= 0) return list;
            double[] speeds = { 5, 7, 10, 12, 15 };
            foreach (var v in speeds)
                list.Add(new QuantiXMaxDosePoint { SpeedKmh = v, MaxDose = (maxHz * meterCal * 36.0) / (ancho * v) });
            return list;
        }
    }
}
