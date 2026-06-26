// ============================================================================
// FormGpsToolGeometryCalculator.cs
// Adaptador IToolGeometryCalculator → PilotX. Stage 4a de la migracion OpenGL
// del mapa: el render GL necesita la barra del implemento (N secciones) con
// sus puntos leftPoint/rightPoint en coords mundo y el estado vivo de cada
// una para colorearlas.
//
// Lectura plana de FormGPS:
//   · mf.tool.numOfSections                 → cuantas secciones tiene la barra
//   · mf.section[i].leftPoint / rightPoint  → vec2 en coords mundo (E/N)
//   · mf.section[i].isSectionOn             → aplica producto en este instante
//   · mf.section[i].isMappingOn             → esta pintando coverage
//   · mf.section[i].sectionBtnState         → btnStates Off/Auto/On (manual)
//
// Cuando movamos el modelo del implemento al Core, se reemplaza por
// PilotXToolGeometryCalculator sin tocar el view.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsToolGeometryCalculator : IToolGeometryCalculator
    {
        private readonly FormGPS _form;

        public FormGpsToolGeometryCalculator(FormGPS form) { _form = form; }

        public ToolGeometrySnapshot GetGeometry()
        {
            // Snapshot defensivo por defecto — sin tool inicializado el
            // cliente debe esconder la capa (IsValid = false).
            var snap = new ToolGeometrySnapshot
            {
                NumSections = 0,
                IsValid = false,
                Sections = new List<ToolSectionGeometry>()
            };
            if (_form == null) return snap;

            try
            {
                if (_form.tool == null || _form.section == null) return snap;
                if (!_form.isJobStarted) return snap;

                int n = _form.tool.numOfSections;
                if (n <= 0 || n > _form.section.Length) return snap;

                snap.NumSections = n;
                snap.IsValid = true;

                for (int i = 0; i < n; i++)
                {
                    var s = _form.section[i];
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
                // Estado parcial (job-end en otro thread, tool en reload, etc) —
                // devolvemos invalid en vez de tirar.
                snap.IsValid = false;
                snap.Sections.Clear();
                snap.NumSections = 0;
            }

            return snap;
        }
    }
}
