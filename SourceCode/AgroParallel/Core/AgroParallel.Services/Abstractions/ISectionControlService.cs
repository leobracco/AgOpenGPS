// ISectionControlService — decisión "esta sección debe estar ON ahora".
// Hoy la decisión (boundary check + headland + anti-overlap + AB look-ahead)
// vive enterrada en Forms/Position*.cs de PilotX. El adaptador FormGpsSectionControlService
// expone el resultado. Cuando movamos la lógica al Core, esta interfaz se
// engruesa con un Decide(snapshot, coverage, boundaries, ...).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ISectionControlService
    {
        /// <summary>Estado actual: sectionOnRequest[] + modo del master.</summary>
        SectionControlSnapshot GetSnapshot();
    }
}
