// ICoverageService — espejo del paint PilotX hace dentro de CPatches/triStrip.
// Hoy la única impl es FormGpsCoverageService que envuelve mf.triStrip[j].patchList.
// Cuando el cálculo se mueva al Core, esta interfaz queda igual y el view no
// se entera.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ICoverageService
    {
        /// <summary>Snapshot de los triángulos pintados por sección. Reservado
        /// para servir al view (canvas HUD) y para sincronizar con OrbitX cloud.</summary>
        CoverageSnapshot GetSnapshot();

        /// <summary>Snapshot incremental: <paramref name="cursor"/> es lo que el
        /// cliente YA tiene, como "j:p:v;j:p:v" (sección : índice de su último
        /// parche : vértices que tiene de ese parche). Devuelve solo lo nuevo
        /// (Full=false) o el snapshot completo (Full=true) si el cursor no
        /// matchea (lote cerrado, reset, primera vez).</summary>
        CoverageSnapshot GetSnapshot(string cursor);

        /// <summary>Borra la cobertura del lote actual. Equivale al botón
        /// "Borrar cobertura" de PilotX. Idempotente si no había nada pintado.</summary>
        void Reset();
    }
}
