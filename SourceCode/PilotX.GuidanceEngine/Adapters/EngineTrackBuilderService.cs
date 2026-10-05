// ============================================================================
// EngineTrackBuilderService.cs — adaptador ITrackBuilderService sobre
// GuidanceEngineHost. Gemelo headless de FormGpsTrackBuilderService
// (GPS/AgroParallel/Common): mismo mapeo TrkBuilderSnapshot -> DTO, pero
// contra los TrkBuilder_* de GuidanceEngineHost.TrackBuilder.cs (bloque 14,
// ítem 2 del PEDIDO taller) en vez de FormGPS. Sin hilo de UI que marshalar
// (OnUi/InvokeRequired de la versión FormGPS no aplican acá) — solo try/catch
// defensivo, mismo criterio que el resto de los adapters de este proyecto.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineTrackBuilderService : ITrackBuilderService
    {
        private readonly GuidanceEngineHost _host;

        public EngineTrackBuilderService(GuidanceEngineHost host) { _host = host; }

        private static TrackBuilderStateDto Fail(string err = "engine-error")
            => new TrackBuilderStateDto { Ok = false, Error = err };

        private TrackBuilderStateDto Map(GuidanceEngineHost.TrkBuilderSnapshot snap)
        {
            var dto = new TrackBuilderStateDto
            {
                Ok = true,
                SelectedIdx = snap.SelectedIdx,
                ActiveIdx = snap.ActiveIdx,
                Error = snap.Error
            };

            for (int i = 0; i < snap.Tracks.Count; i++)
            {
                var t = snap.Tracks[i];
                dto.Tracks.Add(new TrackItemDto
                {
                    Index = i,
                    Name = string.IsNullOrEmpty(t.name) ? ("Guía " + (i + 1)) : t.name,
                    Mode = ModeStr(t.mode),
                    IsVisible = t.isVisible,
                    IsActive = i == snap.ActiveIdx
                });

                // Geometría para el canvas
                var geom = new TrackGeomDto { Index = i, Mode = ModeStr(t.mode) };
                if (t.mode == TrackMode.AB)
                {
                    // Extender la recta para dibujo (como ABDraw)
                    double len = snap.ABLength;
                    double h = t.heading;
                    geom.Points = new double[][] {
                        new double[] { t.ptA.easting - Math.Sin(h) * len, t.ptA.northing - Math.Cos(h) * len },
                        new double[] { t.ptB.easting + Math.Sin(h) * len, t.ptB.northing + Math.Cos(h) * len }
                    };
                }
                else if (t.curvePts != null && t.curvePts.Count > 0)
                {
                    var pts = new double[t.curvePts.Count][];
                    for (int j = 0; j < t.curvePts.Count; j++)
                        pts[j] = new double[] { t.curvePts[j].easting, t.curvePts[j].northing };
                    geom.Points = pts;
                }
                else if (t.mode == TrackMode.waterPivot)
                {
                    geom.Points = new double[][] { new double[] { t.ptA.easting, t.ptA.northing } };
                }
                dto.TrackGeoms.Add(geom);
            }

            dto.Fences = _host.TrkBuilder_FencesEN();
            dto.BndSelect = snap.BndSelect;
            dto.APoint = snap.APoint;
            dto.BPoint = snap.BPoint;
            dto.CanMakeLine = snap.CanMakeLine;
            dto.HasBoundaryCurve = snap.HasBoundaryCurve;

            return dto;
        }

        private static string ModeStr(TrackMode m)
        {
            switch (m)
            {
                case TrackMode.AB: return "ab";
                case TrackMode.Curve: return "curve";
                case TrackMode.bndCurve: return "bnd_curve";
                case TrackMode.waterPivot: return "water_pivot";
                default: return "unknown";
            }
        }

        private TrackBuilderStateDto Run(Action body)
        {
            if (_host == null) return Fail("no-host");
            try { body(); return Map(_host.TrkBuilder_Snapshot()); }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        public TrackBuilderStateDto Open() => Run(() => _host.TrkBuilder_Open());
        public TrackBuilderStateDto GetState()
        {
            if (_host == null) return Fail("no-host");
            try { return Map(_host.TrkBuilder_Snapshot()); }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        public TrackBuilderStateDto ToggleVisibility(int index) => Run(() => _host.TrkBuilder_ToggleVisibility(index));
        public TrackBuilderStateDto ToggleAll(bool visible) => Run(() => _host.TrkBuilder_ToggleAll(visible));
        public TrackBuilderStateDto Select(int index) => Run(() => _host.TrkBuilder_Select(index));
        public TrackBuilderStateDto Delete() => Run(() => _host.TrkBuilder_Delete());
        public TrackBuilderStateDto Duplicate(string newName) => Run(() => _host.TrkBuilder_Duplicate(newName));
        public TrackBuilderStateDto Rename(string newName) => Run(() => _host.TrkBuilder_Rename(newName));
        public TrackBuilderStateDto MoveUp() => Run(() => _host.TrkBuilder_MoveUp());
        public TrackBuilderStateDto MoveDown() => Run(() => _host.TrkBuilder_MoveDown());
        public TrackBuilderStateDto SwapAB() => Run(() => _host.TrkBuilder_SwapAB());
        public TrackBuilderStateDto CreateABFromPivot(double headingDeg, string name)
            => Run(() => _host.TrkBuilder_CreateABFromPivot(headingDeg, name));

        public TrackBuilderStateDto Tap(double easting, double northing) => Run(() => _host.TrkBuilder_Tap(easting, northing));
        public TrackBuilderStateDto CancelTouch() => Run(() => _host.TrkBuilder_CancelTouch());

        public TrackBuilderStateDto MakeCurve()
        {
            if (_host == null) return Fail("no-host");
            try { return Map(_host.TrkBuilder_Snapshot(_host.TrkBuilder_MakeCurve())); }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        public TrackBuilderStateDto MakeABLine()
        {
            if (_host == null) return Fail("no-host");
            try { return Map(_host.TrkBuilder_Snapshot(_host.TrkBuilder_MakeABLine())); }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        public TrackBuilderStateDto MakeBoundaryCurve() => Run(() => _host.TrkBuilder_MakeBoundaryCurve());
        public TrackBuilderStateDto ExtendA() => Run(() => _host.TrkBuilder_ExtendA());
        public TrackBuilderStateDto ExtendB() => Run(() => _host.TrkBuilder_ExtendB());

        public TrackBuilderStateDto RecordCurveA() => Run(() => _host.TrkBuilder_RecordCurveA());
        public TrackBuilderStateDto RecordCurvePause() => Run(() => _host.TrkBuilder_RecordCurvePause());

        public TrackBuilderStateDto RecordCurveB(string name)
        {
            if (_host == null) return Fail("no-host");
            try { return Map(_host.TrkBuilder_Snapshot(_host.TrkBuilder_RecordCurveB(name))); }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        public TrackBuilderStateDto RecordCurveCancel() => Run(() => _host.TrkBuilder_RecordCurveCancel());

        public bool IsRecordingCurve() => _host != null && _host.TrkBuilder_IsRecording();
        public int RecordedPointCount() => _host != null ? _host.TrkBuilder_RecordedCount() : 0;

        public void CloseUse() { try { _host?.TrkBuilder_CloseUse(); } catch { } }
        public void CloseCancel() { try { _host?.TrkBuilder_CloseCancel(); } catch { } }
    }
}
