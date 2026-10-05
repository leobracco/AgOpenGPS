// ============================================================================
// EngineHeadlandEditService.cs — editor de cabecera para el motor headless.
//
// HeadlandController se registra solo `if (_headlandEdit != null)` y
// EngineWebHost nunca inyectaba el servicio: /api/headland daba 404 y la
// pantalla Cabecera no podia construir nada contra PilotX.Desktop. Cuarto caso
// del mismo hueco (perfiles, banderas, contorno, cabecera).
//
// La cabecera importa mas de lo que parece: sin ella no hay corte automatico de
// secciones al entrar al borde del lote, que es donde se solapa y se desperdicia
// semilla.
//
// La geometria NO se reimplementa: es la misma clase HeadlandEditor que usa
// FormGPS (AgOpenGPS.Core). Aca solo se le da el contexto del motor y se mapea
// a los DTOs de cabecera.html.
// ============================================================================

using System;
using AgLibrary.Logging;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineHeadlandEditService : IHeadlandEditService
    {
        private readonly GuidanceEngineHost _host;
        private HeadlandEditor _ed;

        public EngineHeadlandEditService(GuidanceEngineHost host) { _host = host; }

        // Lazy: Bnd/Hdl/Tool del host existen desde el arranque, pero el editor
        // guarda referencias y no hay razon para armarlo si nadie abre la
        // pantalla de cabecera.
        //
        // El motor trabaja siempre en METROS: la conversion a pies es cosa de la
        // pantalla, no del calculo.
        //
        // recalcularExtension y refrescarUi quedan en su no-op por defecto: son
        // el bounding box de OpenGL y los paneles de WinForms. El mapa de
        // PilotX.Desktop se redibuja solo con el proximo /api/aog/state.
        private HeadlandEditor Ed => _ed ??= new HeadlandEditor(
            _host.Bnd, _host.Hdl, _host.Tool,
            guardar: _host.GuardarCabecera,
            ftOrMtoM: () => 1.0,
            unidades: () => "m",
            guardarSeccionControlada: on =>
            {
                AgOpenGPS.Properties.Settings.Default.setHeadland_isSectionControlled = on;
                AgOpenGPS.Properties.Settings.Default.Save();
            });

        // ---- estado --------------------------------------------------------

        private HeadlandEditStateDto Estado(string error)
        {
            return new HeadlandEditStateDto
            {
                HasField = Ed.E_HasBoundary(),
                HasBoundary = Ed.E_HasBoundary(),
                IsHeadlandOn = Ed.E_IsHeadlandOn(),
                IsSectionControlled = Ed.E_IsSectionControlled(),
                Units = Ed.E_Units(),
                ToolWidthM = Ed.E_ToolWidthM(),
                Fence = Ed.E_FenceEN(),
                Headland = Ed.E_HeadlandEN(),
                Fences = Ed.E_FencesEN(),
                BndSelect = Ed.E_BndSelect(),
                Slice = Ed.E_SliceEN(),
                SliceMode = Ed.E_SliceMode(),
                APoint = Ed.E_APointEN(),
                BPoint = Ed.E_BPointEN(),
                CanUndo = Ed.E_CanUndo(),
                Error = error,
            };
        }

        // Nunca tira: la pantalla de cabecera tiene que poder mostrar el error
        // en vez de quedarse colgada.
        private HeadlandEditStateDto Seguro(Func<string> accion)
        {
            try { return Estado(accion()); }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: cabecera: " + ex.Message);
                return new HeadlandEditStateDto { Error = "error-interno" };
            }
        }

        public HeadlandEditStateDto GetState() => Seguro(() => null);

        // ---- construccion --------------------------------------------------

        public HeadlandEditResultDto BuildAround(double distanceDisplay)
        {
            try
            {
                bool ok = Ed.E_BuildAround(distanceDisplay);
                return new HeadlandEditResultDto
                {
                    Ok = ok,
                    IsHeadlandOn = Ed.E_IsHeadlandOn(),
                    Headland = Ed.E_HeadlandEN(),
                    // El offset "colapsa" cuando la distancia se come el lote:
                    // no quedan puntos validos. No se toca la cabecera vieja.
                    Error = ok ? null : "offset-collapsed",
                };
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: cabecera build: " + ex.Message);
                return new HeadlandEditResultDto { Ok = false, Error = "error-interno" };
            }
        }

        public HeadlandEditResultDto Reset()
        {
            try
            {
                Ed.E_Reset();
                return new HeadlandEditResultDto
                {
                    Ok = true,
                    IsHeadlandOn = Ed.E_IsHeadlandOn(),
                    Headland = Ed.E_HeadlandEN(),
                };
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: cabecera reset: " + ex.Message);
                return new HeadlandEditResultDto { Ok = false, Error = "error-interno" };
            }
        }

        public HeadlandEditResultDto TurnOff()
        {
            try
            {
                Ed.E_TurnOff();
                return new HeadlandEditResultDto
                {
                    Ok = true,
                    IsHeadlandOn = Ed.E_IsHeadlandOn(),
                    Headland = Ed.E_HeadlandEN(),
                };
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: cabecera off: " + ex.Message);
                return new HeadlandEditResultDto { Ok = false, Error = "error-interno" };
            }
        }

        public bool SetSectionControlled(bool on)
        {
            try { Ed.E_SetSectionControlled(on); return Ed.E_IsSectionControlled(); }
            catch { return false; }
        }

        // ---- reshape manual ------------------------------------------------

        public HeadlandEditStateDto Open() => Seguro(() => Ed.E_Open());

        public HeadlandEditStateDto Tap(double easting, double northing, string mode, double distanceDisplay)
            => Seguro(() => Ed.E_Tap(easting, northing, mode, distanceDisplay));

        public HeadlandEditStateDto CancelTouch() => Seguro(() => { Ed.E_CancelTouch(); return null; });

        public HeadlandEditStateDto Extend(string end, bool grow)
            => Seguro(() => { Ed.E_Extend(end == "a" ? "a" : "b", grow); return null; });

        public HeadlandEditStateDto Clip() => Seguro(() => Ed.E_Clip());

        public HeadlandEditStateDto Undo() => Seguro(() => { Ed.E_Undo(); return null; });

        public void CloseSession()
        {
            try { Ed.E_CloseSession(); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: cabecera cerrar: " + ex.Message); }
        }
    }
}
