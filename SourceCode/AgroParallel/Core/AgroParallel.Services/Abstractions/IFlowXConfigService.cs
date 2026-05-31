// IFlowXConfigService — CRUD de flowX.json (config de nodos FlowX:
// dosis, calibración del caudalímetro, mapeo de cables a secciones PilotX).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IFlowXConfigService
    {
        FlowXConfigDto Load();
        void Save(FlowXConfigDto dto);
    }
}
