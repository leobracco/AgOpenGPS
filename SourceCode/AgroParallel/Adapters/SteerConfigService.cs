// ============================================================================
// SteerConfigService.cs — implementación REAL de ISteerConfigService.
//
// Reemplaza el stopgap que guardaba un blob JSON: acá la config de la pantalla
// Dirección (direccion.html) se lee y se escribe contra los settings reales de
// PilotX (AgOpenGPS.Properties.Settings: setAS_* / setArdSteer_* / setVehicle_*
// / setDisplay_*), se aplica al CVehicle vivo y se manda al módulo de dirección
// por los PGN 252/251 (vía CSettingsSender, el mismo que usa el form nativo).
//
// El mapeo es port 1:1 del FormSteer nativo (GPS/Forms/Settings/FormSteer.cs):
// mismas escalas (x10 / x100), mismo empaquetado de bits de setting0/setting1 y
// misma derivación lowSteerPWM = highSteerPWM / 3.
//
// Archivo COMPARTIDO por link (no ProjectReference): lo compilan tanto
// AgOpenGPS.csproj (host WinForms/FormGPS) como PilotX.GuidanceEngine.csproj
// (motor headless). Solo depende de AgOpenGPS.Core (Settings, CVehicle) y
// AgroParallel.Models — nada de WinForms ni de OpenGL.
//
// Lo específico de cada host entra por delegados en el constructor:
//   · actualSteerAngleDegrees → lectura viva del WAS (mc.actualSteerAngleDegrees)
//   · sendSettings            → CSettingsSender.SendSettings (el host marshalea
//                               al hilo que corresponda)
//   · applyLive (opcional)    → campos vivos que no viven en CVehicle
//                               (lightbar, ancho de línea, snap distance…)
// ============================================================================

using AgLibrary.Logging;
using AgOpenGPS;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using System;

namespace AgroParallel.Adapters
{
    public sealed class SteerConfigService : ISteerConfigService
    {
        // Tope del form nativo: más de ±3900 cuentas de offset es sensor mal
        // montado, no un cero legítimo (btnZeroWAS_Click).
        private const int WasOffsetLimit = 3900;

        // Tope duro del manejo libre, el mismo del FormSteer nativo
        // (btnSteerAngleUp/Down_MouseDown). Se cruza con el ángulo máximo del
        // vehículo: manda el más chico de los dos.
        private const double FreeDriveMaxAngle = 40.0;

        private readonly CVehicle _vehicle;
        private readonly Func<double> _actualSteerAngleDegrees;
        private readonly Action _sendSettings;
        private readonly Action<SteerConfigDto> _applyLive;
        private readonly Func<double> _avgSpeed;

        public SteerConfigService(
            CVehicle vehicle,
            Func<double> actualSteerAngleDegrees,
            Action sendSettings,
            Action<SteerConfigDto> applyLive = null,
            Func<double> avgSpeed = null)
        {
            _vehicle = vehicle;
            _actualSteerAngleDegrees = actualSteerAngleDegrees;
            _sendSettings = sendSettings;
            _applyLive = applyLive;
            _avgSpeed = avgSpeed;
        }

        // Nombre completo a propósito: este archivo lo compilan dos proyectos
        // distintos y en ambos "Properties" a secas es ambiguo con el namespace
        // generado del propio assembly.
        private static global::AgOpenGPS.Properties.Settings S
            => global::AgOpenGPS.Properties.Settings.Default;

