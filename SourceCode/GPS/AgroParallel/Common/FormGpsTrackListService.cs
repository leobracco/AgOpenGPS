// ============================================================================
// FormGpsTrackListService.cs
// Adaptador ITrackListService → PilotX. Lee FormGPS.trk (CTrack.gArr) para que
// el panel web "Elegir guía" pueda listar las guías del lote por nombre en vez
// de solo ciclar a ciegas (track_prev/track_next).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsTrackListService : ITrackListService
    {
        private readonly FormGPS _form;

        public FormGpsTrackListService(FormGPS form) { _form = form; }

        public List<TrackItemDto> GetTracks()
        {
            var list = new List<TrackItemDto>();
            try
            {
                var trk = _form?.trk;
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
                var trk = _form?.trk;
                if (trk?.gArr == null || index < 0 || index >= trk.gArr.Count) return false;
                InvokeOnUi(() => { trk.idx = index; });
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

        private void InvokeOnUi(Action act)
        {
            if (act == null || _form == null) return;
            try
            {
                if (_form.IsHandleCreated && _form.InvokeRequired)
                    _form.BeginInvoke((MethodInvoker)(() => act()));
                else
                    act();
            }
            catch { /* defensivo */ }
        }
    }
}
