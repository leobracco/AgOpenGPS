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
            }
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

        public void CloseUse() => OnUiVoid(() => _form.TrkBuilder_CloseUse());

        public void CloseCancel() => OnUiVoid(() => _form.TrkBuilder_CloseCancel());
    }
}
