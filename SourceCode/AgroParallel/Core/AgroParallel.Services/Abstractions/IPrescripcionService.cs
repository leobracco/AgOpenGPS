// IPrescripcionService — acceso a prescripciones variable-rate (GeoJSON).
// Consumer principal: QuantiXMotorBridge para variable-rate por zona.
// Consumer secundario: UI (pages/prescripciones.html) para listar y elegir
// la activa, y futuras integraciones (FlowX líquidos, SectionX).
//
// Responsabilidades:
//   - Listar archivos .geojson en <BaseDir>/data/prescripciones/
//   - Cargar el archivo activo en memoria, parseando rings + dosis por feature
//   - Cachear el resultado (no re-parsear en cada tick del bridge)
//   - Lookup de dosis para una posición Lat/Lon (point-in-polygon)
//
// No-objetivos:
//   - No descarga archivos (eso es trabajo de OrbitXSync)
//   - No proyecta coordenadas (operamos en lat/lon WGS84 directamente;
//     error de ~0.1% en 5 km, despreciable para dosificación)

using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IPrescripcionService
    {
        /// <summary>Lista los archivos .geojson disponibles localmente.</summary>
        List<PrescripcionListItemDto> ListAvailable();

        /// <summary>Devuelve la prescripción activa cargada/parseada en
        /// memoria. null si no hay activa o el archivo no se puede parsear.</summary>
        PrescripcionDto GetActive();

        /// <summary>Marca una prescripción como activa por su id (slug).
        /// La carga inmediatamente y guarda el estado en prescripciones-state.json.
        /// Devuelve false si el id no existe.</summary>
        bool SetActive(string id, string propiedadDosis);

        /// <summary>Limpia la activa (sin borrar el archivo). El bridge vuelve
        /// al modo legacy (shapefile/DosisFija).</summary>
        void ClearActive();

        /// <summary>Lookup central: dado Lat/Lon WGS84, devuelve la dosis del
        /// polígono que lo contiene. 0 si no hay activa o el punto está fuera
        /// de todos los features. Thread-safe (lectura concurrente desde el
        /// timer del bridge mientras la UI hace SetActive).</summary>
        double GetDoseAt(double lat, double lon);

        /// <summary>Edición de campo: cambia la dosis de UNA zona (feature) de
        /// una prescripción y la persiste al .geojson. Si es la activa, se
        /// recarga al instante (LoadedUtc nuevo → el piloto la reproyecta y el
        /// bridge dosifica con el valor nuevo). El índice de zona es el orden
        /// de features del archivo — el mismo con el que el piloto dibuja.
        /// Devuelve false si el id/zona no existen o el archivo no se puede
        /// escribir.</summary>
        bool SetZoneDose(string id, int zoneIndex, double dose);
    }
}
