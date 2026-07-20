// ============================================================================
// FormGps.AutoSteerHost.cs
// Implementación de IAutoSteerHost sobre FormGPS. CAutoSteerUpdater (PGN de
// posición corregida + PGN 254 de autosteer, extraídos de UpdateFixPosition
// en Position.designer.cs) vive en AgOpenGPS.Core y consume el host a través
// de esta interfaz (inversión de dependencia — traspaso de portabilidad
// bloque 9, 2026-07-20).
// ============================================================================

using AgOpenGPS.Core;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public partial class FormGPS : IAutoSteerHost
    {
        ApplicationModel IAutoSteerHost.AppModel => AppModel;
        CNMEA IAutoSteerHost.Pn => pn;
        CFieldData IAutoSteerHost.Fd => fd;
        CContour IAutoSteerHost.Ct => ct;
        CTrack IAutoSteerHost.Trk => trk;
        CABLine IAutoSteerHost.ABLine => ABLine;
        CABCurve IAutoSteerHost.Curve => curve;
        CRecordedPath IAutoSteerHost.RecPath => recPath;
        CVehicle IAutoSteerHost.Vehicle => vehicle;
        CAHRS IAutoSteerHost.Ahrs => ahrs;
        CModuleComm IAutoSteerHost.Mc => mc;
        CISOBUS IAutoSteerHost.Isobus => isobus;
        CPGN_FE IAutoSteerHost.P254 => p_254;

        vec3 IAutoSteerHost.PivotAxlePos => pivotAxlePos;
        vec3 IAutoSteerHost.SteerAxlePos => steerAxlePos;

        double IAutoSteerHost.GpsHeading { get => gpsHeading; set => gpsHeading = value; }
        double IAutoSteerHost.AvgSpeed => avgSpeed;
        double IAutoSteerHost.LightbarDistance { get => lightbarDistance; set => lightbarDistance = value; }
        short IAutoSteerHost.GuidanceLineDistanceOff { get => guidanceLineDistanceOff; set => guidanceLineDistanceOff = value; }
        short IAutoSteerHost.GuidanceLineSteerAngle { get => guidanceLineSteerAngle; set => guidanceLineSteerAngle = value; }
        int IAutoSteerHost.CrossTrackError { get => crossTrackError; set => crossTrackError = value; }
        int IAutoSteerHost.MinSteerSpeedTimer { get => minSteerSpeedTimer; set => minSteerSpeedTimer = value; }

        bool IAutoSteerHost.IsBtnAutoSteerOn => isBtnAutoSteerOn;
        bool IAutoSteerHost.IsReverse { get => isReverse; set => isReverse = value; }
        bool IAutoSteerHost.IsChangingDirection { get => isChangingDirection; set => isChangingDirection = value; }
        bool IAutoSteerHost.IsSteerInReverse => isSteerInReverse;
        bool IAutoSteerHost.IsMetric => isMetric;
        bool IAutoSteerHost.IsSimTimerEnabled => timerSim.Enabled;

        void IAutoSteerHost.SendPgnToLoop(byte[] data) => SendPgnToLoop(data);
        void IAutoSteerHost.PerformAutoSteerClick() => btnAutoSteer.PerformClick();
        void IAutoSteerHost.TimedMessageBox(int timeout, string title, string message) => TimedMessageBox(timeout, title, message);
    }
}
