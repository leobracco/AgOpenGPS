// ============================================================================
// FormGpsTramSimpleService.cs — adapter ITramSimpleService → PilotX.
// Envuelve FormGPS y corre la geometría del partial FormGPS.TramSimple en el
// hilo UI (Invoke). Mapea el snapshot plano al DTO de tramline.html. Nunca tira:
// en error devuelve un DTO con Ok=false.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsTramSimpleService : ITramSimpleService
    {
        private readonly FormGPS _form;

        public FormGpsTramSimpleService(FormGPS form) { _form = form; }

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

        private static TramSimpleStateDto Map(FormGPS.TramSimpleStateSnapshot s)
        {
            if (s == null) return new TramSimpleStateDto { Ok = false, Error = "no-state" };
            return new TramSimpleStateDto
            {
                Ok = true,
                HasTrack = s.HasTrack,
                HasBoundary = s.HasBoundary,
                IsCurve = s.IsCurve,
                Passes = s.Passes,
                AlphaPercent = s.AlphaPercent,
                Mode = s.Mode,
                ToolWidthDisplay = s.ToolWidthDisplay,
                TramWidthDisplay = s.TramWidthDisplay,
                TrackWidthDisplay = s.TrackWidthDisplay,
                Units = s.Units
            };
        }

        private static TramSimpleStateDto Fail() =>
            new TramSimpleStateDto { Ok = false, Error = "ui-error" };

        public TramSimpleStateDto Open() =>
            OnUi(() => Map(_form.TramSimple_Open()), Fail());

        public TramSimpleStateDto GetState() =>
            OnUi(() => Map(_form.TramSimple_Snapshot()), Fail());

        public TramSimpleStateDto SetPasses(int passes) =>
            OnUi(() => Map(_form.TramSimple_SetPasses(passes)), Fail());

        public TramSimpleStateDto SetAlpha(int percent) =>
            OnUi(() => Map(_form.TramSimple_SetAlpha(percent)), Fail());

        public TramSimpleStateDto SetMode(string mode) =>
            OnUi(() => Map(_form.TramSimple_SetMode(mode)), Fail());

        public TramSimpleStateDto SwapAB() =>
            OnUi(() => Map(_form.TramSimple_SwapAB()), Fail());

        public bool Commit(bool save) =>
            OnUi(() => { _form.TramSimple_Commit(save); return true; }, false);
    }
}
