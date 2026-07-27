// SteerConfigDtos.cs — configuración del autoguiado (port del FormSteer nativo)
// para la pantalla direccion.html del Hub.
//
// Cada campo mapea 1:1 contra un valor de AgOpenGPS.Properties.Settings
// (setAS_* / setArdSteer_* / setVehicle_* / setDisplay_*). El mapeo real y el
// armado de los PGN 252/251 viven en AgroParallel.Adapters.SteerConfigService.
//
// Unidades: acá viajan en las MISMAS unidades que muestra la UI (el slider de
// la página), no en las del PGN — la conversión (x10, x100, byte packing) la
// hace el mapper, igual que la hacía el form nativo al leer sus hsbar/nud.

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class SteerConfigDto
    {
        // ---- Ganancias del lazo de dirección (PGN 252) ----
        /// <summary>setAS_Kp — ganancia proporcional del módulo (1..200).</summary>
        [JsonPropertyName("proportional_gain")] public int ProportionalGain { get; set; }

        /// <summary>setAS_minSteerPWM — PWM mínimo para vencer el rozamiento.</summary>
        [JsonPropertyName("min_pwm")] public int MinPwm { get; set; }

        /// <summary>setAS_highSteerPWM — PWM máximo. El "low" del PGN se deriva
        /// como high/3, igual que el FormSteer nativo.</summary>
        [JsonPropertyName("high_steer_pwm")] public int HighSteerPwm { get; set; }

        // ---- Sensor de ángulo (WAS) ----
        /// <summary>setAS_wasOffset — cuentas de corrimiento del cero (−4000..4000).</summary>
        [JsonPropertyName("was_offset")] public int WasOffset { get; set; }

        /// <summary>setAS_countsPerDegree — cuentas del WAS por grado de giro.</summary>
        [JsonPropertyName("counts_per_degree")] public int CountsPerDegree { get; set; }

        /// <summary>setAS_ackerman — corrección de Ackerman (%).</summary>
        [JsonPropertyName("ackerman")] public int Ackerman { get; set; }

        /// <summary>setVehicle_maxSteerAngle — ángulo máximo de las ruedas (°).</summary>
        [JsonPropertyName("max_steer_angle")] public double MaxSteerAngle { get; set; }

        // ---- Pure Pursuit ----
        /// <summary>setVehicle_goalPointLookAheadHold — segundos de anticipación
        /// sostenida. La UI lo muestra x0.1 (slider 10..70 → 1.0..7.0 s).</summary>
        [JsonPropertyName("hold_look_ahead")] public double HoldLookAhead { get; set; }

        /// <summary>setVehicle_goalPointLookAheadMult (slider x10).</summary>
        [JsonPropertyName("look_ahead_mult")] public double LookAheadMult { get; set; }

        /// <summary>setVehicle_goalPointAcquireFactor (slider x100).</summary>
        [JsonPropertyName("acquire_factor")] public double AcquireFactor { get; set; }

        /// <summary>purePursuitIntegralGainAB en % (slider 0..100 = 0.00..1.00).</summary>
        [JsonPropertyName("integral_pp")] public int IntegralPp { get; set; }

        // ---- Stanley ----
        /// <summary>stanleyDistanceErrorGain (slider x10).</summary>
        [JsonPropertyName("stanley_gain")] public double StanleyGain { get; set; }

        /// <summary>stanleyHeadingErrorGain (slider x10).</summary>
        [JsonPropertyName("heading_error_gain")] public double HeadingErrorGain { get; set; }

        /// <summary>stanleyIntegralGainAB en % (slider 0..100 = 0.00..1.00).</summary>
        [JsonPropertyName("integral_stanley")] public int IntegralStanley { get; set; }

        /// <summary>setVehicle_isStanleyUsed — true = Stanley (firme),
        /// false = Pure Pursuit (suave).</summary>
        [JsonPropertyName("stanley_pure")] public bool StanleyPure { get; set; }

        // ---- Zona muerta / compensaciones ----
        /// <summary>setAS_deadZoneHeading — en grados (el setting va x100).</summary>
        [JsonPropertyName("dead_zone_heading")] public double DeadZoneHeading { get; set; }

        /// <summary>setAS_deadZoneDelay — ciclos de espera.</summary>
        [JsonPropertyName("dead_zone_delay")] public int DeadZoneDelay { get; set; }

        /// <summary>setAS_uTurnCompensation — slider 2..20, se muestra −10 y se
        /// guarda x0.1 (mismo criterio que el hsbar nativo).</summary>
        [JsonPropertyName("u_turn_comp")] public double UTurnComp { get; set; }

        /// <summary>setAS_sideHillComp — slider 0..30, el setting va x0.01.</summary>
        [JsonPropertyName("side_hill_comp")] public int SideHillComp { get; set; }

        /// <summary>setAS_isSteerInReverse.</summary>
        [JsonPropertyName("steer_in_reverse")] public bool SteerInReverse { get; set; }

        // ---- Sensor de fin de giro (excluyentes) — setArdSteer_setting0/1 ----
        [JsonPropertyName("encoder")] public bool Encoder { get; set; }
        [JsonPropertyName("pressure_sensor")] public bool PressureSensor { get; set; }
        [JsonPropertyName("current_sensor")] public bool CurrentSensor { get; set; }

        /// <summary>setArdSteer_maxPulseCounts cuando el sensor es encoder.</summary>
        [JsonPropertyName("max_counts")] public int MaxCounts { get; set; }

        /// <summary>setArdSteer_maxPulseCounts (0..255) cuando el sensor es
        /// presión o corriente — la UI lo muestra como % de fondo de escala.</summary>
        [JsonPropertyName("sensor_limit")] public int SensorLimit { get; set; }

        // ---- Configuración del módulo (bits de setting0/setting1) ----
        [JsonPropertyName("danfoss")] public bool Danfoss { get; set; }
        [JsonPropertyName("invert_was")] public bool InvertWas { get; set; }
        [JsonPropertyName("invert_steer")] public bool InvertSteer { get; set; }
        [JsonPropertyName("invert_relays")] public bool InvertRelays { get; set; }

        /// <summary>"Cytron" | "IBT2".</summary>
        [JsonPropertyName("motor_drive")] public string MotorDrive { get; set; }

        /// <summary>"Single" | "Differential".</summary>
        [JsonPropertyName("conv_type")] public string ConvType { get; set; }

        /// <summary>"None" | "Switch" | "Button".</summary>
        [JsonPropertyName("steer_enable")] public string SteerEnable { get; set; }

        /// <summary>"X" | "Y" — eje del IMU del módulo.</summary>
        [JsonPropertyName("imu_axis")] public string ImuAxis { get; set; }

        // ---- Velocidades ----
        /// <summary>setAS_functionSpeedLimit (km/h).</summary>
        [JsonPropertyName("guidance_speed_limit")] public double GuidanceSpeedLimit { get; set; }

        /// <summary>setAS_minSteerSpeed (km/h) — también va al PGN 251 x10.</summary>
        [JsonPropertyName("min_steer_speed")] public double MinSteerSpeed { get; set; }

        /// <summary>setAS_maxSteerSpeed (km/h).</summary>
        [JsonPropertyName("max_steer_speed")] public double MaxSteerSpeed { get; set; }

        // ---- Pantalla / sobre la línea ----
        /// <summary>setDisplay_lineWidth (px).</summary>
        [JsonPropertyName("line_width")] public int LineWidth { get; set; }

        /// <summary>setAS_snapDistance (cm).</summary>
        [JsonPropertyName("snap_distance")] public double SnapDistance { get; set; }

        /// <summary>setAS_guidanceLookAheadTime (s).</summary>
        [JsonPropertyName("guidance_look_ahead")] public double GuidanceLookAhead { get; set; }

        /// <summary>setDisplay_lightbarCmPerPixel.</summary>
        [JsonPropertyName("cm_per_pixel")] public int CmPerPixel { get; set; }

        /// <summary>"lightbar" | "steerbar" (setMenu_isLightbarNotSteerBar).</summary>
        [JsonPropertyName("guidance_bar")] public string GuidanceBar { get; set; }

        /// <summary>setMenu_isLightbarOn.</summary>
        [JsonPropertyName("display_lightbar")] public bool DisplayLightbar { get; set; }
    }

    /// <summary>Resultado de poner el sensor de ángulo (WAS) en cero.</summary>
    public sealed class SteerZeroWasResult
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }

        /// <summary>Nuevo setAS_wasOffset (la UI actualiza su slider con esto).</summary>
        [JsonPropertyName("was_offset")] public int WasOffset { get; set; }

        /// <summary>Ángulo leído del módulo en el momento del cero (°).</summary>
        [JsonPropertyName("steer_angle")] public double SteerAngle { get; set; }

        /// <summary>Motivo cuando ok=false (ej. "fuera-de-rango").</summary>
        [JsonPropertyName("error")] public string Error { get; set; }
    }
}
