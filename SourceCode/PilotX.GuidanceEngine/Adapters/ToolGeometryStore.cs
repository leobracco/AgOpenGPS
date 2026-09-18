// ============================================================================
// ToolGeometryStore.cs — persistencia propia del motor headless para la
// GEOMETRÍA DEL IMPLEMENTO (ancho, cantidad y anchos de secciones, enganche,
// timing de corte).
//
// ¿Por qué existe? Porque en el motor headless `Properties.Settings` NO se
// guarda en ningún lado. En este fork `Settings` no es el user.config de .NET:
// es un POCO que se serializa al perfil de vehículo
// `<Documentos>\AgOpenGPS\Vehicles\<vehicleFileName>.XML`, y su `Save()`
// arranca con este guard (AgOpenGPS.Core/Properties/Settings.cs):
//
//     if (RegistrySettings.vehicleFileName != "") XmlSettingsHandler.SaveXMLFile(...)
//     else Log.EventWriter("Default Vehicle Not saved to Vehicles");
//
// El motor arranca con `aog_settings.json → vehicle_file_name: ""` (nadie lo
// setea: el diálogo "elegí un perfil" es de FormGPS, no existe headless), así
// que TODOS los `Settings.Default.Save()` del motor eran no-ops silenciosos y
// el `Settings.Default.Load()` del arranque devolvía `MissingFile` → defaults
// del código: 3 secciones de 4 m. Síntoma en cabina: el operario configuraba
// 14 secciones de 0,52 m, en caliente andaba, y al reiniciar el proceso volvía
// a 3 × 4 m. Perder la geometría del implemento en cada arranque es grave: el
// corte de secciones y la cobertura se calculan con ella.
//
// La solución es un archivo propio del motor (`GuidanceEngineData\tool.json`)
// que espeja los campos de geometría de `Settings`:
//   · se ESCRIBE en cada guardado de config (junto al `Settings.Save()` que
//     puede o no ser no-op — no lo tocamos, sigue funcionando cuando SÍ hay
//     perfil de vehículo elegido);
//   · se LEE en el arranque, después de `Settings.Default.Load()` y ANTES de
//     construir el GuidanceEngineHost (que arma CTool/CVehicle y llama a
//     AplicarGeometriaDeSecciones leyendo estos mismos campos).
//
// Si el archivo no existe no se toca nada: quedan los valores del perfil de
// vehículo (o los defaults del código, como antes).
//
// Vive en GuidanceEngineData\ a propósito: esa carpeta es del MOTOR y no la
// pisa el `dotnet publish` ni el ZIP de release, así que la config sobrevive
// tanto a un reinicio como a una actualización.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgLibrary.Logging;

namespace PilotX.GuidanceEngine.Adapters
{
    /// <summary>
    /// Espejo en disco de los campos de <c>Properties.Settings</c> que definen
    /// la geometría del implemento. Todos anulables: una clave ausente (archivo
    /// viejo, versión anterior) deja el valor que ya tenía Settings en vez de
    /// pisarlo con cero.
    /// </summary>
    public sealed class ToolGeometryFile
    {
        // --- implemento -----------------------------------------------------
        [JsonPropertyName("tool_width")] public double? ToolWidth { get; set; }
        [JsonPropertyName("tool_overlap")] public double? ToolOverlap { get; set; }
        [JsonPropertyName("tool_offset")] public double? ToolOffset { get; set; }

        // --- secciones individuales -----------------------------------------
        [JsonPropertyName("is_sections_not_zones")] public bool? IsSectionsNotZones { get; set; }
        [JsonPropertyName("num_sections")] public int? NumSections { get; set; }
        /// <summary>17 bordes (pos1..pos17), en metros, tal cual los guarda Settings.</summary>
        [JsonPropertyName("section_positions")] public decimal[] SectionPositions { get; set; }
        [JsonPropertyName("default_section_width")] public double? DefaultSectionWidth { get; set; }

        // --- zonas (modo multi) ---------------------------------------------
        [JsonPropertyName("num_sections_multi")] public int? NumSectionsMulti { get; set; }
        [JsonPropertyName("section_width_multi")] public double? SectionWidthMulti { get; set; }
        [JsonPropertyName("zones")] public string Zones { get; set; }
        [JsonPropertyName("is_multi_color_sections")] public bool? IsMultiColorSections { get; set; }

        // --- enganche --------------------------------------------------------
        [JsonPropertyName("hitch_length")] public double? HitchLength { get; set; }
        [JsonPropertyName("trailing_hitch_length")] public double? TrailingHitchLength { get; set; }
        [JsonPropertyName("tank_trailing_hitch_length")] public double? TankTrailingHitchLength { get; set; }
        [JsonPropertyName("trailing_tool_to_pivot_length")] public double? TrailingToolToPivotLength { get; set; }
        [JsonPropertyName("is_tool_trailing")] public bool? IsToolTrailing { get; set; }
        [JsonPropertyName("is_tool_tbt")] public bool? IsToolTbt { get; set; }
        [JsonPropertyName("is_tool_rear_fixed")] public bool? IsToolRearFixed { get; set; }
        [JsonPropertyName("is_tool_front")] public bool? IsToolFront { get; set; }

