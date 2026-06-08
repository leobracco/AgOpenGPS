// ============================================================================
// CoreXEcuDtos.cs
// DTOs del módulo CoreX-ECU — firmware Teensy 4.1 que corre el autosteer
// (IMU BNO + WAS Keya encoder o analógico + CAN Keya + Ethernet PGN UDP).
//
// Spec firmware v1.11+:
//   GET  /api/status   -> CoreXEcuStatusDto  (telemetría runtime — incluye was.source/ads_present/ads_raw)
//   GET  /api/params   -> CoreXEcuParamsDto  (config persistida en EEPROM)
//   POST /api/params   -> { ok, updated: { autoZero, keya, imu } }
//   GET  /api/wassrc   -> CoreXEcuWassrcDto  (fuente activa del WAS + estado ADS1115)
//   POST /api/wassrc   -> CoreXEcuWassrcUpdateResultDto (cambia fuente en caliente + persist EEPROM)
//   POST /api/zero     -> { ok, zeroTicks }
//   POST /api/reboot   -> soft-reset (watchdog)
//
// Los nombres de campos en el JSON del firmware son una mezcla de snake_case
// (/status) y camelCase (/params). Los `[JsonPropertyName]` de abajo replican
// EXACTAMENTE lo que el firmware emite, para que System.Text.Json deserialice
// bien en el service. EmbedIO al re-serializar al cliente Hub ignora estos
// atributos y emite camelCase de los nombres C# — el JS del Hub lee siempre
// camelCase (ej. `imu.yawDeg`, `was.zeroDone`).
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Config persistida del Hub: cómo contactar al Teensy (corexEcu.json).</summary>
    public sealed class CoreXEcuConfigDto
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>IP del Teensy en la LAN del tractor (default 192.168.5.126).</summary>
        [JsonPropertyName("ip")]
        public string Ip { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }

        /// <summary>Timeout HTTP por request (ms). El firmware responde típico ~60 ms.</summary>
        [JsonPropertyName("timeout_ms")]
        public int TimeoutMs { get; set; }

        /// <summary>Fuente del WAS que el operario eligió desde el Hub:
        /// "keya"     → encoder del motor Keya por CAN (UI: "Encoder motor").
        /// "ads_se"   → ADS1115 single-ended A0 (UI: "WAS").
        /// "ads_diff" → ADS1115 differential A0-A1 (valor legacy aceptado, se
        ///              renderiza visualmente como "WAS" en el segmented).
        /// "bno_was"  → BNO085 en modo RVC sobre Serial3 (UI: "YAW", firmware v1.14+).
        ///              Si el Teensy todavía está en v1.11..v1.13, /api/wassrc
        ///              devuelve 400 invalid_source y la UI lo muestra.
        /// Migración: "yaw" del Hub pre-v1.14 se normaliza a "bno_was".
        /// El Hub la persiste acá como preferencia y la propaga al firmware via
        /// POST /api/wassrc {"source":"..."} (endpoint dedicado de v1.11+).
        /// El firmware persiste a EEPROM y la aplica en caliente.</summary>
        [JsonPropertyName("was_source")]
        public string WasSource { get; set; }

        public CoreXEcuConfigDto()
        {
            Enabled = true;
            Ip = "192.168.5.126";
            Port = 80;
            TimeoutMs = 3000;
            WasSource = "keya";
        }
    }

    // ====================== /api/status ======================================

    public sealed class CoreXEcuImuDto
    {
        [JsonPropertyName("present")]      public bool Present { get; set; }
        /// <summary>"rvc" | "i2c" | "none".</summary>
        [JsonPropertyName("mode")]         public string Mode { get; set; }
        [JsonPropertyName("yaw_deg")]      public double YawDeg { get; set; }
        [JsonPropertyName("roll_deg")]     public double RollDeg { get; set; }
        [JsonPropertyName("pitch_deg")]    public double PitchDeg { get; set; }
        /// <summary>Velocidad angular en °/s — guardia del auto-zero.</summary>
        [JsonPropertyName("yaw_rate_dps")] public double YawRateDps { get; set; }

        public CoreXEcuImuDto() { Mode = "none"; }
    }

    public sealed class CoreXEcuWasDto
    {
        /// <summary>"keya" | "ads_se" | "ads_diff" (firmware v1.11+).</summary>
        [JsonPropertyName("source")]        public string Source { get; set; }
        [JsonPropertyName("angle_deg")]     public double AngleDeg { get; set; }
        [JsonPropertyName("encoder_raw")]   public int EncoderRaw { get; set; }
        [JsonPropertyName("zero_ticks")]    public int ZeroTicks { get; set; }
        [JsonPropertyName("zero_done")]     public bool ZeroDone { get; set; }
        /// <summary>Keya only. En modo ADS, este recalc se inhibe — el firmware
        /// preserva la cal Keya original para cuando se vuelva a esa fuente.</summary>
        [JsonPropertyName("ticks_per_deg")] public double TicksPerDeg { get; set; }
        /// <summary>True si el probe I²C al ADS1115 contestó (firmware v1.11+).
        /// En modo Keya queda false aunque el ADS esté físicamente conectado.</summary>
        [JsonPropertyName("ads_present")]   public bool AdsPresent { get; set; }
        /// <summary>Última lectura cruda del ADS (post >>1 shift). 0 en modo Keya.</summary>
        [JsonPropertyName("ads_raw")]       public int AdsRaw { get; set; }

        public CoreXEcuWasDto() { Source = ""; }
    }

    public sealed class CoreXEcuGpsDto
    {
        [JsonPropertyName("speed_kmh")]   public double SpeedKmh { get; set; }
        [JsonPropertyName("speed_knots")] public double SpeedKnots { get; set; }
        [JsonPropertyName("heading_deg")] public double HeadingDeg { get; set; }
        /// <summary>true tras el primer GGA — sino el firmware no tiene fix por NMEA.</summary>
        [JsonPropertyName("gga_seen")]    public bool GgaSeen { get; set; }
    }

    public sealed class CoreXEcuCanDto
    {
        [JsonPropertyName("keya_steer_enabled")] public bool KeyaSteerEnabled { get; set; }
        [JsonPropertyName("keya_current_a")]     public double KeyaCurrentA { get; set; }
    }

    public sealed class CoreXEcuAutosteerDto
    {
        /// <summary>Loop del autosteer corriendo.</summary>
        [JsonPropertyName("running")]          public bool Running { get; set; }
        /// <summary>AOG mandando PGN 254 con guidance ON (watchdog &lt; 100).</summary>
        [JsonPropertyName("guidance_active")]  public bool GuidanceActive { get; set; }
        /// <summary>Watchdog del PGN 254; ≥ 100 → motor desconectado.</summary>
        [JsonPropertyName("watchdog")]         public int Watchdog { get; set; }
        [JsonPropertyName("pwm")]              public int Pwm { get; set; }
        [JsonPropertyName("setpoint_deg")]     public double SetpointDeg { get; set; }
    }

    /// <summary>Estado del motor test manual (firmware v1.09+). Si test_active=true
    /// el Hub debe mostrar el contador y bloquear el slider de PWM ajeno hasta que
    /// expire test_remaining_ms.</summary>
    public sealed class CoreXEcuMotorDto
    {
        [JsonPropertyName("test_active")]        public bool TestActive { get; set; }
        [JsonPropertyName("test_pwm")]           public int TestPwm { get; set; }
        [JsonPropertyName("test_remaining_ms")]  public int TestRemainingMs { get; set; }
    }

    /// <summary>Snapshot completo devuelto por GET /api/status del Teensy v1.08.</summary>
    public sealed class CoreXEcuStatusDto
    {
        // ----- envoltura propia del Hub (no la emite el firmware) ------------
        // Marca si la última llamada al Teensy fue exitosa. Si !ok, el resto
        // de los campos vienen vacíos/cero y la UI debe pintar el banner de
        // error con `error_code` + `error`. Esto evita que la UI vea un 500.
        [JsonPropertyName("ok")]              public bool Ok { get; set; }
        /// <summary>"teensy" si el snapshot vino del firmware, "stub" si fallback.</summary>
        [JsonPropertyName("source")]          public string Source { get; set; }
        [JsonPropertyName("error_code")]      public string ErrorCode { get; set; }
        [JsonPropertyName("error")]           public string Error { get; set; }
        [JsonPropertyName("error_technical")] public string ErrorTechnical { get; set; }

        // ----- campos que vienen del firmware --------------------------------
        [JsonPropertyName("firmware")]   public string Firmware { get; set; }
        [JsonPropertyName("version")]    public string Version { get; set; }
        [JsonPropertyName("uptime_sec")] public double UptimeSec { get; set; }
        /// <summary>IP del Teensy. Top-level en el spec v1.08 (ya no anidado en eth).</summary>
        [JsonPropertyName("ip")]         public string Ip { get; set; }
        /// <summary>true cuando el link Ethernet está up.</summary>
        [JsonPropertyName("ethernet")]   public bool Ethernet { get; set; }

        [JsonPropertyName("imu")]       public CoreXEcuImuDto Imu { get; set; }
        [JsonPropertyName("was")]       public CoreXEcuWasDto Was { get; set; }
        [JsonPropertyName("gps")]       public CoreXEcuGpsDto Gps { get; set; }
        [JsonPropertyName("can")]       public CoreXEcuCanDto Can { get; set; }
        [JsonPropertyName("autosteer")] public CoreXEcuAutosteerDto Autosteer { get; set; }
        [JsonPropertyName("motor")]     public CoreXEcuMotorDto Motor { get; set; }

        public CoreXEcuStatusDto()
        {
            Source = "stub";
            ErrorCode = "";
            Error = "";
            ErrorTechnical = "";
            Firmware = "";
            Version = "";
            Ip = "";
            Imu = new CoreXEcuImuDto();
            Was = new CoreXEcuWasDto();
            Gps = new CoreXEcuGpsDto();
            Can = new CoreXEcuCanDto();
            Autosteer = new CoreXEcuAutosteerDto();
            Motor = new CoreXEcuMotorDto();
        }
    }

    // ====================== /api/params ======================================
    // Spec del firmware: estructura ANIDADA (autoZero, keya, imu) — todos los
    // sub-campos camelCase. Para POST aceptamos un objeto FLAT (claves sueltas)
    // que el firmware enrutea internamente a su grupo correspondiente.

    public sealed class CoreXEcuParamsAutoZeroDto
    {
        [JsonPropertyName("useBno")]     public int UseBno { get; set; }
        [JsonPropertyName("useGps")]     public int UseGps { get; set; }
        [JsonPropertyName("beta")]       public double Beta { get; set; }
        [JsonPropertyName("speedMin")]   public double SpeedMin { get; set; }
        [JsonPropertyName("yawRateMax")] public double YawRateMax { get; set; }
        [JsonPropertyName("gpsHdgMax")]  public double GpsHdgMax { get; set; }
        [JsonPropertyName("timeSlowMs")] public int TimeSlowMs { get; set; }
        [JsonPropertyName("timeFastMs")] public int TimeFastMs { get; set; }
        [JsonPropertyName("speedSlow")]  public double SpeedSlow { get; set; }
        [JsonPropertyName("speedFast")]  public double SpeedFast { get; set; }
    }

    public sealed class CoreXEcuParamsKeyaDto
    {
        [JsonPropertyName("ticksPerDeg")] public double TicksPerDeg { get; set; }
    }

    public sealed class CoreXEcuParamsImuDto
    {
        [JsonPropertyName("emaYaw")]   public double EmaYaw { get; set; }
        [JsonPropertyName("emaRoll")]  public double EmaRoll { get; set; }
        [JsonPropertyName("emaPitch")] public double EmaPitch { get; set; }
        [JsonPropertyName("emaStop")]  public double EmaStop { get; set; }
    }

    public sealed class CoreXEcuParamsDto
    {
        // wrapper del Hub
        [JsonPropertyName("ok")]              public bool Ok { get; set; }
        [JsonPropertyName("error_code")]      public string ErrorCode { get; set; }
        [JsonPropertyName("error")]           public string Error { get; set; }

        [JsonPropertyName("autoZero")] public CoreXEcuParamsAutoZeroDto AutoZero { get; set; }
        [JsonPropertyName("keya")]     public CoreXEcuParamsKeyaDto Keya { get; set; }
        [JsonPropertyName("imu")]      public CoreXEcuParamsImuDto Imu { get; set; }

        public CoreXEcuParamsDto()
        {
            ErrorCode = "";
            Error = "";
            AutoZero = new CoreXEcuParamsAutoZeroDto();
            Keya = new CoreXEcuParamsKeyaDto();
            Imu = new CoreXEcuParamsImuDto();
        }
    }

    /// <summary>Respuesta de POST /api/params — el firmware indica qué grupo
    /// de EEPROM se re-escribió. Si un campo viene fuera de rango se ignora
    /// silenciosamente y el flag updated.* queda false para ese grupo.</summary>
    public sealed class CoreXEcuParamsUpdatedDto
    {
        [JsonPropertyName("autoZero")] public bool AutoZero { get; set; }
        [JsonPropertyName("keya")]     public bool Keya { get; set; }
        [JsonPropertyName("imu")]      public bool Imu { get; set; }
    }

    public sealed class CoreXEcuParamsUpdateResultDto
    {
        [JsonPropertyName("ok")]      public bool Ok { get; set; }
        [JsonPropertyName("updated")] public CoreXEcuParamsUpdatedDto Updated { get; set; }
        [JsonPropertyName("error_code")] public string ErrorCode { get; set; }
        [JsonPropertyName("error")]      public string Error { get; set; }

        public CoreXEcuParamsUpdateResultDto()
        {
            Updated = new CoreXEcuParamsUpdatedDto();
            ErrorCode = "";
            Error = "";
        }
    }

    // ====================== /api/wassrc (v1.11+) ============================
    // GET  /api/wassrc → { source, ads_present, ads_raw, options[] }
    // POST /api/wassrc { source: "keya"|"ads_se"|"ads_diff" } →
    //                  { ok, source, ads_present, probed } | 400 invalid_source

    public sealed class CoreXEcuWassrcDto
    {
        // wrapper Hub
        [JsonPropertyName("ok")]          public bool Ok { get; set; }
        [JsonPropertyName("error_code")]  public string ErrorCode { get; set; }
        [JsonPropertyName("error")]       public string Error { get; set; }

        [JsonPropertyName("source")]      public string Source { get; set; }
        [JsonPropertyName("ads_present")] public bool AdsPresent { get; set; }
        [JsonPropertyName("ads_raw")]     public int AdsRaw { get; set; }
        /// <summary>Lista de valores aceptados que el firmware reporta — la UI
        /// la usa para no hardcodear el set y soportar nuevas fuentes futuras.</summary>
        [JsonPropertyName("options")]     public List<string> Options { get; set; }

        public CoreXEcuWassrcDto()
        {
            ErrorCode = "";
            Error = "";
            Source = "";
            Options = new List<string>();
        }
    }

    /// <summary>Body de POST /api/wassrc.</summary>
    public sealed class CoreXEcuWassrcRequestDto
    {
        [JsonPropertyName("source")] public string Source { get; set; }

        public CoreXEcuWassrcRequestDto() { Source = ""; }
    }

    /// <summary>Respuesta de POST /api/wassrc. En éxito el firmware confirma
    /// la fuente activa + reporta el probe del ADS (si la nueva fuente es ADS).
    /// En 400 (invalid_source) `Options` lista los valores aceptados.</summary>
    public sealed class CoreXEcuWassrcUpdateResultDto
    {
        [JsonPropertyName("ok")]          public bool Ok { get; set; }
        [JsonPropertyName("source")]      public string Source { get; set; }
        [JsonPropertyName("ads_present")] public bool AdsPresent { get; set; }
        /// <summary>True si el firmware corrió el probe I²C en este POST
        /// (solo cuando la fuente nueva es ads_se / ads_diff).</summary>
        [JsonPropertyName("probed")]      public bool Probed { get; set; }
        [JsonPropertyName("error_code")]  public string ErrorCode { get; set; }
        [JsonPropertyName("error")]       public string Error { get; set; }
        [JsonPropertyName("options")]     public List<string> Options { get; set; }

        public CoreXEcuWassrcUpdateResultDto()
        {
            ErrorCode = "";
            Error = "";
            Source = "";
            Options = new List<string>();
        }
    }

    // ====================== /api/zero ========================================

    public sealed class CoreXEcuZeroResultDto
    {
        [JsonPropertyName("ok")]        public bool Ok { get; set; }
        [JsonPropertyName("zeroTicks")] public int ZeroTicks { get; set; }
        [JsonPropertyName("error_code")] public string ErrorCode { get; set; }
        [JsonPropertyName("error")]      public string Error { get; set; }

        public CoreXEcuZeroResultDto()
        {
            ErrorCode = "";
            Error = "";
        }
    }

    // ====================== /api/motor/* (v1.09+) ===========================

    /// <summary>Body de POST /api/motor/test. PWM se clampa a ±200 en firmware.
    /// duration_ms es el dead-man: el motor se frena solo si el Hub deja de
    /// re-postear antes de que expire (default 1000, rango 100..5000).</summary>
    public sealed class CoreXEcuMotorTestRequestDto
    {
        [JsonPropertyName("pwm")]         public int Pwm { get; set; }
        [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }

        public CoreXEcuMotorTestRequestDto() { DurationMs = 1000; }
    }

    public sealed class CoreXEcuMotorTestResultDto
    {
        [JsonPropertyName("ok")]           public bool Ok { get; set; }
        [JsonPropertyName("pwm")]          public int Pwm { get; set; }
        [JsonPropertyName("duration_ms")]  public int DurationMs { get; set; }
        [JsonPropertyName("limit")]        public int Limit { get; set; }
        // 409 → guidance_active. error/detail vienen del body de error del firmware.
        [JsonPropertyName("error_code")]   public string ErrorCode { get; set; }
        [JsonPropertyName("error")]        public string Error { get; set; }
        [JsonPropertyName("detail")]       public string Detail { get; set; }

        public CoreXEcuMotorTestResultDto()
        {
            ErrorCode = "";
            Error = "";
            Detail = "";
        }
    }

    /// <summary>Respuesta genérica { ok } usada por motor/stop, sweep DELETE, reboot.</summary>
    public sealed class CoreXEcuOkResultDto
    {
        [JsonPropertyName("ok")]         public bool Ok { get; set; }
        [JsonPropertyName("error_code")] public string ErrorCode { get; set; }
        [JsonPropertyName("error")]      public string Error { get; set; }
        [JsonPropertyName("detail")]     public string Detail { get; set; }

        public CoreXEcuOkResultDto()
        {
            ErrorCode = "";
            Error = "";
            Detail = "";
        }
    }

    // ====================== /api/firmware (OTA Teensy) ======================
    // Request de la UI al Hub para flashear una versión cacheada a la unidad.
    public sealed class CoreXEcuFlashRequestDto
    {
        [JsonPropertyName("version")] public string Version { get; set; }
    }

    // Resultado del flasheo. El firmware responde 200 antes del flash_move()+reboot;
    // la verificación de que arrancó la versión nueva la hace la UI releyendo /status.
    public sealed class CoreXEcuFlashResultDto
    {
        [JsonPropertyName("ok")]         public bool Ok { get; set; }
        [JsonPropertyName("version")]    public string Version { get; set; }
        [JsonPropertyName("bytes_sent")] public long BytesSent { get; set; }
        [JsonPropertyName("error_code")] public string ErrorCode { get; set; }
        [JsonPropertyName("error")]      public string Error { get; set; }
        [JsonPropertyName("detail")]     public string Detail { get; set; }

        public CoreXEcuFlashResultDto()
        {
            Version = "";
            ErrorCode = "";
            Error = "";
            Detail = "";
        }
    }

    // ====================== /api/calibration/pwm-sweep (v1.10+) ============

    /// <summary>Body del POST que arranca el sweep. Ambos opcionales:
    /// step_duration_ms 500..3000 (default 1500), settle_ms 100..2000 (default 400).</summary>
    public sealed class CoreXEcuSweepStartRequestDto
    {
        [JsonPropertyName("step_duration_ms")] public int StepDurationMs { get; set; }
        [JsonPropertyName("settle_ms")]        public int SettleMs { get; set; }

        public CoreXEcuSweepStartRequestDto()
        {
            StepDurationMs = 1500;
            SettleMs = 400;
        }
    }

    /// <summary>Respuesta 202 del POST start: el firmware confirma que arrancó y
    /// devuelve el plan (cantidad de pasos + tiempo estimado total).</summary>
    public sealed class CoreXEcuSweepStartResultDto
    {
        [JsonPropertyName("ok")]               public bool Ok { get; set; }
        [JsonPropertyName("started")]          public bool Started { get; set; }
        [JsonPropertyName("step_count")]       public int StepCount { get; set; }
        [JsonPropertyName("step_duration_ms")] public int StepDurationMs { get; set; }
        [JsonPropertyName("settle_ms")]        public int SettleMs { get; set; }
        [JsonPropertyName("estimated_ms")]     public int EstimatedMs { get; set; }
        [JsonPropertyName("error_code")]       public string ErrorCode { get; set; }
        [JsonPropertyName("error")]            public string Error { get; set; }
        [JsonPropertyName("detail")]           public string Detail { get; set; }

        public CoreXEcuSweepStartResultDto()
        {
            ErrorCode = "";
            Error = "";
            Detail = "";
        }
    }

    /// <summary>Un paso del sweep — measured=false significa "todavía no ejecutado"
    /// (o abortado mid-sweep por guidance). deg_per_sec ya viene calculado por el
    /// firmware: delta_ticks * 1000 / duration_ms / ticks_per_deg.</summary>
    public sealed class CoreXEcuSweepStepDto
    {
        [JsonPropertyName("pwm")]         public int Pwm { get; set; }
        [JsonPropertyName("measured")]    public bool Measured { get; set; }
        [JsonPropertyName("delta_ticks")] public int DeltaTicks { get; set; }
        [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
        [JsonPropertyName("deg_per_sec")] public double DegPerSec { get; set; }
    }

    /// <summary>GET /api/calibration/pwm-sweep — pollear durante "running" y
    /// pintar tabla. state = "idle" | "running" | "done".</summary>
    public sealed class CoreXEcuSweepStatusDto
    {
        // wrapper Hub
        [JsonPropertyName("ok")]               public bool Ok { get; set; }
        [JsonPropertyName("error_code")]       public string ErrorCode { get; set; }
        [JsonPropertyName("error")]            public string Error { get; set; }

        [JsonPropertyName("state")]            public string State { get; set; }
        [JsonPropertyName("current_step")]     public int CurrentStep { get; set; }
        [JsonPropertyName("total_steps")]      public int TotalSteps { get; set; }
        [JsonPropertyName("step_duration_ms")] public int StepDurationMs { get; set; }
        [JsonPropertyName("settle_ms")]        public int SettleMs { get; set; }
        [JsonPropertyName("ticks_per_deg")]    public double TicksPerDeg { get; set; }
        [JsonPropertyName("results")]          public List<CoreXEcuSweepStepDto> Results { get; set; }

        public CoreXEcuSweepStatusDto()
        {
            State = "idle";
            ErrorCode = "";
            Error = "";
            Results = new List<CoreXEcuSweepStepDto>();
        }
    }
}