        // --------------------------------------------------------------------
        // Lectura
        // --------------------------------------------------------------------
        public SteerConfigDto Get()
        {
            int set0 = S.setArdSteer_setting0;
            int set1 = S.setArdSteer_setting1;

            return new SteerConfigDto
            {
                ProportionalGain = S.setAS_Kp,
                MinPwm = S.setAS_minSteerPWM,
                HighSteerPwm = S.setAS_highSteerPWM,

                WasOffset = S.setAS_wasOffset,
                CountsPerDegree = S.setAS_countsPerDegree,
                Ackerman = S.setAS_ackerman,
                MaxSteerAngle = S.setVehicle_maxSteerAngle,

                // Los sliders de la UI muestran valor x0.1 / x0.01: se devuelve
                // el valor DEL SLIDER, no el del setting (la página aplica la
                // escala con data-scale para el display).
                HoldLookAhead = Math.Round(S.setVehicle_goalPointLookAheadHold * 10.0),
                LookAheadMult = Math.Round(S.setVehicle_goalPointLookAheadMult * 10.0),
                AcquireFactor = Math.Round(S.setVehicle_goalPointAcquireFactor * 100.0),
                IntegralPp = (int)Math.Round(S.purePursuitIntegralGainAB * 100.0),

                StanleyGain = Math.Round(S.stanleyDistanceErrorGain * 10.0),
                HeadingErrorGain = Math.Round(S.stanleyHeadingErrorGain * 10.0),
                IntegralStanley = (int)Math.Round(S.stanleyIntegralGainAB * 100.0),
                StanleyPure = S.setVehicle_isStanleyUsed,

                DeadZoneHeading = S.setAS_deadZoneHeading / 100.0,
                DeadZoneDelay = S.setAS_deadZoneDelay,
                UTurnComp = Math.Round(S.setAS_uTurnCompensation * 10.0),
                SideHillComp = (int)Math.Round(S.setAS_sideHillComp * 100.0),
                SteerInReverse = S.setAS_isSteerInReverse,

                Encoder = (set0 & 128) != 0,
                PressureSensor = (set1 & 2) != 0,
                CurrentSensor = (set1 & 4) != 0,
                MaxCounts = S.setArdSteer_maxPulseCounts,
                SensorLimit = S.setArdSteer_maxPulseCounts,

                Danfoss = (set1 & 1) != 0,
                InvertWas = (set0 & 1) != 0,
                InvertRelays = (set0 & 2) != 0,
                InvertSteer = (set0 & 4) != 0,
                ConvType = (set0 & 8) != 0 ? "Single" : "Differential",
                MotorDrive = (set0 & 16) != 0 ? "Cytron" : "IBT2",
                SteerEnable = (set0 & 32) != 0 ? "Switch" : ((set0 & 64) != 0 ? "Button" : "None"),
                ImuAxis = (set1 & 8) != 0 ? "Y" : "X",

                GuidanceSpeedLimit = S.setAS_functionSpeedLimit,
                MinSteerSpeed = S.setAS_minSteerSpeed,
                MaxSteerSpeed = S.setAS_maxSteerSpeed,

                LineWidth = S.setDisplay_lineWidth,
                SnapDistance = S.setAS_snapDistance,
                GuidanceLookAhead = S.setAS_guidanceLookAheadTime,
                CmPerPixel = S.setDisplay_lightbarCmPerPixel,
                GuidanceBar = S.setMenu_isLightbarNotSteerBar ? "lightbar" : "steerbar",
                DisplayLightbar = S.setMenu_isLightbarOn,
            };
        }

