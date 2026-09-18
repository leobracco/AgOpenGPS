// ============================================================================
// ITramLineService.cs — contrato del constructor de tramlines HTML.
// Implementado por FormGpsTramLineService (proyecto GPS) que marshalea al
// hilo UI de PilotX. Distancias en UNIDADES DISPLAY (ft o m según unitsFtM).
// Sin poll: cada acción devuelve el estado completo.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ITramLineService
    {
        // Estado completo (sin poll).
        TramLineStateDto GetState();

        // Iniciar sesión: carga líneas de guiado AB/Curve visibles, valida
        // que haya al menos una. Error "sin-guias" si no.
        TramLineStateDto Open();

        // Ciclar la línea de guiado seleccionada (+1/-1).
        TramLineStateDto CycleTrack(int dir);

        // Cambiar lado de la construcción (swap isVisible).
        TramLineStateDto SwapSide();

        // Ajustar passes (cantidad de líneas de tram).
        TramLineStateDto SetPasses(int passes);

        // Ajustar start pass.
        TramLineStateDto SetStartPass(int startPass);

        // Toggle outer tram boundary.
        TramLineStateDto SetOuter(bool on);

        // Cambiar alpha (opacidad de trams guardados, 0.2–1.0).
        TramLineStateDto SetAlpha(double alpha);

        // Agregar los trams nuevos (preview) a los guardados.
        TramLineStateDto AddLines();

        // Borrar todos los trams (nuevos + guardados + outer/inner bnd).
        TramLineStateDto DeleteAll();

        // Tap en el mapa (E/N metros). Flujo de 3 toques para cortar:
        // 1) ptA, 2) ptB, 3) side → corta los trams que cruzan la recta A-B,
        // eliminando los puntos del lado tocado.
        TramLineStateDto Tap(double easting, double northing);

        // Descartar los toques de corte (reset step a 0).
        TramLineStateDto CancelTouch();

        // Salir guardando (no cancel).
        void CloseSession();

        // Salir cancelando (revert trams).
        void CancelSession();
    }
}
