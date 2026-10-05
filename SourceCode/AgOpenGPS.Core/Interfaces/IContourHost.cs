using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CContour necesita del host (FormGPS en WinForms). Hereda de
    /// IABLineHost (comparte vehicle/tool/ahrs/escalares de guiado) y agrega
    /// lo propio del contorno. Inversión de dependencia para el traspaso de
    /// portabilidad (2026-07-17).
    /// </summary>
    public interface IContourHost : IABLineHost
    {
        /// <summary>CABLine (ya en Core) — lineWidth para el dibujo.</summary>
        CABLine ABLine { get; }

        /// <summary>Icono de candado de contour en la UI (on/off).</summary>
        void SetContourLockImage(bool isOn);

        /// <summary>pn.fix — posición actual (CNMEA todavía en GPS).</summary>
        vec2 PnFix { get; }

        /// <summary>isPureDisplayOn — mostrar el círculo de pure pursuit.</summary>
        bool IsPureDisplayOn { get; }

        /// <summary>contourSaveList — strips de contorno pendientes de guardar.</summary>
        List<List<vec3>> ContourSaveList { get; }
    }
}
