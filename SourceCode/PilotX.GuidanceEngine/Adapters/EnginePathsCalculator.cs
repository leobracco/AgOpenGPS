// ============================================================================
// EnginePathsCalculator.cs — adaptador IPathsGeometryCalculator sobre
// GuidanceEngineHost. Gemelo headless de FormGpsPathsCalculator: YouTurn
// (giro de cabecera) + Recorded (camino grabado). Cambios vs FormGPS:
// yt -> Yt, recPath -> RecPath.
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EnginePathsCalculator : IPathsGeometryCalculator
    {
        private readonly GuidanceEngineHost _host;

        private int _lastYouTurn = 0;
        private int _lastRecorded = 0;
        private long _revision = 0;

        public EnginePathsCalculator(GuidanceEngineHost host) { _host = host; }

        public PathsGeometrySnapshot GetGeometry()
        {
            var snap = new PathsGeometrySnapshot
            {
                YouTurn = new List<FieldPoint>(),
                Recorded = new List<FieldPoint>(),
                Revision = _revision
            };
            if (_host == null) return snap;

            try
            {
                var yt = _host.Yt;
                if (yt != null)
                {
                    var ytList = yt.ytList;
                    if (ytList != null && ytList.Count >= 2)
                    {
                        for (int i = 0; i < ytList.Count; i++)
                            snap.YouTurn.Add(new FieldPoint(ytList[i].easting, ytList[i].northing));
                    }
                }

                var rec = _host.RecPath;
                if (rec != null)
                {
                    var recList = rec.recList;
                    if (recList != null && recList.Count >= 2)
                    {
                        for (int i = 0; i < recList.Count; i++)
                            snap.Recorded.Add(new FieldPoint(recList[i].easting, recList[i].northing));
                    }
                }

                if (snap.YouTurn.Count != _lastYouTurn
                    || snap.Recorded.Count != _lastRecorded)
                {
                    _revision++;
                    _lastYouTurn = snap.YouTurn.Count;
                    _lastRecorded = snap.Recorded.Count;
                }
                snap.Revision = _revision;
            }
            catch (Exception)
            {
                snap.YouTurn.Clear();
                snap.Recorded.Clear();
                snap.Revision = _revision;
            }

            return snap;
        }
    }
}
