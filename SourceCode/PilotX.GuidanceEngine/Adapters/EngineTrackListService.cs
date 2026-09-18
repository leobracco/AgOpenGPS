// ============================================================================
// EngineTrackListService.cs — adaptador ITrackListService sobre
// GuidanceEngineHost. Gemelo headless de FormGpsTrackListService
// (GPS/AgroParallel/Common): mismo listado/selección de guías del lote activo,
// pero leyendo GuidanceEngineHost.Trk (CTrack) en vez de FormGPS.trk, y sin
// BeginInvoke (el host no tiene UI thread — se muta directo).
//
// Sirve GET /api/aog/tracks + POST /api/aog/tracks/select (TrackListController):
// sin este servicio esos endpoints daban 404 contra el engine, así que el mapa
// no podía listar ni conmutar guías (siempre mostraba la activa) ni auto-activar
// la línea más cercana al acercarse.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineTrackListService : ITrackListService
    {
        private readonly GuidanceEngineHost _host;

        public EngineTrackListService(GuidanceEngineHost host) { _host = host; }

        public List<TrackItemDto> GetTracks()
        {
            var list = new List<TrackItemDto>();
            try
            {
                var trk = _host?.Trk;
                if (trk?.gArr == null) return list;
                for (int i = 0; i < trk.gArr.Count; i++)
                {
                    var t = trk.gArr[i];
                    if (t == null) continue;
                    list.Add(new TrackItemDto
                    {
                        Index = i,
                        Name = string.IsNullOrEmpty(t.name) ? ("Guía " + (i + 1)) : t.name,
                        Mode = ModeToString(t.mode),
                        IsVisible = t.isVisible,
                        IsActive = i == trk.idx
                    });
                }
            }
            catch { /* defensivo: jamás romper el listado por estado parcial */ }
            return list;
        }

        public bool SelectTrack(int index)
        {
            try
            {
                var trk = _host?.Trk;
                if (trk?.gArr == null || index < 0 || index >= trk.gArr.Count) return false;
                trk.idx = index;

                // Invalidar la línea de guiado actual para forzar su reconstrucción
                // en el próximo tick desde el NUEVO track. Sin esto, con el autosteer
                // ON, BuildCurrentABLineList/BuildCurveCurrentList saltean el rebuild
                // (CABLine.cs:82,122) y el mapa sigue mostrando la línea vieja. Mismo
                // gesto que la rutina auto-track (CAutoSteerUpdater.cs:68-69).
                try { if (_host.ABLineField != null) _host.ABLineField.isABValid = false; } catch { }
                try { if (_host.CurveField != null) _host.CurveField.isCurveValid = false; } catch { }
                return true;
            }
            catch { return false; }
        }

        private static string ModeToString(TrackMode m)
        {
            switch (m)
            {
                case TrackMode.AB: return "ab";
                case TrackMode.Curve: return "curve";
                case TrackMode.bndTrackOuter: return "bnd_outer";
                case TrackMode.bndTrackInner: return "bnd_inner";
                case TrackMode.bndCurve: return "bnd_curve";
                case TrackMode.waterPivot: return "water_pivot";
                default: return "unknown";
            }
        }
    }
}
