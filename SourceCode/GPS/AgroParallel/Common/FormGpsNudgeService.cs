// ============================================================================
// FormGpsNudgeService.cs — adapter INudgeService → PilotX.
// Envuelve FormGPS y corre la lógica del partial FormGPS.Nudge en el hilo UI
// (Invoke). Mapea el snapshot plano al DTO de mover-guia.html. Nunca tira:
// en error devuelve un DTO con Ok=false.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsNudgeService : INudgeService
    {
        private readonly FormGPS _form;

        public FormGpsNudgeService(FormGPS form) { _form = form; }

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

        private static NudgeStateDto Map(FormGPS.NudgeStateSnapshot s)
        {
            if (s == null) return new NudgeStateDto { Ok = false, Error = "no-state" };
            return new NudgeStateDto
            {
                Ok = true,
                HasTrack = s.HasTrack,
                OffsetDisplay = s.OffsetDisplay,
                StepDisplay = s.StepDisplay,
                StepRefDisplay = s.StepRefDisplay,
                RefActive = s.RefActive,
                RefMovedDisplay = s.RefMovedDisplay,
                Units = s.Units
            };
        }

        private static NudgeStateDto Fail() =>
            new NudgeStateDto { Ok = false, Error = "ui-error" };

        public NudgeStateDto GetState() => OnUi(() => Map(_form.Nudge_Snapshot()), Fail());

        public NudgeStateDto Move(int dir) => OnUi(() => Map(_form.Nudge_Move(dir)), Fail());
        public NudgeStateDto Half(int dir) => OnUi(() => Map(_form.Nudge_Half(dir)), Fail());
        public NudgeStateDto Zero() => OnUi(() => Map(_form.Nudge_Zero()), Fail());
        public NudgeStateDto ToPivot() => OnUi(() => Map(_form.Nudge_ToPivot()), Fail());
        public NudgeStateDto SetStep(double v) => OnUi(() => Map(_form.Nudge_SetStep(v)), Fail());
        public NudgeStateDto Close() => OnUi(() => Map(_form.Nudge_Close()), Fail());

        public NudgeStateDto RefOpen() => OnUi(() => Map(_form.NudgeRef_Open()), Fail());
        public NudgeStateDto RefMove(int dir) => OnUi(() => Map(_form.NudgeRef_Move(dir)), Fail());
        public NudgeStateDto RefHalf(int dir) => OnUi(() => Map(_form.NudgeRef_Half(dir)), Fail());
        public NudgeStateDto RefSetStep(double v) => OnUi(() => Map(_form.NudgeRef_SetStep(v)), Fail());
        public NudgeStateDto RefSave() => OnUi(() => Map(_form.NudgeRef_Save()), Fail());
        public NudgeStateDto RefCancel() => OnUi(() => Map(_form.NudgeRef_Cancel()), Fail());
    }
}
