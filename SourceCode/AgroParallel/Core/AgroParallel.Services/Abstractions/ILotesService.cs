// ILotesService — gestión de lotes (campos / Fields/&lt;Name&gt;) para la UI HTML.
// La implementación que toca PilotX vive del lado GPS (FormGpsLotesService) — esta
// interfaz no depende de WinForms.

using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ILotesService
    {
        /// <summary>Lista los lotes existentes bajo Fields/ ordenados por fecha desc.</summary>
        IList<FieldInfo> ListFields();

        /// <summary>Nombre del lote actualmente abierto en PilotX, o null si no hay.</summary>
        string GetCurrentFieldName();

        /// <summary>Path absoluto del directorio del lote actual, o null si no hay job abierto.</summary>
        string GetCurrentFieldDirectory();

        /// <summary>Cierra el lote actual si hay alguno y abre <paramref name="name"/>.</summary>
        Task<bool> OpenFieldAsync(string name);

        /// <summary>Cierra el lote actual, guardando todo (boundary/sections/contour/tracks).</summary>
        Task<bool> CloseFieldAsync();

        /// <summary>Crea un lote nuevo con <paramref name="name"/> y lo deja abierto.</summary>
        Task<bool> CreateFieldAsync(string name);
    }
}