        // --------------------------------------------------------------------
        // Escritura + envío al módulo
        // --------------------------------------------------------------------
        public bool Save(SteerConfigDto c)
        {
            if (c == null) return false;

            // ---- PGN 252 (steer settings) ----
            S.setAS_Kp = ClampByte(c.ProportionalGain);
            S.setAS_minSteerPWM = ClampByte(c.MinPwm);
            S.setAS_highSteerPWM = ClampByte(c.HighSteerPwm);
            // El form nativo NO expone el PWM bajo: lo deriva del alto.
            S.setAS_lowSteerPWM = ClampByte(c.HighSteerPwm / 3);

            S.setAS_wasOffset = Clamp(c.WasOffset, -4000, 4000);
            S.setAS_countsPerDegree = ClampByte(c.CountsPerDegree);
            S.setAS_ackerman = ClampByte(c.Ackerman);

            // ---- Vehículo / guiado ----
            S.setVehicle_maxSteerAngle = c.MaxSteerAngle;
            S.setVehicle_goalPointLookAheadHold = c.HoldLookAhead * 0.1;
            S.setVehicle_goalPointLookAheadMult = c.LookAheadMult * 0.1;
            S.setVehicle_goalPointAcquireFactor = c.AcquireFactor * 0.01;
            S.purePursuitIntegralGainAB = c.IntegralPp * 0.01;

            S.stanleyDistanceErrorGain = c.StanleyGain * 0.1;
            S.stanleyHeadingErrorGain = c.HeadingErrorGain * 0.1;
            S.stanleyIntegralGainAB = c.IntegralStanley * 0.01;
            S.setVehicle_isStanleyUsed = c.StanleyPure;

            S.setAS_deadZoneHeading = (int)Math.Round(c.DeadZoneHeading * 100.0);
            S.setAS_deadZoneDelay = c.DeadZoneDelay;
            S.setAS_uTurnCompensation = c.UTurnComp * 0.1;
            S.setAS_sideHillComp = c.SideHillComp * 0.01;
            S.setAS_isSteerInReverse = c.SteerInReverse;

            S.setAS_functionSpeedLimit = c.GuidanceSpeedLimit;
            S.setAS_minSteerSpeed = c.MinSteerSpeed;
            S.setAS_maxSteerSpeed = c.MaxSteerSpeed;

            // ---- Pantalla / sobre la línea ----
            S.setDisplay_lineWidth = c.LineWidth;
            S.setAS_snapDistance = c.SnapDistance;
            S.setAS_guidanceLookAheadTime = c.GuidanceLookAhead;
            S.setDisplay_lightbarCmPerPixel = c.CmPerPixel;
            S.setMenu_isLightbarNotSteerBar = !string.Equals(c.GuidanceBar, "steerbar", StringComparison.OrdinalIgnoreCase);
            S.setMenu_isLightbarOn = c.DisplayLightbar;

            // ---- PGN 251 (config del módulo): bits de setting0/setting1 ----
            // Los 3 sensores de fin de giro son excluyentes (mismo criterio que
            // el form nativo: encoder gana sobre presión, presión sobre corriente).
            bool encoder = c.Encoder;
            bool pressure = !encoder && c.PressureSensor;
            bool current = !encoder && !pressure && c.CurrentSensor;

            int set0 = 0;
            if (c.InvertWas) set0 |= 1;
            if (c.InvertRelays) set0 |= 2;
            if (c.InvertSteer) set0 |= 4;
            if (string.Equals(c.ConvType, "Single", StringComparison.OrdinalIgnoreCase)) set0 |= 8;
            if (string.Equals(c.MotorDrive, "Cytron", StringComparison.OrdinalIgnoreCase)) set0 |= 16;
            if (string.Equals(c.SteerEnable, "Switch", StringComparison.OrdinalIgnoreCase)) set0 |= 32;
            if (string.Equals(c.SteerEnable, "Button", StringComparison.OrdinalIgnoreCase)) set0 |= 64;
            if (encoder) set0 |= 128;
            S.setArdSteer_setting0 = (byte)set0;

            int set1 = 0;
            if (c.Danfoss) set1 |= 1;
            if (pressure) set1 |= 2;
            if (current) set1 |= 4;
            if (string.Equals(c.ImuAxis, "Y", StringComparison.OrdinalIgnoreCase)) set1 |= 8;
            S.setArdSteer_setting1 = (byte)set1;

            S.setArdMac_isDanfoss = c.Danfoss;

            // Con presión/corriente el límite viene del slider (0..255 = % de
            // fondo de escala); con encoder, del contador de pulsos.
            S.setArdSteer_maxPulseCounts = ClampByte((pressure || current) ? c.SensorLimit : c.MaxCounts);

            S.Save();

            ApplyToVehicle(c);
            _applyLive?.Invoke(c);

            // Manda 252 + 251 (+238) al módulo, igual que el form nativo al salir.
            _sendSettings?.Invoke();

            return true;
        }

