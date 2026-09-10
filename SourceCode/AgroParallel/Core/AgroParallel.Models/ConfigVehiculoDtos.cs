// ============================================================================
// ConfigVehiculoDtos.cs — DTOs de la página pages/config.html (réplica HTML de
// FormConfig). Convenciones de wire (AgpJson → snake_case):
//   · Distancias SIEMPRE en METROS (con signo cuando aplica), velocidades en
//     km/h, tiempos en segundos — igual que los settings persistidos de PilotX.
//     La conversión cm/in y m/ft la hace el JS según is_metric.
//   · Signos: antenna_offset + = izquierda; hitch_length − = atrás;
//     tool_offset + = derecha; tool_overlap + = overlap / − = gap;
//     trailing_tool_to_pivot_length + = pivote detrás.
// ============================================================================

namespace AgroParallel.Models
{
    /// <summary>Snapshot completo de FormConfig en unidades persistidas.</summary>
    public class ConfigSnapshotDto
    {
        public bool IsMetric { get; set; }
        public bool IsJobStarted { get; set; }
        public string PerfilActivo { get; set; }

        public ConfigVehiculoSec Vehiculo { get; set; }
        public ConfigDimensionesSec Dimensiones { get; set; }
        public ConfigAntenaSec Antena { get; set; }
        public ConfigEngancheSec Enganche { get; set; }
        public ConfigOffsetSec Offset { get; set; }
        public ConfigTimingSec Timing { get; set; }
        public ConfigSeccionesSec Secciones { get; set; }
        public ConfigSwitchesSec Switches { get; set; }
        public ConfigRelaySec Relay { get; set; }
        public ConfigMaquinaSec Maquina { get; set; }
        public ConfigRumboSec Rumbo { get; set; }
        public ConfigRolidoSec Rolido { get; set; }
        public ConfigUturnSec Uturn { get; set; }
        public ConfigTramSec Tram { get; set; }
        public ConfigDisplaySec Display { get; set; }
        public ConfigBotonesSec Botones { get; set; }
    }

    public class ConfigVehiculoSec
    {
        public int VehicleType { get; set; }          // 0 tractor / 1 cosechadora / 2 articulado
        public string TractorBrand { get; set; }      // nombre del enum (AGOpenGPS, Case, …)
        public string HarvesterBrand { get; set; }
        public string ArticulatedBrand { get; set; }
        public bool IsVehicleImage { get; set; }
        public int Opacity { get; set; }              // 20..100, saltos de 20
    }

    public class ConfigDimensionesSec
    {
        public double Wheelbase { get; set; }         // m
        public double TrackWidth { get; set; }        // m
        public double HitchLength { get; set; }       // m, con signo (− = atrás)
    }

    public class ConfigAntenaSec
    {
        public double AntennaHeight { get; set; }     // m
        public double AntennaPivot { get; set; }      // m, con signo
        public double AntennaOffset { get; set; }     // m, + izquierda / − derecha
    }

    public class ConfigEngancheSec
    {
        public string Estilo { get; set; }            // front | tbt | trailing | rear
        public double HitchLength { get; set; }       // m con signo
        public double TrailingHitchLength { get; set; }      // m, ≤ 0
        public double TankTrailingHitchLength { get; set; }  // m, ≤ 0
    }

    public class ConfigOffsetSec
    {
        public double ToolOffset { get; set; }        // m, + derecha / − izquierda
        public double ToolOverlap { get; set; }       // m, + overlap / − gap
        public double TrailingToolToPivotLength { get; set; } // m, + pivote detrás
    }

    public class ConfigTimingSec
    {
        public double LookAheadOn { get; set; }       // s, 0.2..22
        public double LookAheadOff { get; set; }      // s, 0..20, ≤ 0.8×On
        public double TurnOffDelay { get; set; }      // s, 0..10, excluyente con Off
    }

