// ============================================================================
// IHeadlandEditService.cs — contrato del editor de cabecera (flujo Build Around).
// Implementado por FormGpsHeadlandEditService (proyecto GPS) que marshalea al
// hilo UI de PilotX. Todas las distancias entran en UNIDADES DISPLAY (ft o m
// según mf.unitsFtM); el adapter/partial las convierte a metros.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IHeadlandEditService
    {
        // Estado actual: contorno + cabecera + flags. Nunca tira: si no hay lote
        // devuelve HasField=false con listas vacías.
        HeadlandEditStateDto GetState();

        // Offset del contorno hacia adentro por distanceDisplay (unidades display).
        // distanceDisplay==0 copia contorno→cabecera. Persiste con FileSaveHeadland.
        HeadlandEditResultDto BuildAround(double distanceDisplay);

        // Cabecera = copia exacta del contorno. Persiste.
        HeadlandEditResultDto Reset();

        // Limpia la cabecera, isHeadlandOn=false. Persiste.
        HeadlandEditResultDto TurnOff();

        // Setea "secciones controladas por cabecera" (espeja Settings). Devuelve el
        // valor efectivo.
        bool SetSectionControlled(bool on);

        // ── Reshape manual (fase 2, ex FormHeadLine slice) ──────────────────
        // Los métodos devuelven el estado COMPLETO (sin poll: la geometría solo
        // cambia por acción del usuario).

        // Iniciar sesión de edición: hdLine = contorno si estaba vacía, spacing
        // mínimo si no; limpia el estado de toques.
        HeadlandEditStateDto Open();

        // Toque en el mapa (E/N metros). Primer tap = punto A (contorno más
        // cercano entre todos), segundo = B → arma la línea de corte
        // curva/recta con extensiones 30 m + offset por distanceDisplay.
        HeadlandEditStateDto Tap(double easting, double northing, string mode, double distanceDisplay);

        // Descartar el toque A pendiente y la línea de corte.
        HeadlandEditStateDto CancelTouch();

        // Extender (+9 m en pasos de 1 m) o encoger (-5 pts) un extremo ("a"/"b").
        HeadlandEditStateDto Extend(string end, bool grow);

        // Cortar: reemplaza el segmento de la cabecera entre los 2 cruces con la
        // línea de corte (guarda backup para Undo). Error "cruces" si no cruza.
        HeadlandEditStateDto Clip();

        // Volver la cabecera al backup previo al último corte.
        HeadlandEditStateDto Undo();

        // Cierre del widget: suavizado por decimación de rumbo + persistir +
        // refresco de paneles (bloque post-diálogo del launcher nativo).
        void CloseSession();
    }
}
