// SectionXConfigDto — DTO de la config de SectionX (sectionX.json).
// Atributos JsonPropertyName matchean 1:1 los nombres que usa el legacy
// SectionXConfig.cs (snake_case con casing fijo).

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Mapeo de un cable del SectionX controller a una sección PilotX.</summary>
    public sealed class SxCableMapDto
    {
        /// <summary>Número de cable (1-14 según hardware del SectionX controller).</summary>
        [JsonPropertyName("cable")]
        public int Cable { get; set; }

        /// <summary>Sección PilotX (1-based, 0 = no asignado).</summary>
        [JsonPropertyName("seccion_aog")]
        public int SeccionAOG { get; set; }

        /// <summary>Tren al que pertenece el cable (0 = delantero, 1 = trasero).</summary>
        [JsonPropertyName("tren")]
        public int Tren { get; set; }
    }

    public sealed class SxNodoConfigDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; }

        /// <summary>Distancia entre tren delantero y trasero (metros).</summary>
        [JsonPropertyName("distancia_entre_trenes")]
        public double DistanciaEntreTrenes { get; set; }

        [JsonPropertyName("cables")]
        public List<SxCableMapDto> Cables { get; set; }

        public SxNodoConfigDto()
        {
            Cables = new List<SxCableMapDto>();
            Habilitado = true;
            Nombre = "SectionX controller";
        }
    }

    public sealed class SectionXConfigDto
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("nodos")]
        public List<SxNodoConfigDto> Nodos { get; set; }

        [JsonPropertyName("ignorados")]
        public List<string> Ignorados { get; set; }

        public SectionXConfigDto()
        {
            Enabled = true;
            Nodos = new List<SxNodoConfigDto>();
            Ignorados = new List<string>();
        }
    }
}
