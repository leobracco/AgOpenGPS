// ILibraXConfigService — CRUD de libraX.json (nodos conocidos + parámetros de
// presentación del monitor de rendimiento).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ILibraXConfigService
    {
        LibraXConfigDto Load();
        void Save(LibraXConfigDto dto);
    }
}
