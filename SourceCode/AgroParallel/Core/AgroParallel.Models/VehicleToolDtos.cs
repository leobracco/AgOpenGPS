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

        /// <summary>Cantidad de secciones [1..16].</summary>
        [JsonPropertyName("numSections")]
        public int NumSections { get; set; }

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

    /// <summary>Bundle conveniente para GET conjunto de la pantalla "Vehículo".</summary>
    public sealed class VehicleToolBundleDto
    {
        [JsonPropertyName("vehicle")]
        public VehicleConfigDto Vehicle { get; set; }

        [JsonPropertyName("tool")]
        public ToolConfigDto Tool { get; set; }
    }
}
