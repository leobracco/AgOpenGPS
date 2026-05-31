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

using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsGuidanceCalculator : IGuidanceCalculator
    {
        private readonly FormGPS _form;

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
    }
}