        // --------------------------------------------------------------------
        // Cero del WAS (port de btnZeroWAS_Click)
        // --------------------------------------------------------------------
        public SteerZeroWasResult ZeroWas()
        {
            double angle = _actualSteerAngleDegrees != null ? _actualSteerAngleDegrees() : 0.0;
            int delta = (int)(S.setAS_countsPerDegree * -angle);
            int offset = S.setAS_wasOffset + delta;

            if (Math.Abs(offset) > WasOffsetLimit)
            {
                return new SteerZeroWasResult
                {
                    Ok = false,
                    WasOffset = S.setAS_wasOffset,
                    SteerAngle = angle,
                    Error = "fuera-de-rango",
                };
            }

            S.setAS_wasOffset = offset;
            S.Save();
            _sendSettings?.Invoke();

            return new SteerZeroWasResult { Ok = true, WasOffset = offset, SteerAngle = angle };
        }

        // --------------------------------------------------------------------
        // Manejo libre (port del bloque "Free Drive" de FormSteer.cs)
        //
        // Prendido, el PGN 254 sale con status=1 y el ángulo que fija el
        // operario (CAutoSteerUpdater, rama "Drive button is on"): el módulo
        // mueve el volante SIN guía y sin importar dónde esté el tractor. Es
        // para probar la dirección PARADO — de ahí los dos candados:
        //
        //   1. acá, al prender: se rechaza por encima del límite de velocidad
        //      de funciones de guiado (el mismo que usa el giro manual), y se
        //      rechaza también si el host no sabe informar velocidad — sin
        //      velocidad no hay forma de saber si el tractor está quieto, y
        //      "no sé" tiene que fallar cerrado, no abierto;
        //   2. en CAutoSteerUpdater, en cada PGN: si el tractor arranca con el
        //      manejo libre prendido, se apaga solo. Ese es el candado que
        //      importa — este de acá solo cubre el momento del click.
        // --------------------------------------------------------------------
        // Latidos de gracia del watchdog: el PGN 254 sale a ~10 Hz y la pantalla
        // consulta cada 700 ms, así que 30 (≈3 s) aguanta un hipo de red sin
        // apagar nada, y corta rápido si la pantalla desapareció de verdad.
        private const int FreeDriveLatidos = 30;

        public FreeDriveStateDto GetFreeDrive()
        {
            // Consultar ES el latido: la pantalla que muestra el manejo libre
            // relee el estado mientras está prendido.
            Latir();
            return Estado(true, null);
        }

        /// <summary>Recarga el watchdog del manejo libre. Solo cuenta con el
        /// modo prendido: apagado no hay nada que vigilar.</summary>
        private void Latir()
        {
            if (_vehicle != null && _vehicle.isInFreeDriveMode)
                _vehicle.freeDriveWatchdog = FreeDriveLatidos;
        }

        public FreeDriveStateDto SetFreeDrive(bool on)
        {
            if (_vehicle == null) return Estado(false, "error-interno");

            if (!on)
            {
                // Apagar SIEMPRE vale: es la salida de emergencia.
                _vehicle.isInFreeDriveMode = false;
                _vehicle.driveFreeSteerAngle = 0;
                _vehicle.freeDriveWatchdog = -1;
                return Estado(true, null);
            }

            if (_avgSpeed == null)
            {
                Log.EventWriter("Manejo libre rechazado: el host no informa velocidad");
                return Estado(false, "sin-velocidad");
            }

            double speed = Math.Abs(_avgSpeed());
            double limit = LimiteVelocidad;
            if (speed >= limit)
            {
                Log.EventWriter($"Manejo libre rechazado: {speed:F1} km/h supera el limite de {limit:F1}");
                return Estado(false, "velocidad");
            }

            _vehicle.isInFreeDriveMode = true;
            _vehicle.driveFreeSteerAngle = 0;
            // Prendido desde una pantalla remota: entra al watchdog. Si esa
            // pantalla deja de latir, el motor lo apaga.
            _vehicle.freeDriveWatchdog = FreeDriveLatidos;
            Log.EventWriter("Manejo libre prendido");
            return Estado(true, null);
        }