    public class ConfigSeccionesSec
    {
        public bool IsSectionsNotZones { get; set; }  // true = secciones individuales
        public int MaxSections { get; set; }          // tope del modo zonas (64)
        // Modo secciones individuales
        public int NumSections { get; set; }          // 1..16
        public double DefaultSectionWidth { get; set; }   // m
        public double[] SectionWidths { get; set; }   // 16 anchos en m (derivados de positions)
        // Modo zonas / simétrico
        public int NumSectionsMulti { get; set; }     // 1..64
        public double SectionWidthMulti { get; set; } // m (ancho uniforme)
        public int Zones { get; set; }                // 2..8
        public int[] ZoneRanges { get; set; }         // 8 valores: sección donde TERMINA cada zona
        // Comunes
        public bool IsSectionOffWhenOut { get; set; }
        public double SlowSpeedCutoff { get; set; }   // km/h SIEMPRE
        public int MinCoverage { get; set; }          // %
        public double ToolWidth { get; set; }         // m (ancho total runtime)
    }

    public class ConfigSwitchesSec
    {
        public bool WorkEnabled { get; set; }
        public bool WorkActiveLow { get; set; }
        public bool WorkManualSections { get; set; }  // true manual / false auto
        public bool SteerEnabled { get; set; }
        public bool SteerManualSections { get; set; }
        // ToolX: switch de trabajo inalámbrico (PGN 253 origen 0x7C).
        //   work_toolx_enabled: el perfil acepta sus frames (RUNTIME, _engine.Mc).
        //   work_toolx_alive:   RUNTIME, sólo lectura — habilitado Y con frame hace menos
        //                       de CModuleComm.ToolXTimeoutSec (5 s): "ToolX manda".
        public bool WorkToolxEnabled { get; set; }
        public bool WorkToolxAlive { get; set; }
    }

    public class ConfigRelaySec
    {
        // 24 pines; valor = función (0="-", 1..16 secciones, 17 HydUp, 18 HydDown,
        // 19 TramRight, 20 TramLeft, 21 GeoStop). Guardar = Send+Save (PGN 236).
        public int[] Pins { get; set; }
    }

    public class ConfigMaquinaSec
    {
        public bool InvertRelays { get; set; }
        public bool HydOn { get; set; }
        public int RaiseTime { get; set; }            // s, 1..255
        public int LowerTime { get; set; }            // s, 1..255
        public double HydLiftLookAhead { get; set; }  // s, 1..20 (no viaja en PGN)
        public int User1 { get; set; }                // 0..255
        public int User2 { get; set; }
        public int User3 { get; set; }
        public int User4 { get; set; }
    }

    public class ConfigRumboSec
    {
        public string HeadingSource { get; set; }     // "Fix" | "Dual"
        public bool MinGpsStep { get; set; }          // true = 10 cm (1.0) / false = 5 cm (0.5)
        public int Fusion { get; set; }               // barra 5..60 (weight = v×0.002)
        public bool IsRtk { get; set; }
        public bool IsRtkKillAutosteer { get; set; }
        public int JumpFixDistance { get; set; }      // cm, 0 = off
        public double DualHeadingOffset { get; set; } // grados
        public double DualReverseDistance { get; set; } // m, 0.1..0.9
        public bool ReverseOn { get; set; }
        public bool CurveSpeedComp { get; set; }      // velocidad por sección en curva (setTool_isCurveSpeedComp)
        public bool AutoSwitchDualFix { get; set; }
        public double AutoSwitchSpeed { get; set; }   // km/h SIEMPRE, 1..10
        public bool ImuPresent { get; set; }          // runtime: imuHeading != 99999
    }

    public class ConfigRolidoSec
    {
        public double RollZero { get; set; }          // grados (offset actual)
        public int RollFilter { get; set; }           // barra 0..98 (filter = v×0.01)
        public bool InvertRoll { get; set; }
        public bool ImuPresent { get; set; }          // runtime: imuRoll != 88888
        public double ImuRoll { get; set; }           // roll vivo (88888 = sin dato)
    }

