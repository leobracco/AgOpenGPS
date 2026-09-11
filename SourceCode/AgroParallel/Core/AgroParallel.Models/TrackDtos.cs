// TrackDtos.cs — lista de guías (AB/curvas) del lote activo, para que el
// panel web "Elegir guía" (pages/guia-rapida.html, ícono agrupado bajo
// btnTrack) pueda mostrar por nombre lo que hoy solo se recorre a ciegas con
// track_prev/track_next. Fuente: FormGPS.trk (CTrack.gArr), ver CTrack.cs.

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class TrackItemDto
    {
        /// <summary>Posición en trk.gArr — es lo que se manda de vuelta a
        /// POST /aog/tracks/select para elegir esta guía.</summary>
        [JsonPropertyName("index")] public int Index { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; }

        /// <summary>"ab" | "curve" | "bnd_outer" | "bnd_inner" | "bnd_curve" |
        /// "water_pivot" | "unknown" (mapeo de CTrack.TrackMode).</summary>
        [JsonPropertyName("mode")] public string Mode { get; set; }

        [JsonPropertyName("is_visible")] public bool IsVisible { get; set; }

        /// <summary>true si es la guía activa (i == trk.idx).</summary>
        [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    }
}
