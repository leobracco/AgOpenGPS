// ============================================================================
// FormGpsRecPathService.cs — adapter IRecPathService → PilotX.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsRecPathService : IRecPathService
    {
        private readonly FormGPS _form;

        public FormGpsRecPathService(FormGPS form) { _form = form; }

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
            catch { }
        }

        public List<string> ListPaths() =>
            OnUi(() => _form.RecPath_ListFiles(), new List<string>());

        public bool LoadPath(string name) =>
            OnUi(() => _form.RecPath_Load(name), false);

        public bool DeletePath(string name) =>
            OnUi(() => _form.RecPath_Delete(name), false);

        public void TurnOff() => OnUiVoid(() => _form.RecPath_TurnOff());

        public bool SaveWithName(string name) =>
            OnUi(() => _form.RecPath_SaveWithName(name), false);

        public void DiscardRecording() => OnUiVoid(() => _form.RecPath_Discard());
    }
}
