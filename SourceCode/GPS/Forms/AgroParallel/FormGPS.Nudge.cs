// ============================================================================
// FormGPS.Nudge.cs — lógica del widget "Mover guía" (mover-guia.html).
// Reemplaza los WinForms FormNudge (guía activa) y FormRefNudge (referencia).
// La geometría es idéntica a esos forms: NudgeTrack / NudgeRefTrack / SnapToPivot
// sobre trk; acá vive dentro de FormGPS para que el adapter INudgeService la
// invoque sin abrir forms. Todos los métodos asumen hilo UI (el adapter marshalea).
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // Sesión de nudge de referencia: backup para Cancelar (gTemp del form nativo).
        private List<CTrk> nudgeRefBackup;
        private double nudgeRefMoved;

        internal bool Nudge_HasTrack()
        {
            return trk != null && trk.gArr != null
                   && trk.idx >= 0 && trk.idx < trk.gArr.Count;
        }

        // Paso en metros (los settings guardan cm, igual que FormNudge).
        private double Nudge_StepMeters() =>
            Properties.Settings.Default.setAS_snapDistance * 0.01;

        private double NudgeRef_StepMeters() =>
            Properties.Settings.Default.setAS_snapDistanceRef * 0.01;

        // Setting (cm) → unidades display: cm entero o pulgadas con 1 decimal.
        private double Nudge_StepDisplay(double settingCm)
        {
            if (isMetric) return (int)settingCm;
            return Math.Round(settingCm * cm2CmOrIn, 1);
        }

        internal NudgeStateSnapshot Nudge_Snapshot()
        {
            double offset = Nudge_HasTrack() ? trk.gArr[trk.idx].nudgeDistance : 0;
            return new NudgeStateSnapshot
            {
                HasTrack = Nudge_HasTrack(),
                OffsetDisplay = (int)(offset * m2InchOrCm),
                StepDisplay = Nudge_StepDisplay(Properties.Settings.Default.setAS_snapDistance),
                StepRefDisplay = Nudge_StepDisplay(Properties.Settings.Default.setAS_snapDistanceRef),
                RefActive = nudgeRefBackup != null,
                RefMovedDisplay = (int)(nudgeRefMoved * m2InchOrCm),
                Units = (unitsInCm ?? "cm").Trim()
            };
        }

        // --- Guía activa (réplica de FormNudge) ---

        internal NudgeStateSnapshot Nudge_Move(int dir)
        {
            if (Nudge_HasTrack()) trk.NudgeTrack(Math.Sign(dir) * Nudge_StepMeters());
            return Nudge_Snapshot();
        }

        internal NudgeStateSnapshot Nudge_Half(int dir)
        {
            if (Nudge_HasTrack())
                trk.NudgeTrack(Math.Sign(dir) * (tool.width - tool.overlap) * 0.5);
            return Nudge_Snapshot();
        }

        internal NudgeStateSnapshot Nudge_Zero()
        {
            if (Nudge_HasTrack()) trk.NudgeDistanceReset();
            return Nudge_Snapshot();
        }

        internal NudgeStateSnapshot Nudge_ToPivot()
        {
            if (Nudge_HasTrack()) trk.SnapToPivot();
            return Nudge_Snapshot();
        }

        // valueDisplay en cm (métrico) o in (imperial), igual que nudSnapDistance.
        internal NudgeStateSnapshot Nudge_SetStep(double valueDisplay)
        {
            if (valueDisplay < 0) valueDisplay = 0;
            double meters = valueDisplay * inchOrCm2m;
            Properties.Settings.Default.setAS_snapDistance = meters * 100;
            Properties.Settings.Default.Save();
            return Nudge_Snapshot();
        }

        // FormClosing de FormNudge: persistir las guías.
        internal NudgeStateSnapshot Nudge_Close()
        {
            FileSaveTracks();
            return Nudge_Snapshot();
        }

        // --- Guía de referencia (réplica de FormRefNudge) ---

        internal NudgeStateSnapshot NudgeRef_Open()
        {
            if (!Nudge_HasTrack()) return Nudge_Snapshot();
            if (nudgeRefBackup == null)
            {
                nudgeRefBackup = new List<CTrk>();
                foreach (var item in trk.gArr) nudgeRefBackup.Add(new CTrk(item));
                nudgeRefMoved = 0;
            }
            return Nudge_Snapshot();
        }

        internal NudgeStateSnapshot NudgeRef_Move(int dir)
        {
            if (Nudge_HasTrack())
            {
                double d = Math.Sign(dir) * NudgeRef_StepMeters();
                trk.NudgeRefTrack(d);
                nudgeRefMoved += d;
            }
            return Nudge_Snapshot();
        }

        internal NudgeStateSnapshot NudgeRef_Half(int dir)
        {
            if (Nudge_HasTrack())
            {
                double d = Math.Sign(dir) * (tool.width - tool.overlap) * 0.5;
                trk.NudgeRefTrack(d);
                nudgeRefMoved += d;
            }
            return Nudge_Snapshot();
        }

        internal NudgeStateSnapshot NudgeRef_SetStep(double valueDisplay)
        {
            if (valueDisplay < 0) valueDisplay = 0;
            double meters = valueDisplay * inchOrCm2m;
            Properties.Settings.Default.setAS_snapDistanceRef = meters * 100;
            Properties.Settings.Default.Save();
            return Nudge_Snapshot();
        }

        internal NudgeStateSnapshot NudgeRef_Save()
        {
            FileSaveTracks();
            nudgeRefBackup = null;
            nudgeRefMoved = 0;
            return Nudge_Snapshot();
        }

        // btnCancelMain nativo: restaurar el backup e invalidar las líneas armadas.
        internal NudgeStateSnapshot NudgeRef_Cancel()
        {
            if (nudgeRefBackup != null)
            {
                trk.gArr.Clear();
                foreach (var item in nudgeRefBackup) trk.gArr.Add(new CTrk(item));

                ABLine.isABValid = false;
                curve.isCurveValid = false;

                nudgeRefBackup = null;
                nudgeRefMoved = 0;
            }
            return Nudge_Snapshot();
        }

        // POCO intermedio (assembly GPS) — el adapter lo copia al DTO de Models.
        internal sealed class NudgeStateSnapshot
        {
            public bool HasTrack;
            public int OffsetDisplay;
            public double StepDisplay;
            public double StepRefDisplay;
            public bool RefActive;
            public int RefMovedDisplay;
            public string Units;
        }
    }
}
