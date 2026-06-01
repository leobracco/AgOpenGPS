// ITramCalculator — geometria de tramlines (wheel tracks) + boundary outer/inner.
// Stage 4b de la migracion OpenGL del mapa (PilotX.Desktop): el render GL
// necesita dibujar las polilineas que marcan donde tienen que pasar las
// ruedas en pasadas siguientes (modo "FillTracks"), y los dos tracks pegados
// al borde del lote (modo "BoundaryTracks"). El modo "All" pinta todo y
// "None" no pinta nada.
//
// La impl FormGpsTramCalculator envuelve mf.tram (tramList + tramBndOuterArr +
// tramBndInnerArr + displayMode). Cuando movamos tram al Core, se reemplaza
// la impl sin tocar el view.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ITramCalculator
    {
        /// <summary>Geometria de tram (lineas + outer/inner + displayMode).
        /// Cadencia esperada: ~1 Hz (igual que guidance) — solo cambia al
        /// regenerar (cambio de passes/ancho/displayMode). Usa revision
        /// para que el cliente saltee re-upload del VBO. Defensive: nunca tira.</summary>
        TramGeometrySnapshot GetGeometry();
    }
}