        // --- timing / corte ---------------------------------------------------
        [JsonPropertyName("look_ahead_on")] public double? LookAheadOn { get; set; }
        [JsonPropertyName("look_ahead_off")] public double? LookAheadOff { get; set; }
        [JsonPropertyName("turn_off_delay")] public double? TurnOffDelay { get; set; }
        [JsonPropertyName("is_section_off_when_out")] public bool? IsSectionOffWhenOut { get; set; }
        [JsonPropertyName("slow_speed_cutoff")] public double? SlowSpeedCutoff { get; set; }
        [JsonPropertyName("min_coverage")] public int? MinCoverage { get; set; }
    }

    public static class ToolGeometryStore
    {
        private const string NombreArchivo = "tool.json";

        private static string _ruta;

        /// <summary>
        /// Ruta del archivo. Por defecto <c>&lt;exe&gt;\GuidanceEngineData\tool.json</c>;
        /// Program.cs la fija explícitamente al mismo baseDirectory del host.
        /// </summary>
        public static string Ruta
        {
            get
            {
                if (string.IsNullOrEmpty(_ruta))
                    _ruta = Path.Combine(AppContext.BaseDirectory, "GuidanceEngineData", NombreArchivo);
                return _ruta;
            }
            set { _ruta = value; }
        }

        /// <summary>Fija la ruta a partir de la carpeta de datos del motor.</summary>
        public static void UsarCarpeta(string carpetaDatos)
        {
            if (!string.IsNullOrWhiteSpace(carpetaDatos))
                Ruta = Path.Combine(carpetaDatos, NombreArchivo);
        }

