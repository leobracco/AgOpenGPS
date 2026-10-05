// ============================================================================
// EngineQuickAbService.cs — "AB rapido" para el motor headless.
//
// QuickAbController se registra solo `if (_quickAb != null)` y EngineWebHost
// nunca inyectaba el servicio: /api/quickab daba 404. Tercero de
// docs/RETIRAR-WINFORMS.md.
//
// Es como se crea una guia manejando, sin pasar por la pantalla de guias: modo
// Curva (grabar puntos), AB (marcar A y B) o A+ (marcar A y fijar rumbo).
//
// La logica NO se reimplementa: QuickAbEditor (AgOpenGPS.Core) es la misma que
// usa FormGPS. El mapeo es corto y plano, asi que va aca en vez de linkearse
// como el de tramlines.
//
// Las tres acciones que en FormGPS eran `btnXxx.PerformClick()` se resuelven
// con los comandos del motor, que es lo que esos botones terminaban llamando.
// ============================================================================

using System;
using AgLibrary.Logging;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineQuickAbService : IQuickAbService
    {
        private readonly GuidanceEngineHost _host;
        private QuickAbEditor _ed;

        public EngineQuickAbService(GuidanceEngineHost host) { _host = host; }

        private QuickAbEditor Ed => _ed ??= new QuickAbEditor(
            _host.ABLineField, _host.CurveField, _host.Trk, _host.Tool, _host.Yt, _host.Ct,
            pivote: () => _host.pivotAxlePos,
            hayLote: () => _host.IsJobStarted,
            pilotoPrendido: () => _host.isBtnAutoSteerOn,
            guardarGuias: _host.SaveTracks,
            alternarContorno: () => _host.ExecuteCommand("contour"),
            alternarPiloto: () => _host.ExecuteCommand("autosteer"),
            alternarGiro: () => _host.ExecuteCommand("uturn"));

        private static QuickAbStateDto Map(QuickAbEditor.QuickAbSnapshot s)
        {
            if (s == null) return new QuickAbStateDto { Ok = false, Error = "no-state" };
            return new QuickAbStateDto
            {
                Ok = true,
                HasField = s.HasField,
                Mode = s.Mode,
                Phase = s.Phase,
                RefRight = s.RefRight,
                AMarked = s.AMarked,
                BMarked = s.BMarked,
                Recording = s.Recording,
                Points = s.Points,
                HeadingDeg = s.HeadingDeg,
                SuggestedName = s.SuggestedName,
                GuidanceStopped = s.GuidanceStopped,
                Error = s.Error,
            };
        }

        // Nunca tira: la pantalla tiene que poder mostrar el error.
        private QuickAbStateDto Seguro(Func<QuickAbEditor.QuickAbSnapshot> accion)
        {
            try { return Map(accion()); }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: ab-rapido: " + ex.Message);
                return new QuickAbStateDto { Ok = false, Error = "error-interno" };
            }
        }

        // GetState hace Tick a proposito: mientras se maneja, el punto B sigue
        // al tractor. En el form nativo eso lo hacia un timer de 500 ms; aca lo
        // mueve el poll de la pantalla, que va al mismo ritmo.
        public QuickAbStateDto GetState() => Seguro(() => Ed.QuickAb_Tick());

        public QuickAbStateDto Start(string mode) => Seguro(() => Ed.QuickAb_Start(mode));
        public QuickAbStateDto ToggleSide() => Seguro(() => Ed.QuickAb_ToggleSide());
        public QuickAbStateDto MarkA() => Seguro(() => Ed.QuickAb_MarkA());
        public QuickAbStateDto MarkB() => Seguro(() => Ed.QuickAb_MarkB());
        public QuickAbStateDto PauseToggle() => Seguro(() => Ed.QuickAb_PauseToggle());
        public QuickAbStateDto SetHeading(double degrees) => Seguro(() => Ed.QuickAb_SetHeading(degrees));
        public QuickAbStateDto Commit() => Seguro(() => Ed.QuickAb_Commit());
        public QuickAbStateDto Save(string name) => Seguro(() => Ed.QuickAb_Save(name));
        public QuickAbStateDto Cancel() => Seguro(() => Ed.QuickAb_Cancel());
    }
}
