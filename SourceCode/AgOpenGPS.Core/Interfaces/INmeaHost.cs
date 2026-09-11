using AgOpenGPS.Core;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CNMEA necesita del host (FormGPS en WinForms).
    /// AppModel/CSim/WorldGrid ya viven en Core y se exponen directo.
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// </summary>
    public interface INmeaHost
    {
        ApplicationModel AppModel { get; }

        /// <summary>avgSpeed — CNMEA la promedia (lee y escribe).</summary>
        double AvgSpeed { get; set; }

        /// <summary>timerSim.Enabled — si el simulador está corriendo.</summary>
        bool IsSimTimerEnabled { get; }

        /// <summary>sim — CSim ya vive en Core, se expone directo.</summary>
        CSim Sim { get; }

        /// <summary>worldGrid — Core.Models.WorldGrid, se expone directo.</summary>
        WorldGrid WorldGrid { get; }
    }
}
