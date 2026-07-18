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
    }
}
