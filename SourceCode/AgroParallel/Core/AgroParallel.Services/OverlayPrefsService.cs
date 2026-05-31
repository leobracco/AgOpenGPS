// ============================================================================
// OverlayPrefsService.cs
// Preferencias de visibilidad de los widgets de overlay sobre el mapa de PilotX
// (QuantiX shapefileLegend, VistaX, FlowX legend). El operario las controla
// desde el Hub (página /widgets.html). FormGPS las lee y aplica sin reiniciar.
//
// Persiste en overlayPrefs.json en AppDomain.BaseDirectory. Singleton estatico
// para que FormGPS y el WebHost compartan el mismo archivo.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.Services
{
    public sealed class OverlayPrefsDto
    {
        [JsonPropertyName("qx_overlay")]
        public bool QXOverlay { get; set; } = true;

        [JsonPropertyName("vx_overlay")]
        public bool VxOverlay { get; set; } = true;

        [JsonPropertyName("fx_overlay")]
        public bool FXOverlay { get; set; } = true;

        // Posiciones custom de los overlays (drag persistido).
        // Sentinela -1 = "sin posición custom, usar default del overlay".
        // Si el usuario arrastra el widget, guardamos las coords absolutas en
        // pixeles relativas a FormGPS. Al resetear (doble-click sobre el header
        // o desde el Hub) vuelven a -1 y el overlay vuelve a su posición default.
        [JsonPropertyName("qx_x")] public int QxX { get; set; } = -1;
        [JsonPropertyName("qx_y")] public int QxY { get; set; } = -1;

        [JsonPropertyName("vx_x")] public int VxX { get; set; } = -1;
        [JsonPropertyName("vx_y")] public int VxY { get; set; } = -1;

        // Posiciones independientes para los dos overlays HTML de VistaX
        // (strip inferior + stats superior). Si están en -1, FormGPS los
        // ubica en sus defaults (strip: bottom-stretch, stats: top-right).
        [JsonPropertyName("vx_strip_x")] public int VxStripX { get; set; } = -1;
        [JsonPropertyName("vx_strip_y")] public int VxStripY { get; set; } = -1;
        [JsonPropertyName("vx_strip_w")] public int VxStripW { get; set; } = -1;
        [JsonPropertyName("vx_strip_h")] public int VxStripH { get; set; } = -1;

        [JsonPropertyName("vx_stats_x")] public int VxStatsX { get; set; } = -1;
        [JsonPropertyName("vx_stats_y")] public int VxStatsY { get; set; } = -1;
        [JsonPropertyName("vx_stats_w")] public int VxStatsW { get; set; } = -1;
        [JsonPropertyName("vx_stats_h")] public int VxStatsH { get; set; } = -1;

        [JsonPropertyName("fx_x")] public int FxX { get; set; } = -1;
        [JsonPropertyName("fx_y")] public int FxY { get; set; } = -1;
    }

    public sealed class OverlayPrefsService
    {
        public static readonly OverlayPrefsService Instance = new OverlayPrefsService();

        private const string FileName = "overlayPrefs.json";
        private readonly object _lock = new object();

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static string PathOnDisk
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        public OverlayPrefsDto Load()
        {
            lock (_lock)
            {
                string path = PathOnDisk;
                if (!File.Exists(path))
                {
                    var def = new OverlayPrefsDto();
                    try { File.WriteAllText(path, JsonSerializer.Serialize(def, WriteOpts)); } catch { }
                    return def;
                }
                try
                {
                    return JsonSerializer.Deserialize<OverlayPrefsDto>(File.ReadAllText(path), ReadOpts)
                        ?? new OverlayPrefsDto();
                }
                catch { return new OverlayPrefsDto(); }
            }
        }

        public void Save(OverlayPrefsDto dto)
        {
            if (dto == null) return;
            lock (_lock)
            {
                try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(dto, WriteOpts)); } catch { }
            }
        }
    }
}
