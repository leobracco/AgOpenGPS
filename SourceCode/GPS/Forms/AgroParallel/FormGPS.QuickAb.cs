// ============================================================================
// FormGPS.QuickAb.cs — puente de FormGPS al widget "AB rápido".
//
// La lógica ya NO vive acá: se movió a AgOpenGPS.Core/Classes/QuickAbEditor.cs.
// Era `partial class FormGPS`, así que solo existía bajo WinForms y contra el
// motor headless /api/quickab daba 404.
//
// Las tres llamadas que en el original eran `btnXxx.PerformClick()` —usar el
// botón de la pantalla como forma de invocar lógica— ahora entran al editor
// como callbacks, así que el motor puede hacer lo mismo sin pantalla.
//
// Delegación fina mientras WinForms exista (ver docs/RETIRAR-WINFORMS.md).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private QuickAbEditor _quickAbEditor;

        /// (Sin `??=`: este proyecto compila en C# 7.3.)
        private QuickAbEditor QuickAbEd
        {
            get
            {
                if (_quickAbEditor == null)
                {
                    _quickAbEditor = new QuickAbEditor(
                        ABLine, curve, trk, tool, yt, ct,
                        pivote: () => pivotAxlePos,
                        hayLote: () => isJobStarted,
                        pilotoPrendido: () => isBtnAutoSteerOn,
                        guardarGuias: FileSaveTracks,
                        alternarContorno: () => btnContour.PerformClick(),
                        alternarPiloto: () => btnAutoSteer.PerformClick(),
                        alternarGiro: () => btnAutoYouTurn.PerformClick(),
                        refrescarUi: () =>
                        {
                            twoSecondCounter = 100;
                            PanelUpdateRightAndBottom();
                        });
                }
                return _quickAbEditor;
            }
        }

        internal QuickAbEditor.QuickAbSnapshot QuickAb_Snapshot() => QuickAbEd.QuickAb_Snapshot();
        internal QuickAbEditor.QuickAbSnapshot QuickAb_Tick() => QuickAbEd.QuickAb_Tick();
        internal QuickAbEditor.QuickAbSnapshot QuickAb_Start(string mode) => QuickAbEd.QuickAb_Start(mode);
        internal QuickAbEditor.QuickAbSnapshot QuickAb_ToggleSide() => QuickAbEd.QuickAb_ToggleSide();
        internal QuickAbEditor.QuickAbSnapshot QuickAb_MarkA() => QuickAbEd.QuickAb_MarkA();
        internal QuickAbEditor.QuickAbSnapshot QuickAb_PauseToggle() => QuickAbEd.QuickAb_PauseToggle();
        internal QuickAbEditor.QuickAbSnapshot QuickAb_SetHeading(double d) => QuickAbEd.QuickAb_SetHeading(d);
        internal QuickAbEditor.QuickAbSnapshot QuickAb_MarkB() => QuickAbEd.QuickAb_MarkB();
        internal QuickAbEditor.QuickAbSnapshot QuickAb_Commit() => QuickAbEd.QuickAb_Commit();
        internal QuickAbEditor.QuickAbSnapshot QuickAb_Save(string name) => QuickAbEd.QuickAb_Save(name);
        internal QuickAbEditor.QuickAbSnapshot QuickAb_Cancel() => QuickAbEd.QuickAb_Cancel();
    }
}