        public FreeDriveStateDto NudgeFreeDrive(int dir)
        {
            if (_vehicle == null) return Estado(false, "error-interno");
            // Sin el modo prendido el ángulo no va a ningún lado (el PGN lo
            // ignora): aceptarlo sería decir "ok" sin efecto.
            if (!_vehicle.isInFreeDriveMode) return Estado(false, "apagado");
            if (dir == 0) return Estado(true, null);

            double max = MaxAngulo;
            double v = _vehicle.driveFreeSteerAngle + (dir > 0 ? 1 : -1);
            if (v > max) v = max;
            else if (v < -max) v = -max;
            _vehicle.driveFreeSteerAngle = v;
            Latir();
            return Estado(true, null);
        }

        public FreeDriveStateDto ToggleFreeDriveZero()
        {
            if (_vehicle == null) return Estado(false, "error-interno");
            if (!_vehicle.isInFreeDriveMode) return Estado(false, "apagado");

            // btnFreeDriveZero_Click: 0 ↔ 5°.
            _vehicle.driveFreeSteerAngle = _vehicle.driveFreeSteerAngle == 0 ? 5 : 0;
            Latir();
            return Estado(true, null);
        }

        private double LimiteVelocidad
        {
            get
            {
                double lim = _vehicle != null ? _vehicle.functionSpeedLimit : 0;
                // El vehículo recién construido puede tener 0 hasta que se
                // aplica la config; ahí manda el setting.
                if (lim <= 0) lim = S.setAS_functionSpeedLimit;
                return lim > 0 ? lim : 1.0;
            }
        }

        private double MaxAngulo
        {
            get
            {
                double max = _vehicle != null ? _vehicle.maxSteerAngle : 0;
                if (max <= 0) max = S.setVehicle_maxSteerAngle;
                if (max <= 0 || max > FreeDriveMaxAngle) max = FreeDriveMaxAngle;
                return max;
            }
        }

        private FreeDriveStateDto Estado(bool ok, string error)
        {
            return new FreeDriveStateDto
            {
                Ok = ok,
                Error = error,
                On = _vehicle != null && _vehicle.isInFreeDriveMode,
                Angle = _vehicle != null ? _vehicle.driveFreeSteerAngle : 0,
                MaxAngle = MaxAngulo,
                Speed = _avgSpeed != null ? Math.Abs(_avgSpeed()) : 0,
                SpeedLimit = LimiteVelocidad,
            };
        }

        // --------------------------------------------------------------------
        private void ApplyToVehicle(SteerConfigDto c)
        {
            if (_vehicle == null) return;

            _vehicle.maxSteerAngle = S.setVehicle_maxSteerAngle;
            _vehicle.goalPointLookAheadHold = S.setVehicle_goalPointLookAheadHold;
            _vehicle.goalPointLookAheadMult = S.setVehicle_goalPointLookAheadMult;
            _vehicle.goalPointAcquireFactor = S.setVehicle_goalPointAcquireFactor;
            _vehicle.purePursuitIntegralGain = S.purePursuitIntegralGainAB;

            _vehicle.stanleyDistanceErrorGain = S.stanleyDistanceErrorGain;
            _vehicle.stanleyHeadingErrorGain = S.stanleyHeadingErrorGain;
            _vehicle.stanleyIntegralGainAB = S.stanleyIntegralGainAB;

            _vehicle.deadZoneHeading = S.setAS_deadZoneHeading;
            _vehicle.deadZoneDelay = S.setAS_deadZoneDelay;
            _vehicle.uturnCompensation = S.setAS_uTurnCompensation;

            _vehicle.minSteerSpeed = S.setAS_minSteerSpeed;
            _vehicle.maxSteerSpeed = S.setAS_maxSteerSpeed;
            _vehicle.functionSpeedLimit = S.setAS_functionSpeedLimit;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static byte ClampByte(int v) => (byte)Clamp(v, 0, 255);
    }
}
