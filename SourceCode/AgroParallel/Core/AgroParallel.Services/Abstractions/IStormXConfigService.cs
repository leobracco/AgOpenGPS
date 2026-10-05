// IStormXConfigService — CRUD de stormX.json (config de la estación
// meteorológica móvil: umbrales operativos + nodos).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IStormXConfigService
    {
        StormXConfigDto Load();
        void Save(StormXConfigDto dto);
    }
}
