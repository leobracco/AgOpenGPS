// ============================================================================
// FormGPS.TramSimple.cs — puente de FormGPS al panel simple de tramlines.
//
// La lógica ya NO vive acá: se movió a AgOpenGPS.Core/Classes/
// TramSimpleEditor.cs (/api/tram-simple daba 404 contra el motor headless).
// Delegación fina mientras WinForms exista (ver docs/RETIRAR-WINFORMS.md).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private TramSimpleEditor _tramSimpleEditor;

        /// (Sin `??=`: este proyecto compila en C# 7.3.)
        private TramSimpleEditor TramSimpleEd
        {
            get
            {
                if (_tramSimpleEditor == null)
                {
                    _tramSimpleEditor = new TramSimpleEditor(
                        trk, tram, ABLine, tool, bnd, curve, vehicle,
                        guardarTram: FileSaveTram,
                        guardarGuias: FileSaveTracks,
                        unidades: () => unitsFtM,
                        m2Display: () => m2FtOrM,
                        hayLote: () => isJobStarted,
                        maxDiagonal: () => maxFieldDistance,
                        pasadasGet: () => Properties.Settings.Default.setTram_passes,
                        pasadasSet: p =>
                        {
                            Properties.Settings.Default.setTram_passes = p;
                            Properties.Settings.Default.Save();
                        },
                        alphaGet: () => Properties.Settings.Default.setTram_alpha,
                        alphaSet: a =>
                        {
                            Properties.Settings.Default.setTram_alpha = a;
                            Properties.Settings.Default.Save();
                        },
                        cerrarVentanasFlotantes: CloseTopMosts,
                        refrescarModo: FixTramModeButton,
                        refrescarUi: PanelUpdateRightAndBottom);
                }
                return _tramSimpleEditor;
            }
        }

        internal bool TramSimple_HasTrack() => TramSimpleEd.TramSimple_HasTrack();
        internal bool TramSimple_HasBoundary() => TramSimpleEd.TramSimple_HasBoundary();
        internal bool TramSimple_IsCurve() => TramSimpleEd.TramSimple_IsCurve();
        internal TramSimpleEditor.TramSimpleStateSnapshot TramSimple_Open() => TramSimpleEd.TramSimple_Open();
        internal TramSimpleEditor.TramSimpleStateSnapshot TramSimple_SetPasses(int p) => TramSimpleEd.TramSimple_SetPasses(p);
        internal TramSimpleEditor.TramSimpleStateSnapshot TramSimple_SetAlpha(int pc) => TramSimpleEd.TramSimple_SetAlpha(pc);
        internal TramSimpleEditor.TramSimpleStateSnapshot TramSimple_SetMode(string m) => TramSimpleEd.TramSimple_SetMode(m);
        internal TramSimpleEditor.TramSimpleStateSnapshot TramSimple_SwapAB() => TramSimpleEd.TramSimple_SwapAB();
        internal void TramSimple_Commit(bool save) => TramSimpleEd.TramSimple_Commit(save);
        internal TramSimpleEditor.TramSimpleStateSnapshot TramSimple_Snapshot() => TramSimpleEd.TramSimple_Snapshot();
    }
}
