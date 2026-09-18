// ============================================================================
// EngineTramSimpleService.cs — panel simple de tramlines para el motor.
//
// TramSimpleController se registra solo `if (_tramSimple != null)` y
// EngineWebHost nunca inyectaba el servicio: /api/tram-simple daba 404. Cuarto
// de docs/RETIRAR-WINFORMS.md.
//
// Version reducida del constructor de tramlines: genera las huellas desde la
// guia ACTIVA con pasadas configurables, sin la edicion por cortes. Es lo que
// abre el menu de configuracion de tram.
//
// La logica es TramSimpleEditor (AgOpenGPS.Core), la misma que usa FormGPS.
// ============================================================================

using System;
using AgLibrary.Logging;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineTramSimpleService : ITramSimpleService
    {
        private readonly GuidanceEngineHost _host;
        private TramSimpleEditor _ed;

        public EngineTramSimpleService(GuidanceEngineHost host) { _host = host; }

        // Metros siempre; cerrarVentanasFlotantes/refrescarModo/refrescarUi
        // quedan no-op (son ventanas y botones de WinForms).
        private TramSimpleEditor Ed => _ed ??= new TramSimpleEditor(
            _host.Trk, _host.Tram, _host.ABLineField, _host.Tool, _host.Bnd,
            _host.CurveField, _host.Vehicle,
            guardarTram: _host.GuardarTram,
            guardarGuias: _host.SaveTracks,
            unidades: () => "m",
            m2Display: () => 1.0,
            hayLote: () => _host.IsJobStarted,
            maxDiagonal: () => _host.MaxDistanciaLote,
            pasadasGet: () => AgOpenGPS.Properties.Settings.Default.setTram_passes,
            pasadasSet: p =>
            {
                AgOpenGPS.Properties.Settings.Default.setTram_passes = p;
                AgOpenGPS.Properties.Settings.Default.Save();
            },
            alphaGet: () => AgOpenGPS.Properties.Settings.Default.setTram_alpha,
            alphaSet: a =>
            {
                AgOpenGPS.Properties.Settings.Default.setTram_alpha = a;
                AgOpenGPS.Properties.Settings.Default.Save();
            });

        private static TramSimpleStateDto Map(TramSimpleEditor.TramSimpleStateSnapshot s)
        {
            if (s == null) return new TramSimpleStateDto { Ok = false, Error = "no-state" };
            return new TramSimpleStateDto
            {
                Ok = true,
                HasTrack = s.HasTrack,
                HasBoundary = s.HasBoundary,
                IsCurve = s.IsCurve,
                Passes = s.Passes,
                AlphaPercent = s.AlphaPercent,
                Mode = s.Mode,
                ToolWidthDisplay = s.ToolWidthDisplay,
                TramWidthDisplay = s.TramWidthDisplay,
                TrackWidthDisplay = s.TrackWidthDisplay,
                Units = s.Units,
            };
        }

        // Nunca tira: la pantalla tiene que poder mostrar el error.
        private TramSimpleStateDto Seguro(Func<TramSimpleEditor.TramSimpleStateSnapshot> accion)
        {
            try { return Map(accion()); }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: tram-simple: " + ex.Message);
                return new TramSimpleStateDto { Ok = false, Error = "error-interno" };
            }
        }

        public TramSimpleStateDto Open() => Seguro(() => Ed.TramSimple_Open());

        public TramSimpleStateDto GetState() => Seguro(() => Ed.TramSimple_Snapshot());

        public TramSimpleStateDto SetPasses(int passes) => Seguro(() => Ed.TramSimple_SetPasses(passes));

        public TramSimpleStateDto SetAlpha(int percent) => Seguro(() => Ed.TramSimple_SetAlpha(percent));

        public TramSimpleStateDto SetMode(string mode) => Seguro(() => Ed.TramSimple_SetMode(mode));

        public TramSimpleStateDto SwapAB() => Seguro(() => Ed.TramSimple_SwapAB());

        public bool Commit(bool save)
        {
            try { Ed.TramSimple_Commit(save); return true; }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: tram-simple commit: " + ex.Message);
                return false;
            }
        }
    }
}
