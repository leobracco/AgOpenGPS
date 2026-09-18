// ============================================================================
// INudgeService.cs — contrato del widget "Mover guía" (FormNudge + FormRefNudge).
// Todas las operaciones corren en el hilo UI de FormGPS y devuelven el estado
// completo para refrescar el panel. El movimiento se ve en el mapa.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface INudgeService
    {
        // Estado actual sin tocar nada (poll del panel).
        NudgeStateDto GetState();

        // --- Guía activa (FormNudge) ---

        // Mueve la guía activa un paso: dir=-1 izquierda, dir=+1 derecha.
        NudgeStateDto Move(int dir);

        // Mueve media herramienta ((ancho - solape)/2) hacia dir.
        NudgeStateDto Half(int dir);

        // Vuelve el corrimiento a cero (NudgeDistanceReset).
        NudgeStateDto Zero();

        // Ajusta la guía al pivote del vehículo (SnapToPivot).
        NudgeStateDto ToPivot();

        // Cambia el paso (unidades display: cm o in) y lo persiste.
        NudgeStateDto SetStep(double valueDisplay);

        // Cierra el widget: persiste las guías a archivo (FormClosing nativo).
        NudgeStateDto Close();

        // --- Guía de referencia (FormRefNudge) ---

        // Abre la sesión de referencia: backup de todas las guías para Cancelar.
        NudgeStateDto RefOpen();

        NudgeStateDto RefMove(int dir);
        NudgeStateDto RefHalf(int dir);
        NudgeStateDto RefSetStep(double valueDisplay);

        // Guarda las guías movidas y cierra la sesión.
        NudgeStateDto RefSave();

        // Restaura el backup (descarta los movimientos) y cierra la sesión.
        NudgeStateDto RefCancel();
    }
}
