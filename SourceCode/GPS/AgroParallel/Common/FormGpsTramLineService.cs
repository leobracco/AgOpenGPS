// ============================================================================
// FormGpsTramLineService.cs — adapter ITramLineService → PilotX.
// Envuelve FormGPS y corre la geometría del partial FormGPS.TramLine en el
// hilo UI (Invoke). Devuelve DTOs E/N para tramlines.html.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsTramLineService : ITramLineService
    {
        private readonly FormGPS _form;

        public FormGpsTramLineService(FormGPS form) { _form = form; }

        private T OnUi<T>(Func<T> body, T fallback)
        {
            if (_form == null || _form.IsDisposed) return fallback;
            try
            {
                if (_form.InvokeRequired)
                    return (T)_form.Invoke(new Func<T>(body));
                return body();
            }
            catch { return fallback; }
        }

        private void OnUiVoid(Action body)
        {
            if (_form == null || _form.IsDisposed) return;
            try
            {
                if (_form.InvokeRequired)
                    _form.Invoke(body);
                else
                    body();
            }
            catch { }
        }

        private static TramLineStateDto Fail(string err = "ui-error")
            => new TramLineStateDto { Ok = false, Error = err };

        private TramLineStateDto Map(FormGPS.TramSnapshot snap)
        {
            var dto = new TramLineStateDto
            {
                Ok = true,
                HasBoundary = snap.HasBoundary,
                Units = snap.Units,
                TrackWidthDisplay = Math.Round(snap.TrackWidthDisplay, 2),
                TramWidthDisplay = Math.Round(snap.TramWidthDisplay, 2),
                ToolWidthDisplay = Math.Round(snap.ToolWidthDisplay, 2),
                SelIdx = snap.SelIdx,
                Passes = snap.Passes,
                StartPass = snap.StartPass,
                IsOuter = snap.IsOuter,
                Alpha = snap.Alpha,
                CutStep = snap.CutStep,
                Error = snap.Error
            };

            // Tracks
            foreach (var t in snap.Tracks)
            {
                var pts = new List<double[]>();
                if (t.Mode == "ab")
                {
                    pts.Add(new double[] { t.PtA.easting, t.PtA.northing });
                    pts.Add(new double[] { t.PtB.easting, t.PtB.northing });
                }
                else if (t.CurvePts != null)
                {
                    for (int i = 0; i < t.CurvePts.Count; i++)
                        pts.Add(new double[] { t.CurvePts[i].easting, t.CurvePts[i].northing });
                }
                dto.Tracks.Add(new TramLineTrackDto
                {
                    Index = t.Index,
                    Name = t.Name,
                    Mode = t.Mode,
                    Points = pts.ToArray()
                });
            }

            // New trams
            dto.NewTrams = MapTramList(snap.NewTrams);
            // Saved trams
            dto.SavedTrams = MapTramList(snap.SavedTrams);
            // Fences
            foreach (var f in snap.Fences)
            {
                var pts = new double[f.Count][];
                for (int i = 0; i < f.Count; i++)
                    pts[i] = new double[] { f[i].easting, f[i].northing };
                dto.Fences.Add(pts);
            }
            // Outer/inner bnd
            dto.OuterBnd = MapVec2List(snap.OuterBnd);
            dto.InnerBnd = MapVec2List(snap.InnerBnd);

            // Cut points
            if (snap.PtA.easting < 9000000)
                dto.PtA = new double[] { snap.PtA.easting, snap.PtA.northing };
            if (snap.PtB.easting < 9000000)
                dto.PtB = new double[] { snap.PtB.easting, snap.PtB.northing };

            return dto;
        }

        private static List<double[][]> MapTramList(List<List<vec2>> src)
        {
            var list = new List<double[][]>();
            if (src == null) return list;
            foreach (var line in src)
            {
                var pts = new double[line.Count][];
                for (int i = 0; i < line.Count; i++)
                    pts[i] = new double[] { line[i].easting, line[i].northing };
                list.Add(pts);
            }
            return list;
        }

        private static double[][] MapVec2List(List<vec2> src)
        {
            if (src == null || src.Count == 0) return new double[0][];
            var pts = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                pts[i] = new double[] { src[i].easting, src[i].northing };
            return pts;
        }

        public TramLineStateDto GetState() =>
            OnUi(() => Map(_form.Tram_Snapshot()), Fail());

        public TramLineStateDto Open() =>
            OnUi(() => Map(_form.Tram_Snapshot(_form.Tram_Open())), Fail());

        public TramLineStateDto CycleTrack(int dir) =>
            OnUi(() => { _form.Tram_CycleTrack(dir); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SwapSide() =>
            OnUi(() => { _form.Tram_SwapSide(); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SetPasses(int passes) =>
            OnUi(() => { _form.Tram_SetPasses(passes); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SetStartPass(int startPass) =>
            OnUi(() => { _form.Tram_SetStartPass(startPass); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SetOuter(bool on) =>
            OnUi(() => { _form.Tram_SetOuter(on); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SetAlpha(double alpha) =>
            OnUi(() => { _form.Tram_SetAlpha(alpha); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto AddLines() =>
            OnUi(() => { _form.Tram_AddLines(); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto DeleteAll() =>
            OnUi(() => { _form.Tram_DeleteAll(); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto Tap(double easting, double northing) =>
            OnUi(() => { _form.Tram_Tap(easting, northing); return Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto CancelTouch() =>
            OnUi(() => { _form.Tram_CancelTouch(); return Map(_form.Tram_Snapshot()); }, Fail());

        public void CloseSession() =>
            OnUiVoid(() => _form.Tram_CloseSession());

        public void CancelSession() =>
            OnUiVoid(() => _form.Tram_CancelSession());
    }
}
