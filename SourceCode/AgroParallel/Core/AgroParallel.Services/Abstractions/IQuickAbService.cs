// ============================================================================
// IQuickAbService.cs — contrato del widget "AB rápido" (FormQuickAB).
// Todas las operaciones corren en el hilo UI de FormGPS y devuelven el estado
// completo para refrescar el panel. El preview de la guía se ve en el mapa.
// GetState además "tickea" el punto B mientras se maneja (réplica del timer
// de 500 ms del form nativo) — el JS pollea a ese ritmo.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IQuickAbService
    {
        /// <summary>Estado actual (+tick del preview si se está manejando).</summary>
        QuickAbStateDto GetState();

        /// <summary>Arranca una sesión en "curve" | "ab" | "aplus".</summary>
        QuickAbStateDto Start(string mode);

        /// <summary>Alterna lado de referencia (derecha/izquierda).</summary>
        QuickAbStateDto ToggleSide();

        /// <summary>Marca el punto A (curva: arranca a grabar / agrega punto manual).</summary>
        QuickAbStateDto MarkA();

        /// <summary>Marca el punto B (curva: cierra y arma track; ab: fija B).</summary>
        QuickAbStateDto MarkB();

        /// <summary>Curva: pausa/reanuda la grabación de puntos.</summary>
        QuickAbStateDto PauseToggle();

        /// <summary>A+: fija el rumbo en grados (corta el seguimiento del GPS).</summary>
        QuickAbStateDto SetHeading(double degrees);

        /// <summary>ab/aplus: confirma la línea y arma el track (fase name).</summary>
        QuickAbStateDto Commit();

        /// <summary>Guarda la guía con nombre y persiste (FileSaveTracks).</summary>
        QuickAbStateDto Save(string name);

        /// <summary>Cancela la sesión y limpia el preview.</summary>
        QuickAbStateDto Cancel();
    }
}
