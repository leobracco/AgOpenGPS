// ============================================================================
// FormGpsTrackBuilderService.cs — adapter ITrackBuilderService → PilotX.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsTrackBuilderService : ITrackBuilderService
    {
        private readonly FormGPS _form;

        public FormGpsTrackBuilderService(FormGPS form) { _form = form; }

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

        private static TrackBuilderStateDto Fail(string err = "ui-error")
            => new TrackBuilderStateDto { Ok = false, Error = err };

        private TrackBuilderStateDto Map(FormGPS.TrkBuilderSnapshot snap)
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

            // Fences
            dto.Fences = _form.TrkBuilder_FencesEN();
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

        public TrackBuilderStateDto Open() =>
            OnUi(() => { _form.TrkBuilder_Open(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto GetState() =>
            OnUi(() => Map(_form.TrkBuilder_Snapshot()), Fail());

        public TrackBuilderStateDto ToggleVisibility(int index) =>
            OnUi(() => { _form.TrkBuilder_ToggleVisibility(index); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto ToggleAll(bool visible) =>
            OnUi(() => { _form.TrkBuilder_ToggleAll(visible); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto Select(int index) =>
            OnUi(() => { _form.TrkBuilder_Select(index); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto Delete() =>
            OnUi(() => { _form.TrkBuilder_Delete(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto Duplicate(string newName) =>
            OnUi(() => { _form.TrkBuilder_Duplicate(newName); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto Rename(string newName) =>
            OnUi(() => { _form.TrkBuilder_Rename(newName); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto MoveUp() =>
            OnUi(() => { _form.TrkBuilder_MoveUp(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto MoveDown() =>
            OnUi(() => { _form.TrkBuilder_MoveDown(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto SwapAB() =>
            OnUi(() => { _form.TrkBuilder_SwapAB(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto CreateABFromPivot(double headingDeg, string name) =>
            OnUi(() => { _form.TrkBuilder_CreateABFromPivot(headingDeg, name); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto Tap(double easting, double northing) =>
            OnUi(() => { _form.TrkBuilder_Tap(easting, northing); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto CancelTouch() =>
            OnUi(() => { _form.TrkBuilder_CancelTouch(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto MakeCurve() =>
            OnUi(() => Map(_form.TrkBuilder_Snapshot(_form.TrkBuilder_MakeCurve())), Fail());

        public TrackBuilderStateDto MakeABLine() =>
            OnUi(() => Map(_form.TrkBuilder_Snapshot(_form.TrkBuilder_MakeABLine())), Fail());

        public TrackBuilderStateDto MakeBoundaryCurve() =>
            OnUi(() => { _form.TrkBuilder_MakeBoundaryCurve(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto ExtendA() =>
            OnUi(() => { _form.TrkBuilder_ExtendA(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public TrackBuilderStateDto ExtendB() =>
            OnUi(() => { _form.TrkBuilder_ExtendB(); return Map(_form.TrkBuilder_Snapshot()); }, Fail());

        public void CloseUse() => OnUiVoid(() => _form.TrkBuilder_CloseUse());

        public void CloseCancel() => OnUiVoid(() => _form.TrkBuilder_CloseCancel());
    }
}
