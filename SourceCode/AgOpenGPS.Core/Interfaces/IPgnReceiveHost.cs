using AgOpenGPS.Core;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que PgnReceiver (parser de PGNs entrantes desde CoreX)
    /// necesita del host (FormGPS en WinForms). Inversión de dependencia para
    /// el traspaso de portabilidad (bloque 9 matriz Android, 2026-07-19).
    /// El estado que mutan los PGNs (pn/ahrs/mc/trk/isobus) ya vive en Core
    /// y se expone directo; lo que toca UI o al form cruza como métodos.
    /// </summary>
    public interface IPgnReceiveHost
    {
        ApplicationModel AppModel { get; }
        CNMEA Pn { get; }
        CAHRS Ahrs { get; }
        CModuleComm Mc { get; }
        CTrack Trk { get; }
        CISOBUS Isobus { get; }

        /// <summary>timerSim.Enabled — simulador activo.</summary>
        bool IsSimEnabled { get; }

        /// <summary>Apaga el simulador al llegar GPS real.</summary>
        void DisableSim();

        /// <summary>Corre el pipeline por fix (Position.designer).</summary>
        void UpdateFixPosition();

        /// <summary>Mostrar mensajes de hardware (PGN 221) está habilitado.</summary>
        bool IsHardwareMessages { get; }

        /// <summary>Muestra el cartel de hardware (isAlert=true → fondo alerta).</summary>
        void ShowHardwareMessage(string text, bool isAlert, int secondsToDisplay);

        void HideHardwareMessage();

        /// <summary>btnCycleLines / btnCycleLinesBk (PGN 222).</summary>
        void CycleLineForward();

        void CycleLineBackward();

        /// <summary>Aplica mc.ss recién copiado (PGN 234, switches remotos).</summary>
        void DoRemoteSwitches();

        /// <summary>Llegó sentencia GPS válida — resetea sentenceCounter.</summary>
        void OnGpsSentenceReceived();

        /// <summary>Llegó tráfico del módulo de dirección — resetea su contador.</summary>
        void OnSteerModuleTraffic();
    }
}
