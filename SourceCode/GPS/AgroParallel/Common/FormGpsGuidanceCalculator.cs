// ============================================================================
// FormGpsGuidanceCalculator.cs
// Adaptador IGuidanceCalculator → PilotX. Envuelve los objetos vivos de FormGPS
// (ABLine, curve, trk, gyd) y traduce a GuidanceSnapshot. Por ahora el PilotX
// sigue siendo motor — esta clase solo lee el resultado de los cálculos:
//   · guidanceLineDistanceOff (short, mm)         → XteMeters
//   · guidanceLineSteerAngle (short, deg×100)     → SteerAngleCommandDeg
//   · ABLine.isABValid / curve.isCurveValid       → IsLineSet
//   · isBtnAutoSteerOn                            → IsAutoSteerOn
// Cuando movamos el cálculo al Core, se reemplaza por PilotXGuidanceCalculator
// sin tocar el view.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsGuidanceCalculator : IGuidanceCalculator
    {
        private readonly FormGPS _form;

        // Cache de revision para que el cliente HTTP pueda saltear re-upload del VBO
        // cuando la geometria no cambio. La revision se incrementa cuando cambia el
        // modo o la cantidad de puntos del set activo (criterio suficiente: una
        // curva nueva tiene cuenta distinta; redefinir AB regenera los endpoints).
        private string _lastMode = "Off";
        private int _lastCount = 0;
        private long _revision = 0;

        public FormGpsGuidanceCalculator(FormGPS form) { _form = form; }

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
            if (_form == null) return snap;

            try
            {
                snap.IsAutoSteerOn = _form.isBtnAutoSteerOn;

                // guidanceLineDistanceOff = 32000 → "sin guidance" (sentinel PilotX).
                short raw = _form.guidanceLineDistanceOff;
                if (raw != 32000)
                {
                    snap.XteMeters = raw / 1000.0; // mm → m
                }

                snap.SteerAngleCommandDeg = _form.guidanceLineSteerAngle / 100.0;

                bool ab = _form.ABLine != null && _form.ABLine.isABValid;
                bool cu = _form.curve != null && _form.curve.isCurveValid;
                bool ct = _form.ct != null && _form.ct.isContourBtnOn;

                if (ab) { snap.Mode = "AB"; snap.IsLineSet = true; }
                else if (cu) { snap.Mode = "Curve"; snap.IsLineSet = true; }
                else if (ct) { snap.Mode = "Contour"; snap.IsLineSet = true; }
                else { snap.Mode = "Off"; snap.IsLineSet = false; }

                // gyd expone parámetros de look-ahead/distancia al fin del track
                // según build de PilotX. Lo dejamos en stubs hasta que migremos al
                // Core; el view no los usa todavía.
            }
            catch
            {
                // defensivo: jamás romper el snapshot por estado parcial.
            }

            return snap;
        }

        public GuidanceGeometrySnapshot GetGeometry()
        {
            // Snapshot defensivo por defecto — el polleo 1Hz nunca debe tirar
            // ni dejar al cliente con un VBO huerfano.
            var geom = new GuidanceGeometrySnapshot
            {
                Mode = "Off",
                Points = new List<FieldPoint>(),
                Revision = _revision
            };
            if (_form == null) return geom;

            try
            {
                bool ab = _form.ABLine != null && _form.ABLine.isABValid;
                bool cu = _form.curve != null && _form.curve.isCurveValid;
                bool ct = _form.ct != null && _form.ct.isContourBtnOn;

                if (ab)
                {
                    // currentLinePtA/B ya vienen extendidos ±abLength por la propia
                    // CABLine — son los endpoints que FormGPS usa para su render
                    // OpenGL legacy. Los reutilizamos tal cual.
                    geom.Mode = "AB";
                    geom.Points.Add(new FieldPoint(_form.ABLine.currentLinePtA.easting, _form.ABLine.currentLinePtA.northing));
                    geom.Points.Add(new FieldPoint(_form.ABLine.currentLinePtB.easting, _form.ABLine.currentLinePtB.northing));
                }
                else if (cu)
                {
                    geom.Mode = "Curve";
                    var src = _form.curve.curList;
                    if (src != null)
                    {
                        for (int i = 0; i < src.Count; i++)
                            geom.Points.Add(new FieldPoint(src[i].easting, src[i].northing));
                    }
                }
                else if (ct)
                {
                    geom.Mode = "Contour";
                    var src = _form.ct.ctList;
                    if (src != null)
                    {
                        for (int i = 0; i < src.Count; i++)
                            geom.Points.Add(new FieldPoint(src[i].easting, src[i].northing));
                    }
                }
                // else: queda "Off" con lista vacia.

                // Bump de revision solo cuando cambia mode o cantidad de puntos.
                // No comparamos puntos uno por uno (caro y casi nunca cambian
                // sin que cambie la cuenta).
                if (geom.Mode != _lastMode || geom.Points.Count != _lastCount)
                {
                    _revision++;
                    _lastMode = geom.Mode;
                    _lastCount = geom.Points.Count;
                }
                geom.Revision = _revision;
            }
            catch
            {
                // Estado parcial (curList re-creada en otro thread, ABLine null
                // entre swaps de track, etc) — devolvemos Off seguro.
                geom.Mode = "Off";
                geom.Points = new List<FieldPoint>();
                geom.Revision = _revision;
            }

            return geom;
        }
    }
}