    public class ConfigUturnSec
    {
        public double Radius { get; set; }            // m, ≥ 2
        public double DistanceFromBoundary { get; set; } // m, ≥ 0.2
        public int ExtensionLength { get; set; }      // m, 3..50
        public int Smoothing { get; set; }            // 8..50, paso 2
    }

    public class ConfigTramSec
    {
        public double TramWidth { get; set; }         // m
        public bool DisplayTramControl { get; set; }
        public bool OuterInverted { get; set; }
    }

    public class ConfigDisplaySec
    {
        public bool IsMetric { get; set; }            // ¡cambiarlo recarga todo!
        public bool Brightness { get; set; }
        public bool Floor { get; set; }
        public bool Grid { get; set; }
        public bool Speedo { get; set; }
        public bool StartFullScreen { get; set; }
        public bool SvennArrow { get; set; }
        public bool ExtraGuides { get; set; }
        public bool Polygons { get; set; }
        public bool Keyboard { get; set; }
        public bool LogElevation { get; set; }
        public bool DirectionMarkers { get; set; }
        public bool SectionLines { get; set; }
        public bool LineSmooth { get; set; }
        public bool HeadlandDistance { get; set; }
        public int NumGuideLines { get; set; }        // 1..5000
    }

    public class ConfigBotonesSec
    {
        // Features (setFeatures.*)
        public bool FeatureTram { get; set; }
        public bool FeatureHeadland { get; set; }
        public bool FeatureBoundary { get; set; }
        public bool FeatureRecPath { get; set; }
        public bool FeatureAbSmooth { get; set; }
        public bool FeatureHideContour { get; set; }
        public bool FeatureWebcam { get; set; }
        public bool FeatureOffsetFix { get; set; }
        public bool FeatureUturn { get; set; }
        public bool FeatureLateral { get; set; }
        public bool FeatureNudge { get; set; }        // mapea a setFeatures.isABLineOn
        // Sonidos
        public bool SoundSteer { get; set; }
        public bool SoundTurn { get; set; }
        public bool SoundHydLift { get; set; }
        public bool SoundSections { get; set; }
        // Sistema (el bridge de comunicaciones CoreX)
        public bool AutoStartCorex { get; set; }
        public bool AutoOffCorex { get; set; }
        public bool ShutdownNoPower { get; set; }
        public bool HardwareMessages { get; set; }
    }

