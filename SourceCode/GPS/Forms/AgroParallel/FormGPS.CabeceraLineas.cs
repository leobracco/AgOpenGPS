// ============================================================================
// FormGPS.CabeceraLineas.cs — puente de FormGPS al constructor de cabecera por
// líneas.
//
// La geometría ya NO vive acá: se movió tal cual a AgOpenGPS.Core/Classes/
// CabeceraLineasEditor.cs. Era `partial class FormGPS`, así que solo existía
// bajo WinForms y contra el motor headless /api/cabecera-lineas daba 404.
//
// Queda como delegación fina para que FormGPS y el motor corran EXACTAMENTE el
// mismo algoritmo. Si hay que tocar el offset, el corte o el armado de la
// cabecera, se toca en el Core y les cambia a los dos.
//
// Los nombres CabeceraLineas_* se mantienen porque FormGpsCabeceraLineasService
// (el adapter que marshalea al hilo UI) los llama por nombre.
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private CabeceraLineasEditor _cabLinEditor;

        /// <summary>
        /// Se crea al primer uso, no en el constructor: bnd/hdl/tool/curve se
        /// asignan durante el arranque del form y el editor guarda las
        /// referencias. (Sin `??=`: este proyecto compila en C# 7.3.)
        /// </summary>
        private CabeceraLineasEditor CabLinEditor
        {
            get
            {
                if (_cabLinEditor == null)
                {
                    _cabLinEditor = new CabeceraLineasEditor(
                        bnd, hdl, tool, curve, vehicle,
                        guardarLineas: FileSaveHeadLines,
                        cargarLineas: FileLoadHeadLines,
                        guardarCabecera: FileSaveHeadland,
                        hayLote: () => isJobStarted,
                        ftOrMtoM: () => ftOrMtoM,
                        m2Display: () => m2FtOrM,
                        unidades: () => unitsFtM,
                        guardarSeccionControlada: on =>
                        {
                            Properties.Settings.Default.setHeadland_isSectionControlled = on;
                            Properties.Settings.Default.Save();
                        },
                        recalcularExtension: CalculateMinMax,
                        refrescarUi: () =>
                        {
                            PanelsAndOGLSize();
                            PanelUpdateRightAndBottom();
                            SetZoom();
                        });
                }
                return _cabLinEditor;
            }
        }

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_Snapshot(string error = null)
            => CabLinEditor.CabLin_Snapshot(error);

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_Open()
            => CabLinEditor.CabLin_Open();

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_Tap(
            double easting, double northing, string mode, double distanceDisplay)
            => CabLinEditor.CabLin_Tap(easting, northing, mode, distanceDisplay);

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_CancelTouch()
            => CabLinEditor.CabLin_CancelTouch();

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_Cycle(int dir)
            => CabLinEditor.CabLin_Cycle(dir);

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_DeleteTrack()
            => CabLinEditor.CabLin_DeleteTrack();

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_Extend(string endSide, bool grow)
            => CabLinEditor.CabLin_Extend(endSide, grow);

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_BuildHeadland()
            => CabLinEditor.CabLin_BuildHeadland();

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_ResetHeadland()
            => CabLinEditor.CabLin_ResetHeadland();

        internal CabeceraLineasEditor.CabLinSnapshot CabLin_TurnOff()
            => CabLinEditor.CabLin_TurnOff();

        internal void CabLin_SetSectionControlled(bool on)
            => CabLinEditor.CabLin_SetSectionControlled(on);

        internal void CabLin_CloseSession()
            => CabLinEditor.CabLin_CloseSession();
    }
}
