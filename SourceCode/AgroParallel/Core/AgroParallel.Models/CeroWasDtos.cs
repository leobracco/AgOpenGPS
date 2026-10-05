// CeroWasDtos.cs — estado del CERO AUTOMÁTICO DEL WAS para la pantalla nativa
// (Menú izquierdo › Dirección › Sensor).
//
// Lo arma el motor (EngineCeroWasService) a partir de
// AgOpenGPS.SteerCal.CeroWasEstadistico. La función MIDE y PROPONE: el offset
// solo cambia cuando el operario toca Aplicar. Todo snake_case (AgpJson).
//
// Archivo nuevo, aditivo: no cambia ningún DTO existente.

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class CeroWasEstadoDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;

        /// <summary>Motivo cuando ok=false: "service-unavailable" | "apagado" |
        /// "sin-propuesta" | "piloto-enganchado" | "manejo-libre" | "asistente" |
        /// "fuera-de-rango" | "sin-deshacer" | "offset-cambiado" | "muy-seguido" | otro.</summary>
        [JsonPropertyName("error")] public string Error { get; set; }

        /// <summary>setAS_ceroWasAuto. Apagado (default) no mide nada.</summary>
        [JsonPropertyName("activo")] public bool Activo { get; set; }

        /// <summary>"apagado" | "juntando" | "cero_bien" | "propuesta" | "inestable" |
        /// "sesgo_excesivo" | "fuera_de_rango".</summary>
        [JsonPropertyName("estado")] public string Estado { get; set; }

        /// <summary>El fix actual cumple "andar derecho estable".</summary>
        [JsonPropertyName("condiciones_ok")] public bool CondicionesOk { get; set; }

        /// <summary>Por qué no se está midiendo (código snake, ver CeroWasEstadistico).</summary>
        [JsonPropertyName("motivo")] public string Motivo { get; set; }

        [JsonPropertyName("muestras")] public int Muestras { get; set; }
        [JsonPropertyName("segundos")] public double Segundos { get; set; }
        [JsonPropertyName("segundos_requeridos")] public double SegundosRequeridos { get; set; }

        /// <summary>Cuánto está corrido el cero (°, + = derecha).</summary>
        [JsonPropertyName("sesgo_grados")] public double SesgoGrados { get; set; }
        [JsonPropertyName("dispersion_grados")] public double DispersionGrados { get; set; }

        /// <summary>0..100.</summary>
        [JsonPropertyName("confianza")] public double Confianza { get; set; }
        [JsonPropertyName("confianza_alta")] public bool ConfianzaAlta { get; set; }

        [JsonPropertyName("offset_actual")] public int OffsetActual { get; set; }
        [JsonPropertyName("offset_propuesto")] public int OffsetPropuesto { get; set; }
        [JsonPropertyName("hay_propuesta")] public bool HayPropuesta { get; set; }

        /// <summary>Aplicar está permitido AHORA (hay propuesta y el piloto está suelto).</summary>
        [JsonPropertyName("puede_aplicar")] public bool PuedeAplicar { get; set; }

        /// <summary>Por qué no se puede aplicar ahora (mismos códigos que error).</summary>
        [JsonPropertyName("bloqueo_aplicar")] public string BloqueoAplicar { get; set; }

        [JsonPropertyName("puede_deshacer")] public bool PuedeDeshacer { get; set; }

        /// <summary>Offset al que vuelve Deshacer (válido si puede_deshacer).</summary>
        [JsonPropertyName("offset_previo")] public int OffsetPrevio { get; set; }
    }
}
