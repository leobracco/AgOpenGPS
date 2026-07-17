// ============================================================================
// FormGps.ABCurveHost.cs
// Implementación de IABCurveHost sobre FormGPS. CABCurve vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (hereda de
// IABLineHost — inversión de dependencia, traspaso de portabilidad
// 2026-07-17).
// ============================================================================

using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS : IABCurveHost
    {
        CABLine IABCurveHost.ABLine => ABLine;

        vec3 IABCurveHost.PivotAxlePos => pivotAxlePos;
        vec3 IABCurveHost.SteerAxlePos => steerAxlePos;

        void IABCurveHost.PerformAutoSteerClick() => btnAutoSteer.PerformClick();

        void IABCurveHost.TimedMessageBox(int timeout, string title, string message)
            => TimedMessageBox(timeout, title, message);

        void IABCurveHost.StanleyGuidanceCurve(vec3 pivot, vec3 steer, ref List<vec3> curList)
            => gyd.StanleyGuidanceCurve(pivot, steer, ref curList);
    }
}
