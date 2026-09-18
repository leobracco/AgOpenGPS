// ============================================================================
// EngineCabeceraLineasService.cs — constructor de cabecera por lineas para el
// motor headless.
//
// CabeceraLineasController se registra solo `if (_cabeceraLineas != null)` y
// EngineWebHost nunca inyectaba el servicio: /api/cabecera-lineas daba 404 y la
// pantalla "Cabecera avanzada" no podia hacer nada contra PilotX.Desktop.
//
// Es el hermano complicado de la cabecera simple: en vez de correr el contorno
// hacia adentro una distancia fija, deja marcar lineas A/B sobre el borde y
// arma la cabecera con los cruces. Sirve para lotes que no son un rectangulo,
// que es la mayoria.
//
// La geometria NO se reimplementa: es la misma clase CabeceraLineasEditor que
// usa FormGPS (AgOpenGPS.Core). Aca solo se le da el contexto del motor y se
// mapea a los DTOs de cabecera-lineas.html.
// ============================================================================

using System;
using AgLibrary.Logging;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineCabeceraLineasService : ICabeceraLineasService
    {
        private readonly GuidanceEngineHost _host;
        private CabeceraLineasEditor _ed;

        public EngineCabeceraLineasService(GuidanceEngineHost host) { _host = host; }

        // El motor trabaja siempre en METROS: la conversion a pies es de la
        // pantalla, no del calculo. recalcularExtension/refrescarUi quedan en su
        // no-op: son bounding box de OpenGL y paneles de WinForms.
        private CabeceraLineasEditor Ed => _ed ??= new CabeceraLineasEditor(
            _host.Bnd, _host.Hdl, _host.Tool, _host.CurveField, _host.Vehicle,
            guardarLineas: _host.GuardarLineasDeCabecera,
            cargarLineas: _host.CargarLineasDeCabecera,
            guardarCabecera: _host.GuardarCabecera,
            hayLote: () => _host.IsJobStarted,
            ftOrMtoM: () => 1.0,
            m2Display: () => 1.0,
            unidades: () => "m",
            guardarSeccionControlada: on =>
            {
                AgOpenGPS.Properties.Settings.Default.setHeadland_isSectionControlled = on;
                AgOpenGPS.Properties.Settings.Default.Save();
            });

        // ---- mapeo ---------------------------------------------------------

        private static CabeceraLineasStateDto Map(CabeceraLineasEditor.CabLinSnapshot s)
        {
            if (s == null) return new CabeceraLineasStateDto { Ok = false, Error = "no-state" };

            var dto = new CabeceraLineasStateDto
            {
                Ok = true,
                JobStarted = s.JobStarted,
                HasBoundary = s.HasBoundary,
                Units = s.Units,
                ToolWidthDisplay = s.ToolWidthDisplay,
                BndSelect = s.BndSelect,
                SelIdx = s.SelIdx,
                APoint = s.APoint,
                BPoint = s.BPoint,
                IsSectionControlled = s.IsSectionControlled,
                Error = s.Error,
            };

            foreach (var f in s.Fences) dto.Fences.Add(f.ToArray());
            dto.HdLine = s.HdLine.ToArray();
            for (int i = 0; i < s.Tracks.Count; i++)
            {
                dto.Tracks.Add(new CabeceraLineasTrackDto
                {
                    Index = i,
                    Name = s.Tracks[i].Name,
                    Mode = s.Tracks[i].Mode,
                    Points = s.Tracks[i].Points.ToArray(),
                });
            }
            return dto;
        }

        // Nunca tira: la pantalla tiene que poder mostrar el error en vez de
        // quedarse colgada.
        private CabeceraLineasStateDto Seguro(Func<CabeceraLineasEditor.CabLinSnapshot> accion)
        {
            try { return Map(accion()); }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: cabecera-lineas: " + ex.Message);
                return new CabeceraLineasStateDto { Ok = false, Error = "error-interno" };
            }
        }

        // ---- API -----------------------------------------------------------

        public CabeceraLineasStateDto GetState() => Seguro(() => Ed.CabLin_Snapshot());

        public CabeceraLineasStateDto Open() => Seguro(() => Ed.CabLin_Open());

        public CabeceraLineasStateDto Tap(double easting, double northing, string mode, double distanceDisplay)
            => Seguro(() => Ed.CabLin_Tap(easting, northing, mode, distanceDisplay));

        public CabeceraLineasStateDto CancelTouch() => Seguro(() => Ed.CabLin_CancelTouch());

        public CabeceraLineasStateDto Cycle(int dir) => Seguro(() => Ed.CabLin_Cycle(dir));

        public CabeceraLineasStateDto DeleteTrack() => Seguro(() => Ed.CabLin_DeleteTrack());

        public CabeceraLineasStateDto Extend(string end, bool grow)
            => Seguro(() => Ed.CabLin_Extend(end == "a" ? "a" : "b", grow));

        public CabeceraLineasStateDto BuildHeadland() => Seguro(() => Ed.CabLin_BuildHeadland());

        public CabeceraLineasStateDto ResetHeadland() => Seguro(() => Ed.CabLin_ResetHeadland());

        public CabeceraLineasStateDto TurnOff() => Seguro(() => Ed.CabLin_TurnOff());

        public bool SetSectionControlled(bool on)
        {
            try
            {
                Ed.CabLin_SetSectionControlled(on);
                return _host.Bnd.isSectionControlledByHeadland;
            }
            catch { return false; }
        }

        public void CloseSession()
        {
            try { Ed.CabLin_CloseSession(); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: cabecera-lineas cerrar: " + ex.Message); }
        }
    }
}
