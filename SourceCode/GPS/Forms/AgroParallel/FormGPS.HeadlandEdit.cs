// ============================================================================
// FormGPS.HeadlandEdit.cs — puente de FormGPS al editor de cabecera.
//
// La geometría ya NO vive acá: se movió tal cual a AgOpenGPS.Core/Classes/
// HeadlandEditor.cs. El motivo es que era `partial class FormGPS`, así que solo
// existía bajo WinForms: contra el motor headless /api/headland daba 404 y la
// pantalla Cabecera no podía construir nada.
//
// Esto quedó como delegación fina para que FormGPS y el motor corran EXACTAMENTE
// el mismo algoritmo. Si mañana hay que tocar el offset o el corte, se toca en
// un solo lugar y les cambia a los dos — que es justo lo que no pasaba antes.
//
// Los nombres HeadlandEdit_* se mantienen porque FormGpsHeadlandEditService (el
// adapter que marshalea al hilo UI) los llama por nombre.
// ============================================================================

using System.Collections.Generic;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private HeadlandEditor _hdEditor;

        /// <summary>
        /// Se crea al primer uso y no en el constructor: bnd/hdl/tool se asignan
        /// durante el arranque del form, y el editor guarda las referencias.
        /// (Sin `??=`: este proyecto compila en C# 7.3.)
        /// </summary>
        private HeadlandEditor HdEditor
        {
            get
            {
                if (_hdEditor == null)
                {
                    _hdEditor = new HeadlandEditor(
                        bnd, hdl, tool,
                        guardar: FileSaveHeadland,
                        ftOrMtoM: () => ftOrMtoM,
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
                return _hdEditor;
            }
        }

        // ---- lectura -------------------------------------------------------

        internal bool HeadlandEdit_HasBoundary() => HdEditor.E_HasBoundary();
        internal double[][] HeadlandEdit_FenceEN() => HdEditor.E_FenceEN();
        internal double[][] HeadlandEdit_HeadlandEN() => HdEditor.E_HeadlandEN();
        internal bool HeadlandEdit_IsHeadlandOn() => HdEditor.E_IsHeadlandOn();
        internal double HeadlandEdit_ToolWidthM() => HdEditor.E_ToolWidthM();
        internal string HeadlandEdit_Units() => HdEditor.E_Units();
        internal bool HeadlandEdit_IsSectionControlled() => HdEditor.E_IsSectionControlled();
        internal List<double[][]> HeadlandEdit_FencesEN() => HdEditor.E_FencesEN();
        internal int HeadlandEdit_BndSelect() => HdEditor.E_BndSelect();
        internal string HeadlandEdit_SliceMode() => HdEditor.E_SliceMode();
        internal bool HeadlandEdit_CanUndo() => HdEditor.E_CanUndo();
        internal double[][] HeadlandEdit_SliceEN() => HdEditor.E_SliceEN();
        internal double[] HeadlandEdit_APointEN() => HdEditor.E_APointEN();
        internal double[] HeadlandEdit_BPointEN() => HdEditor.E_BPointEN();

        // ---- construcción --------------------------------------------------

        internal bool HeadlandEdit_BuildAround(double distanceDisplay)
            => HdEditor.E_BuildAround(distanceDisplay);
        internal void HeadlandEdit_Reset() => HdEditor.E_Reset();
        internal void HeadlandEdit_TurnOff() => HdEditor.E_TurnOff();
        internal void HeadlandEdit_SetSectionControlled(bool on)
            => HdEditor.E_SetSectionControlled(on);

        // ---- reshape manual ------------------------------------------------

        internal string HeadlandEdit_Open() => HdEditor.E_Open();
        internal string HeadlandEdit_Tap(double easting, double northing, string mode, double distanceDisplay)
            => HdEditor.E_Tap(easting, northing, mode, distanceDisplay);
        internal void HeadlandEdit_CancelTouch() => HdEditor.E_CancelTouch();
        internal void HeadlandEdit_Extend(string endSide, bool grow) => HdEditor.E_Extend(endSide, grow);
        internal string HeadlandEdit_Clip() => HdEditor.E_Clip();
        internal void HeadlandEdit_Undo() => HdEditor.E_Undo();
        internal void HeadlandEdit_CloseSession() => HdEditor.E_CloseSession();
    }
}
