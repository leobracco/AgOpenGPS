// ============================================================================
// ICabeceraLineasService.cs — contrato del constructor de cabecera por líneas
// (ex FormHeadAche). Implementado por FormGpsCabeceraLineasService (proyecto
// GPS) que marshalea al hilo UI de PilotX. Distancias en UNIDADES DISPLAY
// (ft o m según configuración); el partial convierte a metros con ftOrMtoM.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ICabeceraLineasService
    {
        // Estado actual (geometría completa). Nunca tira.
        CabeceraLineasStateDto GetState();

        // Sesión de edición: replica FormHeadAche_Load (CalculateMinMax,
        // idx=-1, FileLoadHeadLines, hdLine.Clear, reset touch).
        CabeceraLineasStateDto Open();

        // Tap sobre el mapa en coords de campo E/N (metros). Primer tap = punto A
        // (contorno más cercano entre todos), segundo tap = punto B (mismo
        // contorno) y construye la línea con mode ("curve"|"ab") offseteada
        // distanceDisplay hacia adentro.
        CabeceraLineasStateDto Tap(double easting, double northing, string mode, double distanceDisplay);

        // Descartar el punto A tocado (btnCancelTouch).
        CabeceraLineasStateDto CancelTouch();

        // Ciclar línea seleccionada (+1 / -1). Limpia hdLine (regla nativa).
        CabeceraLineasStateDto Cycle(int dir);

        // Borrar la línea seleccionada.
        CabeceraLineasStateDto DeleteTrack();

        // Extender/encoger extremos de la línea seleccionada.
        // end: "a" (inicio) | "b" (final). grow: true=+9 m, false=-5 pts.
        CabeceraLineasStateDto Extend(string end, bool grow);

        // "Build": arma la cabecera cruzando las líneas (btnBndLoop_Click).
        CabeceraLineasStateDto BuildHeadland();

        // "Reset": limpia hdLine + touch (btnDeleteHeadland).
        CabeceraLineasStateDto ResetHeadland();

        // Apagar cabecera (btnHeadlandOff): limpia, persiste, isHeadlandOn=false,
        // isHydLiftOn=false. El widget se cierra del lado JS.
        CabeceraLineasStateDto TurnOff();

        // Secciones controladas por cabecera (espeja Settings). Devuelve efectivo.
        bool SetSectionControlled(bool on);

        // Cierre de sesión (btnExit / cierre de ventana): FileSaveHeadLines,
        // hdl.idx=0|-1, recalcular isHeadlandOn y refrescar paneles/zoom.
        void CloseSession();
    }
}
