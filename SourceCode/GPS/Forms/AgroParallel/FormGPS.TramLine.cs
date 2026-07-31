// ============================================================================
// FormGPS.TramLine.cs — puente de FormGPS al constructor de tramlines.
//
// La geometría ya NO vive acá: se movió tal cual a AgOpenGPS.Core/Classes/
// TramLineEditor.cs. Era `partial class FormGPS`, así que solo existía bajo
// WinForms y contra el motor headless /api/tramlines daba 404.
//
// Queda como delegación fina para que los dos stacks corran EXACTAMENTE el
// mismo algoritmo mientras WinForms siga existiendo (ver
// docs/RETIRAR-WINFORMS.md). Al retirarlo se borra este archivo y listo.
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private TramLineEditor _tramEditor;

        /// (Sin `??=`: este proyecto compila en C# 7.3.)
        private TramLineEditor TramEditor
        {
            get
            {
                if (_tramEditor == null)
                {
                    _tramEditor = new TramLineEditor(
                        tram, bnd, tool, curve, vehicle, trk,
                        guardarTram: FileSaveTram,
                        unidades: () => unitsFtM,
                        m2Display: () => m2FtOrM,
                        alphaGet: () => Properties.Settings.Default.setTram_alpha,
                        alphaSet: a =>
                        {
                            Properties.Settings.Default.setTram_alpha = a;
                            Properties.Settings.Default.Save();
                        },
                        maxDiagonal: () => maxFieldDistance,
                        refrescarModo: FixTramModeButton,
                        recalcularExtension: CalculateMinMax,
                        refrescarUi: PanelUpdateRightAndBottom);
                }
                return _tramEditor;
            }
        }

        internal TramLineEditor.TramSnapshot Tram_Snapshot(string error = null) => TramEditor.Tram_Snapshot(error);
        internal string Tram_Open() => TramEditor.Tram_Open();
        internal void Tram_CycleTrack(int dir) => TramEditor.Tram_CycleTrack(dir);
        internal void Tram_SwapSide() => TramEditor.Tram_SwapSide();
        internal void Tram_SetPasses(int p) => TramEditor.Tram_SetPasses(p);
        internal void Tram_SetStartPass(int s) => TramEditor.Tram_SetStartPass(s);
        internal void Tram_SetOuter(bool on) => TramEditor.Tram_SetOuter(on);
        internal void Tram_SetAlpha(double a) => TramEditor.Tram_SetAlpha(a);
        internal void Tram_AddLines() => TramEditor.Tram_AddLines();
        internal void Tram_DeleteAll() => TramEditor.Tram_DeleteAll();
        internal void Tram_Tap(double easting, double northing) => TramEditor.Tram_Tap(easting, northing);
        internal void Tram_CancelTouch() => TramEditor.Tram_CancelTouch();
        internal void Tram_CloseSession() => TramEditor.Tram_CloseSession();
        internal void Tram_CancelSession() => TramEditor.Tram_CancelSession();
    }
}
