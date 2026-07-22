// ============================================================================
// EngineToolGeometryCalculator.cs — adaptador IToolGeometryCalculator sobre
// GuidanceEngineHost. Gemelo headless de FormGpsToolGeometryCalculator: barra
// del implemento (N secciones) con leftPoint/rightPoint en coords mundo +
// estado vivo de cada sección para colorearlas en el render GL de PilotX.Desktop.
// Cambios vs FormGPS: tool -> Tool, section -> Sections, isJobStarted -> IsJobStarted.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineToolGeometryCalculator : IToolGeometryCalculator
    {
        private readonly GuidanceEngineHost _host;

        public EngineToolGeometryCalculator(GuidanceEngineHost host) { _host = host; }

        public ToolGeometrySnapshot GetGeometry()
        {
            var snap = new ToolGeometrySnapshot
            {
                NumSections = 0,
                IsValid = false,
                Sections = new List<ToolSectionGeometry>()
            };
            if (_host == null) return snap;

            try
            {
                if (_host.Tool == null || _host.Sections == null) return snap;
                if (!_host.IsJobStarted) return snap;

                int n = _host.Tool.numOfSections;
                if (n <= 0 || n > _host.Sections.Length) return snap;

                snap.NumSections = n;
                snap.IsValid = true;

                for (int i = 0; i < n; i++)
                {
                    var s = _host.Sections[i];
                    if (s == null) continue;

                    int btn;
                    switch (s.sectionBtnState)
                    {
                        case btnStates.Off:  btn = 0; break;
                        case btnStates.Auto: btn = 1; break;
                        case btnStates.On:   btn = 2; break;
                        default:             btn = 0; break;
                    }

                    snap.Sections.Add(new ToolSectionGeometry
                    {
                        Index     = i,
                        LeftE     = s.leftPoint.easting,
                        LeftN     = s.leftPoint.northing,
                        RightE    = s.rightPoint.easting,
                        RightN    = s.rightPoint.northing,
                        IsOn      = s.isSectionOn,
                        IsMapping = s.isMappingOn,
                        BtnState  = btn
                    });
                }
            }
            catch (Exception)
            {
                snap.IsValid = false;
                snap.Sections.Clear();
                snap.NumSections = 0;
            }

            return snap;
        }
    }
}
