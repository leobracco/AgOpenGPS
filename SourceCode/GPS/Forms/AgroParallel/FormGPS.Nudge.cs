// ============================================================================
// FormGPS.Nudge.cs — puente de FormGPS al widget "Mover guía".
//
// La lógica ya NO vive acá: se movió a AgOpenGPS.Core/Classes/NudgeEditor.cs
// (/api/nudge daba 404 contra el motor headless). Delegación fina mientras
// WinForms exista (ver docs/RETIRAR-WINFORMS.md).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private NudgeEditor _nudgeEditor;

        /// (Sin `??=`: este proyecto compila en C# 7.3.)
        private NudgeEditor NudgeEd
        {
            get
            {
                if (_nudgeEditor == null)
                {
                    _nudgeEditor = new NudgeEditor(
                        trk, ABLine, curve, tool,
                        hayLote: () => isJobStarted,
                        guardarGuias: FileSaveTracks,
                        snapGet: () => Properties.Settings.Default.setAS_snapDistance,
                        snapSet: v =>
                        {
                            Properties.Settings.Default.setAS_snapDistance = v;
                            Properties.Settings.Default.Save();
                        },
                        snapRefGet: () => Properties.Settings.Default.setAS_snapDistanceRef,
                        snapRefSet: v =>
                        {
                            Properties.Settings.Default.setAS_snapDistanceRef = v;
                            Properties.Settings.Default.Save();
                        },
                        esMetrico: () => isMetric,
                        cm2Display: () => cm2CmOrIn,
                        m2CmDisplay: () => m2InchOrCm,
                        display2m: () => inchOrCm2m,
                        unidadesChicas: () => unitsInCm);
                }
                return _nudgeEditor;
            }
        }

        internal bool Nudge_HasTrack() => NudgeEd.Nudge_HasTrack();
        internal NudgeEditor.NudgeStateSnapshot Nudge_Snapshot() => NudgeEd.Nudge_Snapshot();
        internal NudgeEditor.NudgeStateSnapshot Nudge_Move(int dir) => NudgeEd.Nudge_Move(dir);
        internal NudgeEditor.NudgeStateSnapshot Nudge_Half(int dir) => NudgeEd.Nudge_Half(dir);
        internal NudgeEditor.NudgeStateSnapshot Nudge_Zero() => NudgeEd.Nudge_Zero();
        internal NudgeEditor.NudgeStateSnapshot Nudge_ToPivot() => NudgeEd.Nudge_ToPivot();
        internal NudgeEditor.NudgeStateSnapshot Nudge_SetStep(double v) => NudgeEd.Nudge_SetStep(v);
        internal NudgeEditor.NudgeStateSnapshot Nudge_Close() => NudgeEd.Nudge_Close();
        internal NudgeEditor.NudgeStateSnapshot NudgeRef_Open() => NudgeEd.NudgeRef_Open();
        internal NudgeEditor.NudgeStateSnapshot NudgeRef_Move(int dir) => NudgeEd.NudgeRef_Move(dir);
        internal NudgeEditor.NudgeStateSnapshot NudgeRef_Half(int dir) => NudgeEd.NudgeRef_Half(dir);
        internal NudgeEditor.NudgeStateSnapshot NudgeRef_SetStep(double v) => NudgeEd.NudgeRef_SetStep(v);
        internal NudgeEditor.NudgeStateSnapshot NudgeRef_Save() => NudgeEd.NudgeRef_Save();
        internal NudgeEditor.NudgeStateSnapshot NudgeRef_Cancel() => NudgeEd.NudgeRef_Cancel();
    }
}
