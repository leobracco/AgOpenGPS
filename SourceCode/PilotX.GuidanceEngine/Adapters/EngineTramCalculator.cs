// ============================================================================
// EngineTramCalculator.cs — adaptador ITramCalculator sobre GuidanceEngineHost.
// Gemelo headless de FormGpsTramCalculator: tramlines internas + outer/inner
// boundary tracks + displayMode. Cambio vs FormGPS: tram -> Tram.
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineTramCalculator : ITramCalculator
    {
        private readonly GuidanceEngineHost _host;

        private string _lastMode = "None";
        private int _lastLines = 0;
        private int _lastOuter = 0;
        private int _lastInner = 0;
        private long _revision = 0;

        public EngineTramCalculator(GuidanceEngineHost host) { _host = host; }

        public TramGeometrySnapshot GetGeometry()
        {
            var snap = new TramGeometrySnapshot
            {
                DisplayMode = "None",
                Lines = new List<TramLine>(),
                OuterBoundary = new List<FieldPoint>(),
                InnerBoundary = new List<FieldPoint>(),
                Revision = _revision
            };
            if (_host == null) return snap;

            try
            {
                if (_host.Tram == null) return snap;

                switch (_host.Tram.displayMode)
                {
                    case TramMode.None: snap.DisplayMode = "None"; break;
                    case TramMode.All: snap.DisplayMode = "All"; break;
                    case TramMode.FillTracks: snap.DisplayMode = "FillTracks"; break;
                    case TramMode.BoundaryTracks: snap.DisplayMode = "BoundaryTracks"; break;
                    default: snap.DisplayMode = "None"; break;
                }

                var src = _host.Tram.tramList;
                if (src != null)
                {
                    for (int i = 0; i < src.Count; i++)
                    {
                        var lst = src[i];
                        if (lst == null || lst.Count < 2) continue;
                        var line = new TramLine { Points = new List<FieldPoint>(lst.Count) };
                        for (int h = 0; h < lst.Count; h++)
                            line.Points.Add(new FieldPoint(lst[h].easting, lst[h].northing));
                        snap.Lines.Add(line);
                    }
                }

                var outer = _host.Tram.tramBndOuterArr;
                if (outer != null)
                {
                    for (int i = 0; i < outer.Count; i++)
                        snap.OuterBoundary.Add(new FieldPoint(outer[i].easting, outer[i].northing));
                }

                var inner = _host.Tram.tramBndInnerArr;
                if (inner != null)
                {
                    for (int i = 0; i < inner.Count; i++)
                        snap.InnerBoundary.Add(new FieldPoint(inner[i].easting, inner[i].northing));
                }

                if (snap.DisplayMode != _lastMode
                    || snap.Lines.Count != _lastLines
                    || snap.OuterBoundary.Count != _lastOuter
                    || snap.InnerBoundary.Count != _lastInner)
                {
                    _revision++;
                    _lastMode = snap.DisplayMode;
                    _lastLines = snap.Lines.Count;
                    _lastOuter = snap.OuterBoundary.Count;
                    _lastInner = snap.InnerBoundary.Count;
                }
                snap.Revision = _revision;
            }
            catch (Exception)
            {
                snap.DisplayMode = "None";
                snap.Lines.Clear();
                snap.OuterBoundary.Clear();
                snap.InnerBoundary.Clear();
                snap.Revision = _revision;
            }

            return snap;
        }
    }
}
