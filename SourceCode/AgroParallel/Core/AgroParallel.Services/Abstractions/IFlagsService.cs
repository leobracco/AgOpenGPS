// ============================================================================
// IFlagsService.cs — contrato del widget "Banderas" (banderas.html).
// Reemplaza FormFlags (lista/selección/borrado/notas) y FormEnterFlag
// (alta por lat/lon + import/export CSV). Implementado por PilotX
// (FormGpsFlagsService) sobre el hilo UI.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IFlagsService
    {
        /// <summary>Estado actual (lista + distancias live).</summary>
        FlagsStateDto GetState();

        /// <summary>Selecciona la bandera number (1-based).</summary>
        FlagsStateDto Pick(int number);

        /// <summary>Borra la bandera seleccionada y persiste.</summary>
        FlagsStateDto Delete();

        /// <summary>Actualiza las notas de la bandera seleccionada.</summary>
        FlagsStateDto SetNotes(string notes);

        /// <summary>
        /// Crea una bandera. Si useCurrent, usa la posición actual del tractor
        /// (ignora lat/lon); si no, usa lat/lon. color: 0 rojo, 1 verde, 2 amarillo.
        /// </summary>
        FlagsStateDto Add(double lat, double lon, int color, bool useCurrent);

        /// <summary>Cierre del widget: deselecciona y persiste (ex btnExit).</summary>
        FlagsStateDto CloseSession();

        /// <summary>Importa banderas de un .txt/.csv (diálogo nativo).</summary>
        FlagsStateDto Import();

        /// <summary>Exporta banderas a un .txt/.csv (diálogo nativo).</summary>
        FlagsStateDto Export();
    }
}
