// IAogStateProvider — linchpin del desacople AgroParallel ↔ PilotX.
// Los servicios (QuantiX/SectionX/OrbitX) consumen estado de PilotX SOLO
// a través de esta interfaz. La implementación FormGpsStateProvider vive
// en el proyecto GPS (único lugar autorizado a usar `using AgOpenGPS;`).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IAogStateProvider
    {
        /// <summary>Snapshot tipado del estado de PilotX en este instante.</summary>
        AogStateSnapshot GetSnapshot();

        /// <summary>
        /// Lee el valor numérico de un campo DBF arbitrario del shapefile
        /// activo, en el polígono actualmente bajo el tractor. Retorna 0 si
        /// no hay shapefile, no hay polígono, o el campo no es numérico.
        /// Habilita motores QuantiX con `CampoDosis` apuntando a un atributo
        /// específico del shape (multi-producto en una sola capa).
        /// </summary>
        double GetShapeFieldDose(string fieldName);

        /// <summary>
        /// Polígonos de la capa shapefile activa (prescripción/dosis). El
        /// cliente la pinta como overlay del mapa. Devuelve null si no hay
        /// shapefile cargado.
        /// </summary>
        ShapeSnapshot GetShape();

        /// <summary>
        /// Lista los campos DBF del shapefile activo con su stats numérico.
        /// La UI de QuantiX la usa para poblar el dropdown CampoDosis.
        /// Si no hay shapefile, devuelve un ShapeFieldsSnapshot vacío.
        /// </summary>
        ShapeFieldsSnapshot GetShapeFields();
    }
}
