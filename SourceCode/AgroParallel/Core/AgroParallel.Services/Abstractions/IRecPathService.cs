// ============================================================================
// IRecPathService.cs — contrato del picker/guardado de recorded paths HTML.
// Reemplazo de FormRecordName + FormRecordPicker.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Services.Abstractions
{
    public interface IRecPathService
    {
        // Lista de archivos .rec del lote activo (sin extensión).
        List<string> ListPaths();

        // Cargar un path .rec por nombre (copia a RecPath.txt + carga en recList).
        bool LoadPath(string name);

        // Borrar un archivo .rec del lote activo.
        bool DeletePath(string name);

        // Apagar recorded path (StopDriving + clear + save + hide panel).
        void TurnOff();

        // Guardar el recList actual con nombre (agrega fecha/hora si se pide).
        // Llamado cuando el usuario deja de grabar. Guarda RecPath.txt + <name>.rec.
        bool SaveWithName(string name);

        // Descartar la grabación actual (limpiar recList sin guardar).
        void DiscardRecording();
    }
}
