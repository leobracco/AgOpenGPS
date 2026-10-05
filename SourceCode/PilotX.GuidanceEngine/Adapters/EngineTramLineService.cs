// ============================================================================
// EngineTramLineService.cs — constructor de tramlines para el motor headless.
//
// TramLineController se registra solo `if (_tramLine != null)` y EngineWebHost
// nunca inyectaba el servicio: /api/tramlines daba 404 y la pantalla de
// tramlines no podia hacer nada contra PilotX.Desktop. Segundo de
// docs/RETIRAR-WINFORMS.md.
//
// Las tramlines son las huellas por donde pasa el pulverizador despues: se
// generan de una guia mas el ancho de labor y quedan marcadas para no pisar el
// cultivo.
//
// La geometria NO se reimplementa (TramLineEditor, en AgOpenGPS.Core, es la
// misma que usa FormGPS) y el mapeo a DTO tampoco: TramLineMapper se linkea en
// los dos proyectos.
// ============================================================================

using System;
using AgLibrary.Logging;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineTramLineService : ITramLineService
    {
        private readonly GuidanceEngineHost _host;
        private TramLineEditor _ed;

        public EngineTramLineService(GuidanceEngineHost host) { _host = host; }

        // El motor trabaja siempre en METROS. recalcularExtension/refrescarUi/
        // refrescarModo quedan en su no-op: son bounding box de OpenGL e
        // imagenes de botones de WinForms.
        //
        // maxDiagonal: las tramlines se extienden mas alla del borde y despues
        // se recortan contra el contorno. Sin una diagonal razonable saldrian
        // cortas y no llegarian al lindero.
        private TramLineEditor Ed => _ed ??= new TramLineEditor(
            _host.Tram, _host.Bnd, _host.Tool, _host.CurveField, _host.Vehicle, _host.Trk,
            guardarTram: _host.GuardarTram,
            unidades: () => "m",
            m2Display: () => 1.0,
            alphaGet: () => AgOpenGPS.Properties.Settings.Default.setTram_alpha,
            alphaSet: a =>
            {
                AgOpenGPS.Properties.Settings.Default.setTram_alpha = a;
                AgOpenGPS.Properties.Settings.Default.Save();
            },
            maxDiagonal: () => _host.MaxDistanciaLote);

        // Nunca tira: la pantalla tiene que poder mostrar el error.
        private TramLineStateDto Seguro(Func<TramLineEditor.TramSnapshot> accion)
        {
            try { return TramLineMapper.Map(accion()); }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: tramlines: " + ex.Message);
                return new TramLineStateDto { Ok = false, Error = "error-interno" };
            }
        }

        public TramLineStateDto GetState() => Seguro(() => Ed.Tram_Snapshot());

        public TramLineStateDto Open() => Seguro(() => Ed.Tram_Snapshot(Ed.Tram_Open()));

        public TramLineStateDto CycleTrack(int dir)
            => Seguro(() => { Ed.Tram_CycleTrack(dir); return Ed.Tram_Snapshot(); });

        public TramLineStateDto SwapSide()
            => Seguro(() => { Ed.Tram_SwapSide(); return Ed.Tram_Snapshot(); });

        public TramLineStateDto SetPasses(int passes)
            => Seguro(() => { Ed.Tram_SetPasses(passes); return Ed.Tram_Snapshot(); });

        public TramLineStateDto SetStartPass(int startPass)
            => Seguro(() => { Ed.Tram_SetStartPass(startPass); return Ed.Tram_Snapshot(); });

        public TramLineStateDto SetOuter(bool on)
            => Seguro(() => { Ed.Tram_SetOuter(on); return Ed.Tram_Snapshot(); });

        public TramLineStateDto SetAlpha(double alpha)
            => Seguro(() => { Ed.Tram_SetAlpha(alpha); return Ed.Tram_Snapshot(); });

        public TramLineStateDto AddLines()
            => Seguro(() => { Ed.Tram_AddLines(); return Ed.Tram_Snapshot(); });

        public TramLineStateDto DeleteAll()
            => Seguro(() => { Ed.Tram_DeleteAll(); return Ed.Tram_Snapshot(); });

        public TramLineStateDto Tap(double easting, double northing)
            => Seguro(() => { Ed.Tram_Tap(easting, northing); return Ed.Tram_Snapshot(); });

        public TramLineStateDto CancelTouch()
            => Seguro(() => { Ed.Tram_CancelTouch(); return Ed.Tram_Snapshot(); });

        public void CloseSession()
        {
            try { Ed.Tram_CloseSession(); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: tramlines cerrar: " + ex.Message); }
        }

        public void CancelSession()
        {
            try { Ed.Tram_CancelSession(); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: tramlines cancelar: " + ex.Message); }
        }
    }
}
