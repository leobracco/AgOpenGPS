using AgOpenGPS.Core;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CAutoSteerUpdater (armado del PGN de posición corregida +
    /// PGN 254 de autosteer, extraídos de UpdateFixPosition en
    /// Position.designer.cs) necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (bloque 9
    /// matriz Android, 2026-07-20). Cálculo/armado de PGN y decisión de
    /// tracking (AB/curva) es puro; los toques UI (click del botón, mensaje
    /// timed, timer del simulador) cruzan como método/propiedad.
    /// </summary>
    public interface IAutoSteerHost
    {
        ApplicationModel AppModel { get; }
        CNMEA Pn { get; }
        CFieldData Fd { get; }
        CContour Ct { get; }
        CTrack Trk { get; }
        CABLine ABLine { get; }
        CABCurve Curve { get; }
        CRecordedPath RecPath { get; }
        CVehicle Vehicle { get; }
        CAHRS Ahrs { get; }
        CModuleComm Mc { get; }
        CISOBUS Isobus { get; }
        CPGN_FE P254 { get; }

        vec3 PivotAxlePos { get; }
        vec3 SteerAxlePos { get; }

        double GpsHeading { get; }
        double AvgSpeed { get; }
        double LightbarDistance { get; set; }
        short GuidanceLineDistanceOff { get; set; }
        short GuidanceLineSteerAngle { get; set; }
        int CrossTrackError { get; set; }
        int MinSteerSpeedTimer { get; set; }

        bool IsBtnAutoSteerOn { get; }
        bool IsReverse { get; }
        bool IsChangingDirection { get; }
        bool IsSteerInReverse { get; }
        bool IsMetric { get; }
        bool IsSimTimerEnabled { get; }

        /// <summary>Envía un PGN al loop UDP (CoreX).</summary>
        void SendPgnToLoop(byte[] data);

        /// <summary>Simula un click del botón AutoSteer (lo apaga/prende con sus efectos de UI).</summary>
        void PerformAutoSteerClick();

        void TimedMessageBox(int timeout, string title, string message);
    }
}
