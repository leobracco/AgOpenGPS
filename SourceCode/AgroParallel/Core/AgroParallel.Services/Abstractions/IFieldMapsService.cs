// ============================================================================
// IFieldMapsService.cs — provee GeoJSON del lote actual para el preview "Mapas"
// en el Hub. No mantiene estado; cada llamada relee del filesystem.
//
// Fuente de datos:
//   - Sesiones VistaX:  <Field>/VistaX/vistax_<ts>(_heatmap).shp
//   - Boundary/Headland: AogStateSnapshot.Boundaries/Headlands (en E/N locales,
//                        se proyectan a lat/lon con flat-earth alrededor del
//                        pivote actual)
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IFieldMapsService
    {
        /// <summary>Lista de sesiones VistaX disponibles en el lote actual.</summary>
        MapasSesionesDto ListSesiones();

        /// <summary>GeoJSON del heatmap (polígonos) de una sesión. null si no existe.</summary>
        GeoJsonFeatureCollectionDto GetHeatmap(string ts);

        /// <summary>GeoJSON de puntos por surco de una sesión. null si no existe.</summary>
        GeoJsonFeatureCollectionDto GetPuntos(string ts);

        /// <summary>Boundary del lote actual como GeoJSON Polygon (multi-ring si hay islas).
        /// null si no hay lote abierto.</summary>
        GeoJsonFeatureCollectionDto GetBoundary();

        /// <summary>Headland (cabecera) del lote actual como LineString GeoJSON. null si no aplica.</summary>
        GeoJsonFeatureCollectionDto GetHeadland();
    }
}
