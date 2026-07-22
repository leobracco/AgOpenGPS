// IPathsGeometryCalculator — geometria de caminos: youturn (giro de cabecera)
// + recorded path (camino grabado). Stage 5 de la migracion OpenGL del mapa
// (PilotX.Desktop): el render GL necesita dibujar dos polilineas simples —
// el giro Dubins/pattern activo en cabecera y el camino grabado manejando.
//
// Se agrupan en UN solo snapshot/endpoint para no duplicar boilerplate: cada
// una es una polilinea E/N que el render pinta como GL_LINE_STRIP con color
// propio (youturn naranja, recorded violeta).
//
// La impl FormGpsPathsCalculator envuelve mf.yt.ytList (List<vec3>) y
// mf.recPath.recList (List<CRecPathPt>). Cuando movamos esto al Core, se
// reemplaza la impl sin tocar el view.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IPathsGeometryCalculator
    {
        /// <summary>Geometria de caminos (youturn + recorded). Cadencia
        /// esperada: ~1 Hz (igual que tram/guidance) — solo cambia al generar
        /// un giro o grabar/cargar un camino. Usa revision para que el cliente
        /// saltee re-upload del VBO. Defensive: nunca tira.</summary>
        PathsGeometrySnapshot GetGeometry();
    }
}
