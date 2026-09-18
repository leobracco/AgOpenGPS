// ILineXConfigService — CRUD de lineX.json (config de nodos LineX:
// tipo de placa servo/embrague, surcos, ángulos/pulsos de servo, mapeo de
// surcos a secciones de PilotX).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ILineXConfigService
    {
        LineXConfigDto Load();
        void Save(LineXConfigDto dto);
    }
}
