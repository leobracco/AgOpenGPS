// ============================================================================
// EngineSectionControlService.cs — adaptador ISectionControlService sobre
// GuidanceEngineHost. Gemelo headless de FormGpsSectionControlService
// (GPS/AgroParallel/Common, carril taller, solo lectura). La decisión de
// fondo (boundary/headland/anti-overlap/look-ahead) vive en CSectionCalculator
// (Core, bloque 9) — este service solo expone el resultado ya calculado
// (Sections[i].sectionOnRequest), igual que su gemelo FormGPS.
//
// Mejora chica sobre el adaptador FormGPS: ese dejaba IsAuto/IsManualOn en
// false a propósito ("fase scaffold", el master vive repartido en mf/mc en
// FormGPS). Acá GuidanceEngineHost ya expone autoBtnState/manualBtnState
// como campos directos (los usa Commands.cs para sec_auto/sec_manual), así
// que se pueden poblar de verdad sin scaffold.
// ============================================================================

using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineSectionControlService : ISectionControlService
    {
        private readonly GuidanceEngineHost _host;

        public EngineSectionControlService(GuidanceEngineHost host) { _host = host; }

        public SectionControlSnapshot GetSnapshot()
        {
            var snap = new SectionControlSnapshot
            {
                NumSections = 0,
                OnRequest = new bool[0],
                SectionStates = new int[0],
                ZoneRanges = new int[0],
                IsAuto = false,
                IsManualOn = false
            };
            if (_host == null) return snap;

            int n = _host.Tool != null ? _host.Tool.numOfSections : 0;
            snap.NumSections = n;
            if (n > 0 && _host.Sections != null)
            {
                var arr = new bool[n];
                var estados = new int[n];
                for (int i = 0; i < n && i < _host.Sections.Length; i++)
                {
                    var sec = _host.Sections[i];
                    arr[i] = sec != null && sec.sectionOnRequest;
                    // 0=Off, 1=Auto, 2=On. Sin esto la UI no puede distinguir
                    // "Auto" de "On" (los dos dan OnRequest=true al aplicar) y no
                    // puede pintar los 3 colores del nativo.
                    estados[i] = sec == null ? 0 : (int)sec.sectionBtnState;
                }
                snap.OnRequest = arr;
                snap.SectionStates = estados;
            }

            if (_host.Tool != null)
            {
                snap.IsSectionsNotZones = _host.Tool.isSectionsNotZones;
                if (!_host.Tool.isSectionsNotZones && _host.Tool.zoneRanges != null)
                {
                    // zoneRanges[0] no se usa; se expone 1..8 tal cual.
                    var z = new int[8];
                    for (int i = 1; i <= 8 && i < _host.Tool.zoneRanges.Length; i++) z[i - 1] = _host.Tool.zoneRanges[i];
                    snap.ZoneRanges = z;
                }
            }

            snap.IsAuto = _host.autoBtnState == btnStates.Auto;
            snap.IsManualOn = _host.manualBtnState == btnStates.On;
            return snap;
        }
    }
}
