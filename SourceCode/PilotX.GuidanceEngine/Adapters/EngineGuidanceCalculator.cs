// ============================================================================
// EngineGuidanceCalculator.cs — adaptador IGuidanceCalculator sobre
// GuidanceEngineHost. Gemelo headless de FormGpsGuidanceCalculator. Cambios
// vs FormGPS: ABLine -> ABLineField, curve -> CurveField, ct -> Ct, y
// ExecuteCommand delega en host.ExecuteCommand (Commands.cs, sin UI) en vez
// de FormGPS.ExecuteGuidanceCommand (que clickeaba el botón nativo).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineGuidanceCalculator : IGuidanceCalculator
    {
        private readonly GuidanceEngineHost _host;

        private string _lastKey = "";
        private long _revision = 0;

        public EngineGuidanceCalculator(GuidanceEngineHost host) { _host = host; }

        public GuidanceSnapshot GetSnapshot()
        {
            var snap = new GuidanceSnapshot
            {
                Mode = "Off",
                IsLineSet = false,
                IsAutoSteerOn = false,
                XteMeters = 0,
                HeadingErrorRad = 0,
                SteerAngleCommandDeg = 0,
                DistanceToEndM = -1,
                LookAhead = null
            };
            if (_host == null) return snap;

            try
            {
                snap.IsAutoSteerOn = _host.isBtnAutoSteerOn;
                snap.SteerAngleCommandDeg = _host.guidanceLineSteerAngle / 100.0;

                bool ab = _host.ABLineField != null && _host.ABLineField.isABValid;
                bool cu = _host.CurveField != null && _host.CurveField.isCurveValid;
                bool ct = _host.Ct != null && _host.Ct.isContourBtnOn;

                if (ab) { snap.Mode = "AB"; snap.IsLineSet = true; snap.HowManyPathsAway = _host.ABLineField.howManyPathsAway; }
                else if (cu) { snap.Mode = "Curve"; snap.IsLineSet = true; snap.HowManyPathsAway = _host.CurveField.howManyPathsAway; }
                else if (ct) { snap.Mode = "Contour"; snap.IsLineSet = true; }
                else { snap.Mode = "Off"; snap.IsLineSet = false; }

                // XTE: la distancia REAL a la línea actual (en m), que el
                // guiado recalcula en CADA fix contra la PARALELA más cercana
                // al pivote — con el piloto prendido o apagado. Antes salía de
                // guidanceLineDistanceOff (el campo del PGN), que es un valor
                // de PROTOCOLO pisado por sentinels de estado: 32000 "sin
                // línea" y 32020 "autosteer apagado". El 32000 se filtraba,
                // el 32020 NO → con el piloto apagado la pantalla mostraba
                // "32.0 m" clavado en vez de la distancia a la guía.
                if (ab) snap.XteMeters = _host.ABLineField.distanceFromCurrentLinePivot;
                else if (cu) snap.XteMeters = _host.CurveField.distanceFromCurrentLinePivot;
                else if (ct) snap.XteMeters = _host.Ct.distanceFromCurrentLinePivot;
            }
            catch
            {
                // defensivo: jamás romper el snapshot por estado parcial.
            }

            return snap;
        }

        public GuidanceGeometrySnapshot GetGeometry()
        {
            var geom = new GuidanceGeometrySnapshot
            {
                Mode = "Off",
                Points = new List<FieldPoint>(),
                Revision = _revision
            };
            if (_host == null) return geom;

            try
            {
                bool ab = _host.ABLineField != null && _host.ABLineField.isABValid;
                bool cu = _host.CurveField != null && _host.CurveField.isCurveValid;
                bool ct = _host.Ct != null && _host.Ct.isContourBtnOn;

                if (ab)
                {
                    geom.Mode = "AB";
                    geom.Points.Add(new FieldPoint(_host.ABLineField.currentLinePtA.easting, _host.ABLineField.currentLinePtA.northing));
                    geom.Points.Add(new FieldPoint(_host.ABLineField.currentLinePtB.easting, _host.ABLineField.currentLinePtB.northing));
                }
                else if (cu)
                {
                    geom.Mode = "Curve";
                    var src = _host.CurveField.curList;
                    if (src != null)
                    {
                        for (int i = 0; i < src.Count; i++)
                            geom.Points.Add(new FieldPoint(src[i].easting, src[i].northing));
                    }
                }
                else if (ct)
                {
                    geom.Mode = "Contour";
                    var src = _host.Ct.ctList;
                    if (src != null)
                    {
                        for (int i = 0; i < src.Count; i++)
                            geom.Points.Add(new FieldPoint(src[i].easting, src[i].northing));
                    }
                }

                // Firma de la geometría: cambia si cambian los PUNTOS, no solo el
                // modo/cantidad. Antes solo se comparaba mode+count → al conmutar
                // entre dos líneas AB (ambas mode="AB", count=2) la revisión no
                // subía y el mapa (cache por revisión) seguía mostrando la vieja.
                // Muestreo first/middle/last: identifica una línea AB completa y
                // distingue curvas distintas sin recorrer todos los puntos.
                int n = geom.Points.Count;
                string key;
                if (n == 0)
                {
                    key = geom.Mode + ":0";
                }
                else
                {
                    var p0 = geom.Points[0];
                    var pm = geom.Points[n / 2];
                    var pl = geom.Points[n - 1];
                    key = string.Format(CultureInfo.InvariantCulture,
                        "{0}:{1}:{2:F3},{3:F3}|{4:F3},{5:F3}|{6:F3},{7:F3}",
                        geom.Mode, n, p0.E, p0.N, pm.E, pm.N, pl.E, pl.N);
                }
                if (key != _lastKey)
                {
                    _revision++;
                    _lastKey = key;
                }
                geom.Revision = _revision;

                // ---- U-turn (réplica 6.8.5) --------------------------------
                // FUERA de la firma de revisión a propósito: el camino se
                // re-arma con el tractor andando y el cliente lo consume en
                // cada poll (como el XTE), no solo cuando cambia la guía.
                var yt = _host.Yt;
                if (yt != null && yt.ytList != null && yt.ytList.Count > 2)
                {
                    var dto = new YouTurnPathDto
                    {
                        Points = new List<FieldPoint>(yt.ytList.Count),
                        Phase = yt.youTurnPhase,
                        Triggered = yt.isYouTurnTriggered,
                        OutOfBounds = yt.isOutOfBounds,
                        TurnLeft = yt.isTurnLeft,
                        DistanceM = _host.distancePivotToTurnLine,
                    };
                    for (int i = 0; i < yt.ytList.Count; i++)
                        dto.Points.Add(new FieldPoint(yt.ytList[i].easting, yt.ytList[i].northing));
                    geom.YouTurn = dto;
                }
            }
            catch
            {
                geom.Mode = "Off";
                geom.Points = new List<FieldPoint>();
                geom.Revision = _revision;
            }

            return geom;
        }

        public bool ExecuteCommand(string command)
        {
            // El host tiene su propio ExecuteCommand headless (Commands.cs), con el
            // mismo vocabulario que FormGPS.ExecuteGuidanceCommand pero sin UI.
            try { return _host != null && _host.ExecuteCommand(command); }
            catch { return false; }
        }
    }
}
