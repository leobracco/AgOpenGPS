// ============================================================================
// FormGpsSectionControlService.cs
// Adaptador ISectionControlService → PilotX. Hoy solo lee el estado calculado
// por PilotX (mf.section[i].sectionOnRequest + master switches). La lógica de
// DECISIÓN sigue viviendo en Forms/Position*.cs. Cuando la migremos al Core,
// esta clase pasará a delegar al SectionLogicService del Core.
// ============================================================================

using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsSectionControlService : ISectionControlService
    {
        private readonly FormGPS _form;

        public FormGpsSectionControlService(FormGPS form) { _form = form; }

        public SectionControlSnapshot GetSnapshot()
        {
            var snap = new SectionControlSnapshot
            {
                NumSections = 0,
                OnRequest = new bool[0],
                IsAuto = false,
                IsManualOn = false
            };
            if (_form == null) return snap;

            int n = _form.tool != null ? _form.tool.numOfSections : 0;
            snap.NumSections = n;
            if (n > 0 && _form.section != null)
            {
                var arr = new bool[n];
                for (int i = 0; i < n && i < _form.section.Length; i++)
                {
                    var sec = _form.section[i];
                    arr[i] = sec != null && sec.sectionOnRequest;
                }
                snap.OnRequest = arr;
            }
            // El master de secciones en PilotX vive en mf.autoBtnState / mc — distintos builds
            // exponen flags diferentes. Para la fase scaffold dejamos los flags en false;
            // cuando movamos la lógica al Core, el SectionLogicService los pobla.
            return snap;
        }
    }
}
