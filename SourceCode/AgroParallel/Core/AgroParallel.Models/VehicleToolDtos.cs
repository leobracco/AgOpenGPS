// ============================================================================
// VehicleToolDtos.cs
// DTOs para la edición HTML de configuración de Vehículo y Herramienta de PilotX.
// Se serializan/deserializan con System.Text.Json (no Swan). Las claves usan
// camelCase porque así las consume el JS; el adaptador PilotX-side las mapea a
// Properties.Settings.Default.setVehicle_*/setTool_*.
//
// Subset deliberado: solo los campos que realmente edita el operario de
// cabina. Los gains de autosteer y los colores de sección viven en otras
// pantallas (Settings misc / piloto).
// ============================================================================

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class VehicleConfigDto
    {
        /// <summary>0=Tractor, 1=Harvester, 2=Articulated. Mapea VehicleType de PilotX.</summary>
        [JsonPropertyName("vehicleType")]
        public int VehicleType { get; set; }

        /// <summary>Distancia entre eje delantero y trasero (m).</summary>
        [JsonPropertyName("wheelbase")]
        public double Wheelbase { get; set; }

        /// <summary>Ancho de trocha (m).</summary>
        [JsonPropertyName("trackWidth")]
        public double TrackWidth { get; set; }

        /// <summary>Altura de antena GPS sobre el piso (m).</summary>
        [JsonPropertyName("antennaHeight")]
        public double AntennaHeight { get; set; }

        /// <summary>Pivote eje trasero ↔ antena, hacia adelante + (m).</summary>
        [JsonPropertyName("antennaPivot")]
        public double AntennaPivot { get; set; }

        /// <summary>Offset lateral antena (m, + derecha).</summary>
        [JsonPropertyName("antennaOffset")]
        public double AntennaOffset { get; set; }

        /// <summary>Ángulo máximo de dirección (°).</summary>
        [JsonPropertyName("maxSteerAngle")]
        public double MaxSteerAngle { get; set; }

        /// <summary>Velocidad mínima para activar autosteer (km/h).</summary>
        [JsonPropertyName("slowSpeedCutoff")]
        public double SlowSpeedCutoff { get; set; }
    }

    public sealed class ToolConfigDto
    {
        /// <summary>Ancho total de la herramienta (m).</summary>
        [JsonPropertyName("width")]
        public double Width { get; set; }

        /// <summary>Solape entre pasadas (m).</summary>
        [JsonPropertyName("overlap")]
        public double Overlap { get; set; }

        /// <summary>Offset lateral de la herramienta (m, + derecha).</summary>
        [JsonPropertyName("offset")]
        public double Offset { get; set; }

        /// <summary>Cantidad de secciones efectiva (modo secciones [1..16], modo zonas [1..64]).</summary>
        [JsonPropertyName("numSections")]
        public int NumSections { get; set; }

        /// <summary>true = modo secciones individuales (≤16, ancho propio);
        /// false = modo zonas (≤64 secciones iguales agrupadas).</summary>
        [JsonPropertyName("isSectionsNotZones")]
        public bool IsSectionsNotZones { get; set; } = true;

        /// <summary>Modo secciones: ancho de cada sección (m), largo = NumSections.
        /// Deriva de setSection_position1..17. null/vacío ⇒ reparto igual de Width.</summary>
        [JsonPropertyName("sectionWidths")]
        public double[] SectionWidths { get; set; }

        /// <summary>Modo zonas: ancho de cada sección (m, todas iguales).</summary>
        [JsonPropertyName("sectionWidthMulti")]
        public double SectionWidthMulti { get; set; }

        /// <summary>Modo zonas: cantidad de zonas [1..8].</summary>
        [JsonPropertyName("zones")]
        public int Zones { get; set; }

        /// <summary>Modo zonas: última sección de cada zona (largo = Zones,
        /// ascendente, la última debe ser NumSections). Mapea setTool_zones.</summary>
        [JsonPropertyName("zoneRanges")]
        public int[] ZoneRanges { get; set; }

        /// <summary>Largo enganche del tractor (m).</summary>
        [JsonPropertyName("hitchLength")]
        public double HitchLength { get; set; }

        /// <summary>Largo del enganche colgante (m) — solo si IsToolTrailing.</summary>
        [JsonPropertyName("trailingHitchLength")]
        public double TrailingHitchLength { get; set; }

        /// <summary>Largo herramienta ↔ pivote (m).</summary>
        [JsonPropertyName("trailingToolToPivotLength")]
        public double TrailingToolToPivotLength { get; set; }

        /// <summary>Lookahead encendido de secciones (s).</summary>
        [JsonPropertyName("lookAheadOn")]
        public double LookAheadOn { get; set; }

        /// <summary>Lookahead apagado de secciones (s).</summary>
        [JsonPropertyName("lookAheadOff")]
        public double LookAheadOff { get; set; }

        /// <summary>Delay extra al apagar secciones (s).</summary>
        [JsonPropertyName("turnOffDelay")]
        public double TurnOffDelay { get; set; }

        /// <summary>Trailing (rastra/sembradora colgada con enganche).</summary>
        [JsonPropertyName("isToolTrailing")]
        public bool IsToolTrailing { get; set; }

        /// <summary>Tool-behind-tool (carro tanque tras rastra).</summary>
        [JsonPropertyName("isToolTBT")]
        public bool IsToolTBT { get; set; }

        /// <summary>Rígida trasera (sembradora montada).</summary>
        [JsonPropertyName("isToolRearFixed")]
        public bool IsToolRearFixed { get; set; }

        /// <summary>Rígida delantera (pala frontal).</summary>
        [JsonPropertyName("isToolFrontFixed")]
        public bool IsToolFrontFixed { get; set; }

        /// <summary>Apagar secciones al salir del lote/cabecera.</summary>
        [JsonPropertyName("isSectionOffWhenOut")]
        public bool IsSectionOffWhenOut { get; set; }
    }

    /// <summary>
    /// Config IMU / fuente de rumbo (solapas WinForms tabDHeading + tabDRoll).
    /// Los ángulos van en grados; velocidades en km/h; porcentajes 0..100.
    /// </summary>
    public sealed class ImuConfigDto
    {
        /// <summary>"Fix" (GPS simple) o "Dual" (antena dual). setGPS_headingFromWhichSource.</summary>
        [JsonPropertyName("headingSource")]
        public string HeadingSource { get; set; }

        /// <summary>Peso GPS en la fusión GPS/IMU (0..100 %). setIMU_fusionWeight2 = v*0.002.</summary>
        [JsonPropertyName("fusionGpsPercent")]
        public int FusionGpsPercent { get; set; }

        /// <summary>true = paso mínimo 10 cm (heading step 1 m); false = 5 cm (0.5 m).</summary>
        [JsonPropertyName("minGpsStep10cm")]
        public bool MinGpsStep10Cm { get; set; }

        /// <summary>Offset del rumbo dual (°). setGPS_dualHeadingOffset.</summary>
        [JsonPropertyName("dualHeadingOffset")]
        public double DualHeadingOffset { get; set; }

        /// <summary>Distancia detección reversa dual (m). setGPS_dualReverseDetectionDistance.</summary>
        [JsonPropertyName("dualReverseDistance")]
        public double DualReverseDistance { get; set; }

        /// <summary>Alarma al perder RTK. setGPS_isRTK.</summary>
        [JsonPropertyName("isRtkAlarm")]
        public bool IsRtkAlarm { get; set; }

        /// <summary>Cortar autosteer al perder RTK. setGPS_isRTK_KillAutoSteer.</summary>
        [JsonPropertyName("isRtkKillAutosteer")]
        public bool IsRtkKillAutosteer { get; set; }

        /// <summary>Alarma de salto de fix (m, 0 = off). setGPS_jumpFixAlarmDistance.</summary>
        [JsonPropertyName("fixJumpAlarmDistance")]
        public int FixJumpAlarmDistance { get; set; }

        /// <summary>Detección de reversa por IMU. setIMU_isReverseOn.</summary>
        [JsonPropertyName("isReverseOn")]
        public bool IsReverseOn { get; set; }

        /// <summary>Cambio automático Dual→Fix a baja velocidad. setAutoSwitchDualFixOn.</summary>
        [JsonPropertyName("autoSwitchDualFix")]
        public bool AutoSwitchDualFix { get; set; }

        /// <summary>Velocidad de cambio Dual↔Fix (km/h, 1..10). setAutoSwitchDualFixSpeed.</summary>
        [JsonPropertyName("autoSwitchDualFixSpeed")]
        public double AutoSwitchDualFixSpeed { get; set; }

        /// <summary>Filtro de roll (0..100 %). setIMU_rollFilter = v*0.01.</summary>
        [JsonPropertyName("rollFilterPercent")]
        public int RollFilterPercent { get; set; }

        /// <summary>Invertir signo del roll. setIMU_invertRoll.</summary>
        [JsonPropertyName("invertRoll")]
        public bool InvertRoll { get; set; }
    }

    /// <summary>Estado IMU en vivo para la UI (roll actual, cero, presencia de IMU).</summary>
    public sealed class ImuLiveDto
    {
        /// <summary>true si hay heading de IMU (imuHeading != 99999).</summary>
        [JsonPropertyName("hasImuHeading")]
        public bool HasImuHeading { get; set; }

        /// <summary>true si hay roll de IMU (imuRoll != 88888).</summary>
        [JsonPropertyName("hasImuRoll")]
        public bool HasImuRoll { get; set; }

        /// <summary>Roll actual corregido (°) — solo válido si hasImuRoll.</summary>
        [JsonPropertyName("imuRoll")]
        public double ImuRoll { get; set; }

        /// <summary>Offset de cero de roll vigente (°).</summary>
        [JsonPropertyName("rollZero")]
        public double RollZero { get; set; }
    }

    /// <summary>Bundle conveniente para GET conjunto de la pantalla "Vehículo".</summary>
    public sealed class VehicleToolBundleDto
    {
        [JsonPropertyName("vehicle")]
        public VehicleConfigDto Vehicle { get; set; }

        [JsonPropertyName("tool")]
        public ToolConfigDto Tool { get; set; }
    }
}
