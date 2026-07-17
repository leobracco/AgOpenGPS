// ============================================================================
// FormGps.GuidanceHost.cs
// Implementación de IGuidanceHost sobre FormGPS. CGuidance vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (hereda de
// IABLineHost — inversión de dependencia, traspaso de portabilidad
// 2026-07-17).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : IGuidanceHost
    {
        CABLine IGuidanceHost.ABLine => ABLine;
        CABCurve IGuidanceHost.Curve => curve;
    }
}