    /// <summary>
    /// Body único de POST /aog/config/{seccion}: todos los campos nullable,
    /// cada sección usa solo los suyos (null = mantener valor actual).
    /// </summary>
    public class ConfigGuardarBody
    {
        // vehiculo
        public int? VehicleType { get; set; }
        public string TractorBrand { get; set; }
        public string HarvesterBrand { get; set; }
        public string ArticulatedBrand { get; set; }
        public bool? IsVehicleImage { get; set; }
        public int? Opacity { get; set; }
        // dimensiones
        public double? Wheelbase { get; set; }
        public double? TrackWidth { get; set; }
        public double? HitchLength { get; set; }
        // antena
        public double? AntennaHeight { get; set; }
        public double? AntennaPivot { get; set; }
        public double? AntennaOffset { get; set; }
        // enganche
        public string Estilo { get; set; }
        public double? TrailingHitchLength { get; set; }
        public double? TankTrailingHitchLength { get; set; }
        // offset / pivote
        public double? ToolOffset { get; set; }
        public double? ToolOverlap { get; set; }
        public double? TrailingToolToPivotLength { get; set; }
        // timing
        public double? LookAheadOn { get; set; }
        public double? LookAheadOff { get; set; }
        public double? TurnOffDelay { get; set; }
        // secciones
        public bool? IsSectionsNotZones { get; set; }
        public int? NumSections { get; set; }
        public double? DefaultSectionWidth { get; set; }
        public double[] SectionWidths { get; set; }
        public int? NumSectionsMulti { get; set; }
        public double? SectionWidthMulti { get; set; }
        public int? Zones { get; set; }
        public int[] ZoneRanges { get; set; }
        public bool? IsSectionOffWhenOut { get; set; }
        public double? SlowSpeedCutoff { get; set; }
        public int? MinCoverage { get; set; }
        // switches
        public bool? WorkEnabled { get; set; }
        public bool? WorkActiveLow { get; set; }
        public bool? WorkManualSections { get; set; }
        public bool? SteerEnabled { get; set; }
        public bool? SteerManualSections { get; set; }
        public bool? WorkToolxEnabled { get; set; }
        // relay
        public int[] Pins { get; set; }
        // maquina
        public bool? InvertRelays { get; set; }
        public bool? HydOn { get; set; }
        public int? RaiseTime { get; set; }
        public int? LowerTime { get; set; }
        public double? HydLiftLookAhead { get; set; }
        public int? User1 { get; set; }
        public int? User2 { get; set; }
        public int? User3 { get; set; }
        public int? User4 { get; set; }
        // rumbo
        public string HeadingSource { get; set; }
        public bool? MinGpsStep { get; set; }
        public int? Fusion { get; set; }
        public bool? IsRtk { get; set; }
        public bool? IsRtkKillAutosteer { get; set; }
        public int? JumpFixDistance { get; set; }
        public double? DualHeadingOffset { get; set; }
        public double? DualReverseDistance { get; set; }
        public bool? ReverseOn { get; set; }
        public bool? CurveSpeedComp { get; set; }
        public bool? AutoSwitchDualFix { get; set; }
        public double? AutoSwitchSpeed { get; set; }
        // rolido
        public int? RollFilter { get; set; }
        public bool? InvertRoll { get; set; }
        // uturn
        public double? Radius { get; set; }
        public double? DistanceFromBoundary { get; set; }
        public int? ExtensionLength { get; set; }
        public int? Smoothing { get; set; }
        // tram
        public double? TramWidth { get; set; }
        public bool? DisplayTramControl { get; set; }
        public bool? OuterInverted { get; set; }
        // display
        public bool? IsMetric { get; set; }
        public bool? Brightness { get; set; }
        public bool? Floor { get; set; }
        public bool? Grid { get; set; }
        public bool? Speedo { get; set; }
        public bool? StartFullScreen { get; set; }
        public bool? SvennArrow { get; set; }
        public bool? ExtraGuides { get; set; }
        public bool? Polygons { get; set; }
        public bool? Keyboard { get; set; }
        public bool? LogElevation { get; set; }
        public bool? DirectionMarkers { get; set; }
        public bool? SectionLines { get; set; }
        public bool? LineSmooth { get; set; }
        public bool? HeadlandDistance { get; set; }
        public int? NumGuideLines { get; set; }
        // botones
        public bool? FeatureTram { get; set; }
        public bool? FeatureHeadland { get; set; }
        public bool? FeatureBoundary { get; set; }
        public bool? FeatureRecPath { get; set; }
        public bool? FeatureAbSmooth { get; set; }
        public bool? FeatureHideContour { get; set; }
        public bool? FeatureWebcam { get; set; }
        public bool? FeatureOffsetFix { get; set; }
        public bool? FeatureUturn { get; set; }
        public bool? FeatureLateral { get; set; }
        public bool? FeatureNudge { get; set; }
        public bool? SoundSteer { get; set; }
        public bool? SoundTurn { get; set; }
        public bool? SoundHydLift { get; set; }
        public bool? SoundSections { get; set; }
        public bool? AutoStartCorex { get; set; }
        public bool? AutoOffCorex { get; set; }
        public bool? ShutdownNoPower { get; set; }
        public bool? HardwareMessages { get; set; }
    }

    public class ConfigResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }

        public static ConfigResultDto Exito() { return new ConfigResultDto { Ok = true }; }
        public static ConfigResultDto Falla(string error) { return new ConfigResultDto { Ok = false, Error = error }; }
    }

    /// <summary>Resultado de las acciones live de rolido (zero/quitar/subir/bajar/reset_imu).</summary>
    public class ConfigRolidoResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public double RollZero { get; set; }
        public double ImuRoll { get; set; }     // 88888 = sin dato
        public bool ImuPresent { get; set; }
    }
}
