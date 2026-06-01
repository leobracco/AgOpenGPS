// IToolGeometryCalculator — geometria + estado por seccion del implemento.
// Stage 4a de la migracion OpenGL del mapa (PilotX.Desktop): el render GL
// necesita dibujar la barra del implemento como N rectangulos coloreados
// segun el estado (off/auto/manual) y el isMapping de cada seccion.
//
// La impl FormGpsToolGeometryCalculator envuelve mf.section[] + mf.tool +
// mf.vehicle. Cuando algun dia movamos el modelo del implemento al Core,
// se reemplaza por una impl propia sin tocar el view.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IToolGeometryCalculator
    {
        /// <summary>Geometria + estado de cada seccion en este instante.
        /// Cadencia esperada: ~4 Hz (igual que el HUD). No usa revision
        /// porque los puntos cambian cada frame que el tractor se mueve.
        /// Defensive: nunca tira.</summary>
        ToolGeometrySnapshot GetGeometry();
    }
}
