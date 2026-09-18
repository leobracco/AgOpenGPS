// ============================================================================
// EngineNudgeService.cs — "Mover guia" para el motor headless.
//
// NudgeController se registra solo `if (_nudge != null)` y EngineWebHost nunca
// inyectaba el servicio: /api/nudge daba 404. Quinto de
// docs/RETIRAR-WINFORMS.md y el que mas iconos destraba (13, de los que solo
// andaban 3 por comandos sueltos).
//
// Correr la guia activa de a pasos (esquivar, re-centrar sobre el pivote) o
// mover la linea de REFERENCIA, que corre el patron entero del lote.
//
// La logica es NudgeEditor (AgOpenGPS.Core), la misma que usa FormGPS. Los
// factores de unidades quedan en su default metrico: el motor trabaja en
// metros y la conversion a pulgadas es de la pantalla.
// ============================================================================

using System;
using AgLibrary.Logging;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineNudgeService : INudgeService
    {
        private readonly GuidanceEngineHost _host;
        private NudgeEditor _ed;

        public EngineNudgeService(GuidanceEngineHost host) { _host = host; }

        private NudgeEditor Ed => _ed ??= new NudgeEditor(
            _host.Trk, _host.ABLineField, _host.CurveField, _host.Tool,
            hayLote: () => _host.IsJobStarted,
            guardarGuias: _host.SaveTracks,
            snapGet: () => AgOpenGPS.Properties.Settings.Default.setAS_snapDistance,
            snapSet: v =>
            {
                AgOpenGPS.Properties.Settings.Default.setAS_snapDistance = v;
                AgOpenGPS.Properties.Settings.Default.Save();
            },
            snapRefGet: () => AgOpenGPS.Properties.Settings.Default.setAS_snapDistanceRef,
            snapRefSet: v =>
            {
                AgOpenGPS.Properties.Settings.Default.setAS_snapDistanceRef = v;
                AgOpenGPS.Properties.Settings.Default.Save();
            });

        private static NudgeStateDto Map(NudgeEditor.NudgeStateSnapshot s)
        {
            if (s == null) return new NudgeStateDto { Ok = false, Error = "no-state" };
            return new NudgeStateDto
            {
                Ok = true,
                HasTrack = s.HasTrack,
                OffsetDisplay = s.OffsetDisplay,
                StepDisplay = s.StepDisplay,
                StepRefDisplay = s.StepRefDisplay,
                RefActive = s.RefActive,
                RefMovedDisplay = s.RefMovedDisplay,
                Units = s.Units,
            };
        }

        // Nunca tira: la pantalla tiene que poder mostrar el error.
        private NudgeStateDto Seguro(Func<NudgeEditor.NudgeStateSnapshot> accion)
        {
            try { return Map(accion()); }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: nudge: " + ex.Message);
                return new NudgeStateDto { Ok = false, Error = "error-interno" };
            }
        }

        public NudgeStateDto GetState() => Seguro(() => Ed.Nudge_Snapshot());
        public NudgeStateDto Move(int dir) => Seguro(() => Ed.Nudge_Move(dir));
        public NudgeStateDto Half(int dir) => Seguro(() => Ed.Nudge_Half(dir));
        public NudgeStateDto Zero() => Seguro(() => Ed.Nudge_Zero());
        public NudgeStateDto ToPivot() => Seguro(() => Ed.Nudge_ToPivot());
        public NudgeStateDto SetStep(double valueDisplay) => Seguro(() => Ed.Nudge_SetStep(valueDisplay));
        public NudgeStateDto Close() => Seguro(() => Ed.Nudge_Close());
        public NudgeStateDto RefOpen() => Seguro(() => Ed.NudgeRef_Open());
        public NudgeStateDto RefMove(int dir) => Seguro(() => Ed.NudgeRef_Move(dir));
        public NudgeStateDto RefHalf(int dir) => Seguro(() => Ed.NudgeRef_Half(dir));
        public NudgeStateDto RefSetStep(double valueDisplay) => Seguro(() => Ed.NudgeRef_SetStep(valueDisplay));
        public NudgeStateDto RefSave() => Seguro(() => Ed.NudgeRef_Save());
        public NudgeStateDto RefCancel() => Seguro(() => Ed.NudgeRef_Cancel());
    }
}
