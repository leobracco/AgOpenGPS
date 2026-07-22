// ============================================================================
// EngineStateProvider.cs — adaptador IAogStateProvider sobre GuidanceEngineHost.
// Gemelo headless de FormGpsStateProvider (GPS/AgroParallel/Common).
//
// GetSnapshot() porta 1:1 desde FormGPS con renombrado de campos (lowercase ->
// PascalCase del host). Lo único que NO existe headless se stubbea:
//   · PowerStatus (WinForms SystemInformation)      -> PowerOnline = false
//   · flagColor / isNudgeOn (estado de UI nativa)    -> 0 / false
//   · Properties.Settings (proyecto GPS net48)       -> VehicleType/Brand fijos, HasHydLift=false
//   · ShapefileLayerForAdapters (capa del mapa AOG)  -> sin dosis de shape
//
// Los ~14 métodos satélite (graphs, sim-coords, display-colors, event-log,
// section-colors, shape) espejaban ventanas WinForms de PilotX que este
// proceso no tiene y que el render del mapa (PilotX.Desktop) no consume:
// devuelven snapshots vacíos/seguros. Si algún día el Hub corre contra el
// engine headless, se completan con la fuente real (Settings portados a Core,
// buffers de gráfico propios, etc.).
// ============================================================================

using System.Collections.Generic;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineStateProvider : IAogStateProvider
    {
        private readonly GuidanceEngineHost _host;

        public EngineStateProvider(GuidanceEngineHost host) { _host = host; }

        public AogStateSnapshot GetSnapshot()
        {
            var snap = new AogStateSnapshot();
            if (_host == null) return snap;

            try
            {
                snap.IsJobStarted = _host.IsJobStarted;
                snap.CurrentFieldDirectory = _host.currentFieldDirectory;
                snap.FieldsDirectory = RegistrySettings.fieldsDirectory;
                snap.AvgSpeed = _host.avgSpeed;
                snap.FixQuality = _host.Pn != null ? _host.Pn.fixQuality : 0;
                snap.PowerOnline = false; // sin WinForms SystemInformation headless.
                snap.Heading = _host.pivotAxlePos.heading;
                snap.PivotEasting = _host.pivotAxlePos.easting;
                snap.PivotNorthing = _host.pivotAxlePos.northing;

                if (_host.AppModelField != null)
                {
                    snap.Latitude = _host.AppModelField.CurrentLatLon.Latitude;
                    snap.Longitude = _host.AppModelField.CurrentLatLon.Longitude;
                }

                snap.IsAutoSteerOn = _host.isBtnAutoSteerOn;
                snap.IsSectionAutoOn = _host.autoBtnState == btnStates.Auto;
                snap.IsSectionManualOn = _host.manualBtnState == btnStates.On;
                if (_host.Trk != null)
                {
                    snap.IsAutoSnapToPivot = _host.Trk.isAutoSnapToPivot;
                    snap.IsAutoTrackOn = _host.Trk.isAutoTrack;
                    snap.TrackIdx = _host.Trk.idx;
                    if (_host.Trk.gArr != null)
                    {
                        snap.TracksTotal = _host.Trk.gArr.Count;
                        int vis = 0;
                        for (int i = 0; i < _host.Trk.gArr.Count; i++)
                            if (_host.Trk.gArr[i].isVisible) vis++;
                        snap.TracksVisible = vis;
                    }
                }
                if (_host.Yt != null) snap.IsYouTurnOn = _host.Yt.isYouTurnBtnOn;
                if (_host.Ct != null)
                {
                    snap.IsContourOn = _host.Ct.isContourBtnOn;
                    snap.IsContourLocked = _host.Ct.isLocked;
                }
                if (_host.Isobus != null)
                {
                    snap.IsobusAlive = _host.Isobus.IsAlive();
                    snap.IsobusOn = _host.Isobus.SectionControlEnabled;
                }
                snap.HasBoundary = _host.Bnd != null && _host.Bnd.bndList != null
                    && _host.Bnd.bndList.Count > 0;

                // Barra abajo: flag/nudge son estado de UI nativa — no headless.
                snap.FlagColor = 0;
                snap.IsNudgeOn = false;
                if (_host.Bnd != null)
                {
                    snap.HasHeadland = _host.Bnd.bndList != null && _host.Bnd.bndList.Count > 0
                        && _host.Bnd.bndList[0].hdLine != null && _host.Bnd.bndList[0].hdLine.Count > 0;
                    snap.IsHeadlandOn = _host.Bnd.isHeadlandOn;
                    snap.IsSectionControlledByHeadland = _host.Bnd.isSectionControlledByHeadland;
                }
                snap.HasHydLift = false; // Properties.Settings vive en el proyecto GPS net48.
                if (_host.Vehicle != null) snap.IsHydLiftOn = _host.Vehicle.isHydLiftOn;
                if (_host.Tram != null)
                {
                    snap.HasTram = (_host.Tram.tramList != null ? _host.Tram.tramList.Count : 0)
                        + (_host.Tram.tramBndOuterArr != null ? _host.Tram.tramBndOuterArr.Count : 0) > 0;
                    snap.TramDisplayMode = (int)_host.Tram.displayMode;
                }
                if (_host.Yt != null)
                {
                    snap.YouSkipMode = (int)_host.Yt.skipMode;
                    snap.RowSkipsWidth = _host.Yt.rowSkipsWidth;
                }

                snap.ToolEasting = _host.toolPos.easting;
                snap.ToolNorthing = _host.toolPos.northing;
                snap.ToolHeading = _host.toolPos.heading;

                if (_host.Fd != null)
                {
                    snap.WorkedAreaTotalM2 = _host.Fd.workedAreaTotal;
                    snap.ActualAreaCoveredM2 = _host.Fd.actualAreaCovered;
                }

                if (_host.Tool != null)
                {
                    int n = _host.Tool.numOfSections;
                    snap.NumSections = n;
                    snap.ToolWidth = _host.Tool.width;
                    snap.ToolOffset = _host.Tool.offset;
                    if (n > 0 && _host.Sections != null)
                    {
                        var arr = new bool[n];
                        var pos = new List<SectionExtent>(n);
                        var speeds = new double[n];
                        for (int i = 0; i < n && i < _host.Sections.Length; i++)
                        {
                            var sec = _host.Sections[i];
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
                    snap.ToolFarLeftSpeedKmh = _host.Tool.farLeftSpeed * 3.6;
                    snap.ToolFarRightSpeedKmh = _host.Tool.farRightSpeed * 3.6;
                }

                // Geometría del lote: boundary + headland + track activo.
                try
                {
                    if (_host.Bnd != null && _host.Bnd.bndList != null)
                    {
                        var bnds = new List<List<FieldPoint>>();
                        var hdls = new List<List<FieldPoint>>();
                        foreach (var b in _host.Bnd.bndList)
                        {
                            if (b == null) continue;
                            bnds.Add(DecimateVec3(b.fenceLine, 0.5));
                            hdls.Add(DecimateVec3(b.hdLine, 0.5));
                        }
                        snap.Boundaries = bnds;
                        snap.Headlands = hdls;

                        if (_host.Bnd.bndList.Count > 0)
                        {
                            double areaM2 = _host.Bnd.bndList[0].area;
                            for (int i = 1; i < _host.Bnd.bndList.Count; i++)
                                areaM2 -= _host.Bnd.bndList[i].area;
                            snap.BoundaryAreaM2 = areaM2;
                        }
                    }

                    if (_host.Trk != null && _host.Trk.gArr != null && _host.Trk.idx >= 0 && _host.Trk.idx < _host.Trk.gArr.Count)
                    {
                        var t = _host.Trk.gArr[_host.Trk.idx];
                        if (t != null)
                        {
                            var ti = new TrackInfo
                            {
                                Name = t.name,
                                Mode = t.mode.ToString(),
                                Heading = t.heading,
                                A = new FieldPoint(t.ptA.easting, t.ptA.northing),
                                B = new FieldPoint(t.ptB.easting, t.ptB.northing),
                                CurvePts = DecimateVec3(t.curvePts, 1.0)
                            };
                            snap.ActiveTrack = ti;
                        }
                    }
                }
                catch { /* no romper snapshot por geometría */ }

                // Sin Properties.Settings headless: sprite default.
                snap.VehicleType = "Tractor";
                snap.VehicleBrand = "AGOpenGPS";
            }
            catch
            {
                // Defensivo: jamás romper a un consumidor por estado parcial.
            }

            return snap;
        }

        // Reduce densidad de polylines: descarta puntos a < minStepM metros del anterior.
        private static List<FieldPoint> DecimateVec3(List<vec3> src, double minStepM)
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

        // ---- Métodos satélite: sin fuente headless (ventanas WinForms de PilotX
        //      que este proceso no tiene). Snapshots vacíos/seguros. ----

        public AllSettingsSnapshot GetAllSettings() => new AllSettingsSnapshot();

        public EventLogSnapshot GetEventLog()
            => new EventLogSnapshot { File = "", History = "", Session = "" };

        public XteGraphSample GetXteGraphSample() => new XteGraphSample();
        public HeadingGraphSample GetHeadingGraphSample() => new HeadingGraphSample();
        public SteerGraphSample GetSteerGraphSample() => new SteerGraphSample();
        public CorrectionGraphSample GetCorrectionGraphSample() => new CorrectionGraphSample();
        public ShiftPosSnapshot GetShiftPos() => new ShiftPosSnapshot();
        public SimCoordsSnapshot GetSimCoords() => new SimCoordsSnapshot();

        public SectionColorsSnapshot GetSectionColors()
        {
            var s = new SectionColorsSnapshot();
            var colors = new string[16];
            for (int i = 0; i < 16; i++) colors[i] = "#000000";
            s.Colors = colors;
            return s;
        }

        public DisplayColorsSnapshot GetDisplayColors()
        {
            return new DisplayColorsSnapshot
            {
                FrameDay = "#000000",
                FrameNight = "#000000",
                FieldDay = "#000000",
                FieldNight = "#000000",
                TextDay = "#000000",
                TextNight = "#000000",
            };
        }

        public double GetShapeFieldDose(string fieldName) => 0;
        public ShapeSnapshot GetShape() => null;
        public ShapeFieldsSnapshot GetShapeFields() => new ShapeFieldsSnapshot();
    }
}
