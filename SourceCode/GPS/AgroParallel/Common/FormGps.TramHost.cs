// ============================================================================
// FormGps.TramHost.cs
// Implementación de ITramHost sobre FormGPS. CTram vive en AgOpenGPS.Core y
// consume el host a través de esta interfaz (inversión de dependencia —
// traspaso de portabilidad 2026-07-17).
// ============================================================================

using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS : ITramHost
    {
        double ITramHost.ToolWidth => tool.width;
        double ITramHost.CamSetDistance => camera.camSetDistance;
        IReadOnlyList<CBoundaryList> ITramHost.BoundaryList => bnd.bndList;
    }
}
