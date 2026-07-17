// ============================================================================
// FormGps.ContourHost.cs
// Implementación de IContourHost sobre FormGPS. CContour vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (hereda de
// IABLineHost — inversión de dependencia, traspaso de portabilidad
// 2026-07-17).
// ============================================================================

using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS : IContourHost
    {
        CABLine IContourHost.ABLine => ABLine;

        void IContourHost.SetContourLockImage(bool isOn) => SetContourLockImage(isOn);

        vec2 IContourHost.PnFix => pn.fix;

        bool IContourHost.IsPureDisplayOn => isPureDisplayOn;

        List<List<vec3>> IContourHost.ContourSaveList => contourSaveList;
    }
}