        /// <summary>
        /// Baja a disco la geometría actual de <c>Settings.Default</c>. Se llama
        /// junto a cada <c>Settings.Default.Save()</c> de los adaptadores de
        /// config: es el que realmente persiste cuando no hay perfil de vehículo.
        /// </summary>
        public static bool Guardar()
        {
            try
            {
                var s = global::AgOpenGPS.Properties.Settings.Default;
                var f = new ToolGeometryFile
                {
                    ToolWidth = s.setVehicle_toolWidth,
                    ToolOverlap = s.setVehicle_toolOverlap,
                    ToolOffset = s.setVehicle_toolOffset,

                    IsSectionsNotZones = s.setTool_isSectionsNotZones,
                    NumSections = s.setVehicle_numSections,
                    SectionPositions = LeerPosiciones(s),
                    DefaultSectionWidth = s.setTool_defaultSectionWidth,

                    NumSectionsMulti = s.setTool_numSectionsMulti,
                    SectionWidthMulti = s.setTool_sectionWidthMulti,
                    Zones = s.setTool_zones,
                    IsMultiColorSections = s.setColor_isMultiColorSections,

                    HitchLength = s.setVehicle_hitchLength,
                    TrailingHitchLength = s.setTool_toolTrailingHitchLength,
                    TankTrailingHitchLength = s.setVehicle_tankTrailingHitchLength,
                    TrailingToolToPivotLength = s.setTool_trailingToolToPivotLength,
                    IsToolTrailing = s.setTool_isToolTrailing,
                    IsToolTbt = s.setTool_isToolTBT,
                    IsToolRearFixed = s.setTool_isToolRearFixed,
                    IsToolFront = s.setTool_isToolFront,

                    LookAheadOn = s.setVehicle_toolLookAheadOn,
                    LookAheadOff = s.setVehicle_toolLookAheadOff,
                    TurnOffDelay = s.setVehicle_toolOffDelay,
                    IsSectionOffWhenOut = s.setTool_isSectionOffWhenOut,
                    SlowSpeedCutoff = s.setVehicle_slowSpeedCutoff,
                    MinCoverage = s.setVehicle_minCoverage
                };

                var opts = new JsonSerializerOptions { WriteIndented = true };
                AgroParallel.Common.AtomicJson.Write(Ruta, JsonSerializer.Serialize(f, opts));
                return true;
            }
            catch (Exception ex)
            {
                Log.EventWriter("ToolGeometryStore -> no se pudo guardar " + Ruta + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Aplica el archivo (si existe) sobre <c>Settings.Default</c>. Devuelve
        /// true si aplicó algo. Llamar DESPUÉS de <c>Settings.Default.Load()</c>
        /// y ANTES de construir el host: CTool/CVehicle leen estos campos en su
        /// constructor y AplicarGeometriaDeSecciones reparte el ancho con ellos.
        /// </summary>
        public static bool Cargar()
        {
            try
            {
                if (!File.Exists(Ruta)) return false;

                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var f = AgroParallel.Common.AtomicJson.Read<ToolGeometryFile>(Ruta, opts);
                if (f == null) return false;

                var s = global::AgOpenGPS.Properties.Settings.Default;

                if (f.ToolWidth.HasValue) s.setVehicle_toolWidth = f.ToolWidth.Value;
                if (f.ToolOverlap.HasValue) s.setVehicle_toolOverlap = f.ToolOverlap.Value;
                if (f.ToolOffset.HasValue) s.setVehicle_toolOffset = f.ToolOffset.Value;

                if (f.IsSectionsNotZones.HasValue) s.setTool_isSectionsNotZones = f.IsSectionsNotZones.Value;
                if (f.NumSections.HasValue) s.setVehicle_numSections = f.NumSections.Value;
                if (f.SectionPositions != null && f.SectionPositions.Length == 17)
                    EscribirPosiciones(s, f.SectionPositions);
                if (f.DefaultSectionWidth.HasValue) s.setTool_defaultSectionWidth = f.DefaultSectionWidth.Value;

                if (f.NumSectionsMulti.HasValue) s.setTool_numSectionsMulti = f.NumSectionsMulti.Value;
                if (f.SectionWidthMulti.HasValue) s.setTool_sectionWidthMulti = f.SectionWidthMulti.Value;
                if (!string.IsNullOrWhiteSpace(f.Zones)) s.setTool_zones = f.Zones;
                if (f.IsMultiColorSections.HasValue) s.setColor_isMultiColorSections = f.IsMultiColorSections.Value;

                if (f.HitchLength.HasValue) s.setVehicle_hitchLength = f.HitchLength.Value;
                if (f.TrailingHitchLength.HasValue) s.setTool_toolTrailingHitchLength = f.TrailingHitchLength.Value;
                if (f.TankTrailingHitchLength.HasValue) s.setVehicle_tankTrailingHitchLength = f.TankTrailingHitchLength.Value;
                if (f.TrailingToolToPivotLength.HasValue) s.setTool_trailingToolToPivotLength = f.TrailingToolToPivotLength.Value;
                if (f.IsToolTrailing.HasValue) s.setTool_isToolTrailing = f.IsToolTrailing.Value;
                if (f.IsToolTbt.HasValue) s.setTool_isToolTBT = f.IsToolTbt.Value;
                if (f.IsToolRearFixed.HasValue) s.setTool_isToolRearFixed = f.IsToolRearFixed.Value;
                if (f.IsToolFront.HasValue) s.setTool_isToolFront = f.IsToolFront.Value;

                if (f.LookAheadOn.HasValue) s.setVehicle_toolLookAheadOn = f.LookAheadOn.Value;
                if (f.LookAheadOff.HasValue) s.setVehicle_toolLookAheadOff = f.LookAheadOff.Value;
                if (f.TurnOffDelay.HasValue) s.setVehicle_toolOffDelay = f.TurnOffDelay.Value;
                if (f.IsSectionOffWhenOut.HasValue) s.setTool_isSectionOffWhenOut = f.IsSectionOffWhenOut.Value;
                if (f.SlowSpeedCutoff.HasValue) s.setVehicle_slowSpeedCutoff = f.SlowSpeedCutoff.Value;
                if (f.MinCoverage.HasValue) s.setVehicle_minCoverage = f.MinCoverage.Value;

                return true;
            }
            catch (Exception ex)
            {
                Log.EventWriter("ToolGeometryStore -> no se pudo leer " + Ruta + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Descripción corta para el log de arranque.</summary>
        public static string Resumen()
        {
            try
            {
                var s = global::AgOpenGPS.Properties.Settings.Default;
                int n = s.setTool_isSectionsNotZones ? s.setVehicle_numSections : s.setTool_numSectionsMulti;
                return n.ToString() + (s.setTool_isSectionsNotZones ? " secciones" : " secciones (zonas)")
                    + ", ancho " + s.setVehicle_toolWidth.ToString("0.##") + " m";
            }
            catch { return "(sin datos)"; }
        }

        private static decimal[] LeerPosiciones(global::AgOpenGPS.Properties.Settings s)
        {
            return new[]
            {
                s.setSection_position1,  s.setSection_position2,  s.setSection_position3,
                s.setSection_position4,  s.setSection_position5,  s.setSection_position6,
                s.setSection_position7,  s.setSection_position8,  s.setSection_position9,
                s.setSection_position10, s.setSection_position11, s.setSection_position12,
                s.setSection_position13, s.setSection_position14, s.setSection_position15,
                s.setSection_position16, s.setSection_position17
            };
        }

        private static void EscribirPosiciones(global::AgOpenGPS.Properties.Settings s, decimal[] p)
        {
            s.setSection_position1 = p[0]; s.setSection_position2 = p[1];
            s.setSection_position3 = p[2]; s.setSection_position4 = p[3];
            s.setSection_position5 = p[4]; s.setSection_position6 = p[5];
            s.setSection_position7 = p[6]; s.setSection_position8 = p[7];
            s.setSection_position9 = p[8]; s.setSection_position10 = p[9];
            s.setSection_position11 = p[10]; s.setSection_position12 = p[11];
            s.setSection_position13 = p[12]; s.setSection_position14 = p[13];
            s.setSection_position15 = p[14]; s.setSection_position16 = p[15];
            s.setSection_position17 = p[16];
        }
    }
}
