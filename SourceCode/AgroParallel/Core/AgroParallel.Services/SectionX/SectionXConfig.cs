// ============================================================================
// SectionXConfig.cs - Configuración de corte de secciones
// Mapeo explícito: cada cable del nodo → sección PilotX.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.SectionX
{
    // Mapeo de un cable físico del PCA9685 a una sección PilotX.
    public class SxCableMap
    {
        // Número de cable en el PCA9685 (1-14 = SA1A..SA7A, SA1B..SA7B).
        [JsonPropertyName("cable")]
        public int Cable { get; set; }

        // Sección PilotX que controla (1-based, 0 = no asignado).
        [JsonPropertyName("seccion_aog")]
        public int SeccionAOG { get; set; }

        // A qué tren pertenece este cable (0 = delantero, 1 = trasero).
        [JsonPropertyName("tren")]
        public int Tren { get; set; }

        public SxCableMap()
        {
            Cable = 0;
            SeccionAOG = 0;
            Tren = 0;
        }
    }

    public class SxNodoConfig
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; }

        // Distancia entre tren delantero y trasero (metros).
        [JsonPropertyName("distancia_entre_trenes")]
        public double DistanciaEntreTrenes { get; set; }

        // Mapeo cable → sección (reemplaza la lista de trenes/secciones).
        [JsonPropertyName("cables")]
        public List<SxCableMap> Cables { get; set; }

        public SxNodoConfig()
        {
            Uid = "";
            Nombre = "Nodo SectionX";
            Habilitado = true;
            DistanciaEntreTrenes = 0;
            Cables = new List<SxCableMap>();
        }
    }

    public class SectionXConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("nodos")]
        public List<SxNodoConfig> Nodos { get; set; }

        [JsonPropertyName("ignorados")]
        public List<string> Ignorados { get; set; }

        public SectionXConfig()
        {
            Enabled = true;
            Nodos = new List<SxNodoConfig>();
            Ignorados = new List<string>();
        }

        private static readonly string FileName = "sectionX.json";

        public static SectionXConfig Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                var def = new SectionXConfig();
                def.Save();
                return def;
            }
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<SectionXConfig>(File.ReadAllText(path), opts)
                    ?? new SectionXConfig();
            }
            catch { return new SectionXConfig(); }
        }

        public void Save()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
        }
    }
}
