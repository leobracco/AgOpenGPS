// ============================================================================
// FormGps.FieldDataHost.cs
// Implementación de IFieldDataHost sobre FormGPS. CFieldData vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (inversión de
// dependencia — traspaso de portabilidad 2026-07-17).
// ============================================================================

using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS : IFieldDataHost
    {
        double IFieldDataHost.AvgSpeed => avgSpeed;
        string IFieldDataHost.DisplayFieldName => displayFieldName;
        double IFieldDataHost.ToolWidth => tool.width;
        int IFieldDataHost.ToolNumOfSections => tool.numOfSections;
        double IFieldDataHost.ToolOverlap => tool.overlap;
        IReadOnlyList<CBoundaryList> IFieldDataHost.BoundaryList => bnd.bndList;
    }
}
