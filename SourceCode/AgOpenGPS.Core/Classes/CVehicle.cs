//Please, if you use this, share the improvements

using AgOpenGPS.Core.Models;
using System;

namespace AgOpenGPS
{
    public class CVehicle
    {
        // Host invertido (FormGPS implementa IVehicleHost) — traspaso 2026-07-17.
        // internal (era private): lo lee VehicleDrawExtensions (mismo assembly).
        internal readonly IVehicleHost mf;

        public int deadZoneHeading, deadZoneDelay;
        public int deadZoneDelayCounter;
        public bool isInDeadZone;

        //min vehicle speed allowed before turning shit off
        public double slowSpeedCutoff = 0;

        //autosteer values
        public double goalPointLookAheadHold, goalPointLookAheadMult, goalPointAcquireFactor, uturnCompensation;

        public double stanleyDistanceErrorGain, stanleyHeadingErrorGain;
        public double maxSteerAngle, maxSteerSpeed, minSteerSpeed;
        public double maxAngularVelocity;
        public double hydLiftLookAheadTime;

        public double hydLiftLookAheadDistanceLeft, hydLiftLookAheadDistanceRight;

        public bool isHydLiftOn;
        public double stanleyIntegralGainAB, purePursuitIntegralGain;

        //flag for free drive window to control autosteer
        public bool isInFreeDriveMode;

        //the trackbar angle for free drive
        public double driveFreeSteerAngle = 0;

        // Latidos que le quedan al manejo libre antes de apagarse solo.
        // −1 = sin watchdog (lo prendió el FormSteer nativo, que vive mientras
        // la ventana está abierta). ≥0 = lo prendió una pantalla remota (el Hub
        // por /api/steer/freedrive): esa pantalla puede desaparecer de golpe —
        // WebView cerrado, PilotX caído — y el volante NO puede quedar bajo
        // control de nadie. Cada consulta de la pantalla lo recarga;
        // CAutoSteerUpdater lo descuenta en cada PGN.
        public int freeDriveWatchdog = -1;

        public double modeXTE, modeActualXTE = 0, modeActualHeadingError = 0;
        public int modeTime = 0;

        public double functionSpeedLimit;

        public CVehicle(IVehicleHost _f)
        {
            //constructor
            mf = _f;

            VehicleConfig = new VehicleConfig();

            VehicleConfig.AntennaHeight = Properties.Settings.Default.setVehicle_antennaHeight;
            VehicleConfig.AntennaPivot = Properties.Settings.Default.setVehicle_antennaPivot;
            VehicleConfig.AntennaOffset = Properties.Settings.Default.setVehicle_antennaOffset;

            VehicleConfig.Wheelbase = Properties.Settings.Default.setVehicle_wheelbase;

            slowSpeedCutoff = Properties.Settings.Default.setVehicle_slowSpeedCutoff;

            goalPointLookAheadHold = Properties.Settings.Default.setVehicle_goalPointLookAheadHold;
            goalPointLookAheadMult = Properties.Settings.Default.setVehicle_goalPointLookAheadMult;
            goalPointAcquireFactor = Properties.Settings.Default.setVehicle_goalPointAcquireFactor;

            stanleyDistanceErrorGain = Properties.Settings.Default.stanleyDistanceErrorGain;
            stanleyHeadingErrorGain = Properties.Settings.Default.stanleyHeadingErrorGain;

            maxAngularVelocity = Properties.Settings.Default.setVehicle_maxAngularVelocity;
            maxSteerAngle = Properties.Settings.Default.setVehicle_maxSteerAngle;

            isHydLiftOn = false;

            VehicleConfig.TrackWidth = Properties.Settings.Default.setVehicle_trackWidth;

            stanleyIntegralGainAB = Properties.Settings.Default.stanleyIntegralGainAB;

            purePursuitIntegralGain = Properties.Settings.Default.purePursuitIntegralGainAB;
            VehicleConfig.Type = (VehicleType)Properties.Settings.Default.setVehicle_vehicleType;

            hydLiftLookAheadTime = Properties.Settings.Default.setVehicle_hydraulicLiftLookAhead;

            deadZoneHeading = Properties.Settings.Default.setAS_deadZoneHeading;
            deadZoneDelay = Properties.Settings.Default.setAS_deadZoneDelay;

            isInFreeDriveMode = false;

            //how far from line before it becomes Hold
            modeXTE = 0.2;

            //how long before hold is activated
            modeTime = 1;

            functionSpeedLimit = Properties.Settings.Default.setAS_functionSpeedLimit;
            maxSteerSpeed = Properties.Settings.Default.setAS_maxSteerSpeed;
            minSteerSpeed = Properties.Settings.Default.setAS_minSteerSpeed;

            uturnCompensation = Properties.Settings.Default.setAS_uTurnCompensation;
        }

        public int modeTimeCounter = 0;
        public double goalDistance = 0;

        public VehicleConfig VehicleConfig { get; }

        public double UpdateGoalPointDistance()
        {
            double xTE = Math.Abs(modeActualXTE);
            double goalPointDistance = mf.AvgSpeed * 0.05 * goalPointLookAheadMult;

            double LoekiAheadHold = goalPointLookAheadHold;
            double LoekiAheadAcquire = goalPointLookAheadHold * goalPointAcquireFactor;

            if (xTE <= 0.1)
            {
                goalPointDistance *= LoekiAheadHold;
                goalPointDistance += LoekiAheadHold;
            }

            else if (xTE > 0.1 && xTE < 0.4)
            {
                xTE -= 0.1;

                LoekiAheadHold = (1 - (xTE / 0.3)) * (LoekiAheadHold - LoekiAheadAcquire);
                LoekiAheadHold += LoekiAheadAcquire;

                goalPointDistance *= LoekiAheadHold;
                goalPointDistance += LoekiAheadHold;
            }
            else
            {
                goalPointDistance *= LoekiAheadAcquire;
                goalPointDistance += LoekiAheadAcquire;
            }

            if (goalPointDistance < 2) goalPointDistance = 2;
            goalDistance = goalPointDistance;

            return goalPointDistance;
        }

        //DrawVehicle() y AckermannAngles() se movieron a VehicleDrawExtensions
        //(DrawLib/GuidanceDrawExtensions.cs): eran el único uso de OpenTK/GLW
        //acá (traspaso portabilidad).
    }
}
