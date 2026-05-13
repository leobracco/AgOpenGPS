// ISectionXConfigService — CRUD de sectionX.json (mapeo cables → secciones AOG).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ISectionXConfigService
    {
        SectionXConfigDto Load();
        void Save(SectionXConfigDto dto);
    }
}
