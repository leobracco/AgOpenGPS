// IShapefileService — operaciones write-side dedicadas a la capa de shapefile
// activa de PilotX. Vive fuera de IAogCommandSink (que es whitelist estrecha) para
// no contaminar la interfaz de comandos con concerns de I/O de archivos.
//
// Implementación PilotX-side: GPS/AgroParallel/Common/FormGpsShapefileService.cs
// que marshallea LoadShapefileFromExternal a la UI thread del FormGPS.

using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IShapefileService
    {
        /// <summary>
        /// Persiste el set de archivos en el directorio del lote actual y dispara
        /// FormGPS.LoadShapefileFromExternal con el .shp resultante. Requiere
        /// que haya un job abierto (PilotX no carga shapefile sin lote).
        /// </summary>
        Task<ShapefileUploadResult> UploadAsync(IReadOnlyList<ShapefileUploadFile> files);

        /// <summary>
        /// Quita la capa activa (ClearShapefileLayer) y elimina la persistencia
        /// asociada al lote actual.
        /// </summary>
        Task<bool> RemoveAsync();
    }
}
