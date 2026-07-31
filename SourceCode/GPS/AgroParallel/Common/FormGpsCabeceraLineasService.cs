// ============================================================================
// FormGpsCabeceraLineasService.cs — adapter ICabeceraLineasService → PilotX.
// Envuelve FormGPS y corre la lógica del partial FormGPS.CabeceraLineas en el
// hilo UI (Invoke). Mapea el snapshot plano al DTO de cabecera-lineas.html.
// Nunca tira: en error devuelve un DTO con Ok=false.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsCabeceraLineasService : ICabeceraLineasService
    {
        private readonly FormGPS _form;

        public FormGpsCabeceraLineasService(FormGPS form) { _form = form; }

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
                if (_form.InvokeRequired) _form.Invoke(body);
                else body();
            }
            catch { /* best-effort */ }
        }

        private static CabeceraLineasStateDto Map(CabeceraLineasEditor.CabLinSnapshot s)
        {
            if (s == null) return new CabeceraLineasStateDto { Ok = false, Error = "no-state" };
            var dto = new CabeceraLineasStateDto
            {
                Ok = true,
                JobStarted = s.JobStarted,
                HasBoundary = s.HasBoundary,
                Units = s.Units,
                ToolWidthDisplay = s.ToolWidthDisplay,
                BndSelect = s.BndSelect,
                SelIdx = s.SelIdx,
                APoint = s.APoint,
                BPoint = s.BPoint,
                IsSectionControlled = s.IsSectionControlled,
                Error = s.Error
            };
            foreach (var f in s.Fences) dto.Fences.Add(f.ToArray());
            dto.HdLine = s.HdLine.ToArray();
            for (int i = 0; i < s.Tracks.Count; i++)
            {
                dto.Tracks.Add(new CabeceraLineasTrackDto
                {
                    Index = i,
                    Name = s.Tracks[i].Name,
                    Mode = s.Tracks[i].Mode,
                    Points = s.Tracks[i].Points.ToArray()
                });
            }
            return dto;
        }

        private static CabeceraLineasStateDto Fail() =>
            new CabeceraLineasStateDto { Ok = false, Error = "ui-error" };

        public CabeceraLineasStateDto GetState() => OnUi(() => Map(_form.CabLin_Snapshot()), Fail());
        public CabeceraLineasStateDto Open() => OnUi(() => Map(_form.CabLin_Open()), Fail());
        public CabeceraLineasStateDto Tap(double easting, double northing, string mode, double distanceDisplay) =>
            OnUi(() => Map(_form.CabLin_Tap(easting, northing, mode, distanceDisplay)), Fail());
        public CabeceraLineasStateDto CancelTouch() => OnUi(() => Map(_form.CabLin_CancelTouch()), Fail());
        public CabeceraLineasStateDto Cycle(int dir) => OnUi(() => Map(_form.CabLin_Cycle(dir)), Fail());
        public CabeceraLineasStateDto DeleteTrack() => OnUi(() => Map(_form.CabLin_DeleteTrack()), Fail());
        public CabeceraLineasStateDto Extend(string end, bool grow) => OnUi(() => Map(_form.CabLin_Extend(end, grow)), Fail());
        public CabeceraLineasStateDto BuildHeadland() => OnUi(() => Map(_form.CabLin_BuildHeadland()), Fail());
        public CabeceraLineasStateDto ResetHeadland() => OnUi(() => Map(_form.CabLin_ResetHeadland()), Fail());
        public CabeceraLineasStateDto TurnOff() => OnUi(() => Map(_form.CabLin_TurnOff()), Fail());

        public bool SetSectionControlled(bool on)
        {
            return OnUi(() => { _form.CabLin_SetSectionControlled(on); return on; }, !on);
        }

        public void CloseSession() => OnUiVoid(() => _form.CabLin_CloseSession());
    }
}
