// ============================================================================
// FormGps.PatchesHost.cs
// Implementación de IPatchesHost sobre FormGPS. CPatches vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (inversión de
// dependencia — traspaso de portabilidad 2026-07-17).
// ============================================================================

using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS : IPatchesHost
    {
        CFieldData IPatchesHost.Fd => fd;
        CSection[] IPatchesHost.Section => section;

        bool IPatchesHost.ToolIsMultiColoredSections => tool.isMultiColoredSections;
        bool IPatchesHost.ToolIsSectionsNotZones => tool.isSectionsNotZones;

        vec3 IPatchesHost.SectionColorDayVec
            => new vec3(sectionColorDay.R, sectionColorDay.G, sectionColorDay.B);

        vec3 IPatchesHost.SecColorVec(int j)
            => new vec3(tool.secColors[j].R, tool.secColors[j].G, tool.secColors[j].B);

        void IPatchesHost.IncrementPatchCounter() => patchCounter++;

        List<List<vec3>> IPatchesHost.PatchSaveList => patchSaveList;
    }
}
