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




        public TramLineStateDto GetState() =>
            OnUi(() => TramLineMapper.Map(_form.Tram_Snapshot()), Fail());

        public TramLineStateDto Open() =>
            OnUi(() => TramLineMapper.Map(_form.Tram_Snapshot(_form.Tram_Open())), Fail());

        public TramLineStateDto CycleTrack(int dir) =>
            OnUi(() => { _form.Tram_CycleTrack(dir); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SwapSide() =>
            OnUi(() => { _form.Tram_SwapSide(); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SetPasses(int passes) =>
            OnUi(() => { _form.Tram_SetPasses(passes); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SetStartPass(int startPass) =>
            OnUi(() => { _form.Tram_SetStartPass(startPass); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SetOuter(bool on) =>
            OnUi(() => { _form.Tram_SetOuter(on); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto SetAlpha(double alpha) =>
            OnUi(() => { _form.Tram_SetAlpha(alpha); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto AddLines() =>
            OnUi(() => { _form.Tram_AddLines(); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto DeleteAll() =>
            OnUi(() => { _form.Tram_DeleteAll(); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto Tap(double easting, double northing) =>
            OnUi(() => { _form.Tram_Tap(easting, northing); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public TramLineStateDto CancelTouch() =>
            OnUi(() => { _form.Tram_CancelTouch(); return TramLineMapper.Map(_form.Tram_Snapshot()); }, Fail());

        public void CloseSession() =>
            OnUiVoid(() => _form.Tram_CloseSession());

        public void CancelSession() =>
            OnUiVoid(() => _form.Tram_CancelSession());
    }
}
