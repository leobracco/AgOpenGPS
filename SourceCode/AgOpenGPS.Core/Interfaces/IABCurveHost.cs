using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CABCurve necesita del host (FormGPS en WinForms). Hereda de
    /// IABLineHost (comparten casi todo) y agrega lo propio de la curva.
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// </summary>
    public interface IABCurveHost : IABLineHost
    {
        /// <summary>CABLine (ya en Core) — lineWidth/abLength/numGuideLines.</summary>
        CABLine ABLine { get; }

        vec3 PivotAxlePos { get; }
        vec3 SteerAxlePos { get; }

        /// <summary>btnAutoSteer.PerformClick() — apaga/prende el autosteer.</summary>
        void PerformAutoSteerClick();

        /// <summary>Aviso no bloqueante en pantalla (timeout ms, título, detalle).</summary>
        void TimedMessageBox(int timeout, string title, string message);

        /// <summary>gyd.StanleyGuidanceCurve (CGuidance todavía en GPS).</summary>
        void StanleyGuidanceCurve(vec3 pivot, vec3 steer, ref List<vec3> curList);
    }
}
