// ============================================================================
// SonidosDtos.cs — alarmas sonoras de cabina, configurables y muteables.
//
// Modelo: una lista FIJA de eventos conocidos (piloto, dosis, motor, tubo,
// tolva…) — el operario no crea eventos, configura los que hay: si suena,
// con qué sonido, cada cuánto repite y con qué umbrales se dispara.
// Persistencia: sonidos.json en ConfigRoot (AtomicJson, snake_case).
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class SonidoEventoDto
    {
        /// <summary>Id fijo del evento (piloto_on, dosis_baja, …).</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Nombre visible para el operario.</summary>
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; } = true;

        /// <summary>Archivo de sonido (nombre en /sounds, ej. "alarma.wav").</summary>
        [JsonPropertyName("sonido")]
        public string Sonido { get; set; } = "";

        /// <summary>Mientras la condición siga activa, volver a sonar cada N
        /// segundos. 0 = suena una sola vez por episodio.</summary>
        [JsonPropertyName("repetir_seg")]
        public int RepetirSeg { get; set; }

        /// <summary>Umbral en % (solo eventos de dosis).</summary>
        [JsonPropertyName("umbral_pct")]
        public double UmbralPct { get; set; }

        /// <summary>Cuántos segundos debe sostenerse la condición antes de
        /// disparar (evita falsas alarmas por baches de un instante).</summary>
        [JsonPropertyName("sostenido_seg")]
        public double SostenidoSeg { get; set; }
    }

    public sealed class SonidosConfigDto
    {
        /// <summary>Mute global: nada suena, la detección sigue (la pantalla
        /// puede mostrar las alarmas igual).</summary>
        [JsonPropertyName("mute")]
        public bool Mute { get; set; }

        [JsonPropertyName("eventos")]
        public List<SonidoEventoDto> Eventos { get; set; } = new List<SonidoEventoDto>();
    }

    /// <summary>Un disparo concreto (transición a activo o repetición).</summary>
    public sealed class SonidoDisparoDto
    {
        [JsonPropertyName("seq")]
        public long Seq { get; set; }

        [JsonPropertyName("evento")]
        public string Evento { get; set; } = "";

        [JsonPropertyName("sonido")]
        public string Sonido { get; set; } = "";

        /// <summary>Qué lo disparó, para la pantalla ("M0: 12% por debajo").</summary>
        [JsonPropertyName("detalle")]
        public string Detalle { get; set; } = "";
    }

    public sealed class SonidosEstadoDto
    {
        /// <summary>Seq del último disparo emitido. El cliente pide
        /// ?desde=seq y recibe solo los nuevos.</summary>
        [JsonPropertyName("seq")]
        public long Seq { get; set; }

        [JsonPropertyName("mute")]
        public bool Mute { get; set; }

        /// <summary>Alarmas activas AHORA (para pintar la pantalla).</summary>
        [JsonPropertyName("activas")]
        public List<SonidoDisparoDto> Activas { get; set; } = new List<SonidoDisparoDto>();

        /// <summary>Disparos posteriores a ?desde= (para sonar).</summary>
        [JsonPropertyName("disparos")]
        public List<SonidoDisparoDto> Disparos { get; set; } = new List<SonidoDisparoDto>();
    }
}
