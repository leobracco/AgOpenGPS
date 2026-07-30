// ============================================================================
// FormGpsQuickAbService.cs — adapter IQuickAbService → PilotX.
// Envuelve FormGPS y corre la lógica del partial FormGPS.QuickAb en el hilo UI
// (Invoke). Mapea el snapshot plano al DTO de ab-rapido.html. Nunca tira:
// en error devuelve un DTO con Ok=false.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsQuickAbService : IQuickAbService
    {
        private readonly FormGPS _form;

        public FormGpsQuickAbService(FormGPS form) { _form = form; }

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

        private static QuickAbStateDto Map(QuickAbEditor.QuickAbSnapshot s)
        {
            if (s == null) return new QuickAbStateDto { Ok = false, Error = "no-state" };
            return new QuickAbStateDto
            {
                Ok = true,
                HasField = s.HasField,
                Mode = s.Mode,
                Phase = s.Phase,
                RefRight = s.RefRight,
                AMarked = s.AMarked,
                BMarked = s.BMarked,
                Recording = s.Recording,
                Points = s.Points,
                HeadingDeg = s.HeadingDeg,
                SuggestedName = s.SuggestedName,
                GuidanceStopped = s.GuidanceStopped,
                Error = s.Error
            };
        }

        private static QuickAbStateDto Fail() =>
            new QuickAbStateDto { Ok = false, Error = "ui-error" };

        public QuickAbStateDto GetState() => OnUi(() => Map(_form.QuickAb_Tick()), Fail());

        public QuickAbStateDto Start(string mode) => OnUi(() => Map(_form.QuickAb_Start(mode)), Fail());
        public QuickAbStateDto ToggleSide() => OnUi(() => Map(_form.QuickAb_ToggleSide()), Fail());
        public QuickAbStateDto MarkA() => OnUi(() => Map(_form.QuickAb_MarkA()), Fail());
        public QuickAbStateDto MarkB() => OnUi(() => Map(_form.QuickAb_MarkB()), Fail());
        public QuickAbStateDto PauseToggle() => OnUi(() => Map(_form.QuickAb_PauseToggle()), Fail());
        public QuickAbStateDto SetHeading(double deg) => OnUi(() => Map(_form.QuickAb_SetHeading(deg)), Fail());
        public QuickAbStateDto Commit() => OnUi(() => Map(_form.QuickAb_Commit()), Fail());
        public QuickAbStateDto Save(string name) => OnUi(() => Map(_form.QuickAb_Save(name)), Fail());
        public QuickAbStateDto Cancel() => OnUi(() => Map(_form.QuickAb_Cancel()), Fail());
    }
}
