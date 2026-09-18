// ============================================================================
// VistaXCalibracionDtos.cs — DTOs de la ventana de calibración "Detectar
// densidad N segundos". El operario aprieta el botón con la sembradora
// trabajando; durante N segundos el servicio acumula el promedio de sem/m
// de los surcos tipo "semilla". Al cierre propone:
//
//   · modo "objetivo"  → guarda el promedio como insumo.densidad_objetivo_sem_m
//   · modo "saturado"  → guarda el override que el operario tipea (o el promedio
//                        si decidió aceptarlo) como insumo.densidad_asumida_saturado_sem_m
//
// El servicio detecta automáticamente "está saturado" si el promedio supera
// max_densidad_sensor → la UI muestra el modal correcto.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class VistaXCalibracionStartDto
    {
        /// <summary>Id del insumo donde se va a guardar el resultado. Si está
        /// vacío, se usa el insumo activo del catálogo.</summary>
        [JsonPropertyName("insumo_id")]
        public string InsumoId { get; set; } = "";

        /// <summary>"objetivo" | "saturado". Determina dónde se persiste el valor
        /// al hacer apply.</summary>
        [JsonPropertyName("modo")]
        public string Modo { get; set; } = "objetivo";

        /// <summary>Duración de la ventana de captura. Default 5 s.</summary>
        [JsonPropertyName("segundos")]
        public double Segundos { get; set; } = 5;
    }

    public sealed class VistaXCalibracionApplyDto
    {
        /// <summary>True para persistir el valor capturado. False = cancelar.</summary>
        [JsonPropertyName("aceptar")]
        public bool Aceptar { get; set; }

        /// <summary>Override manual (sem/m). Si es 0 / negativo, se usa el
        /// promedio capturado por el servicio. Útil para que el operario
        /// "fuerce" 90 sem/m cuando saturó.</summary>
        [JsonPropertyName("valor_override")]
        public double ValorOverride { get; set; }
    }

    public sealed class VistaXCalibracionStateDto
    {
        [JsonPropertyName("running")]
        public bool Running { get; set; }

        [JsonPropertyName("insumo_id")]
        public string InsumoId { get; set; } = "";

        [JsonPropertyName("modo")]
        public string Modo { get; set; } = "objetivo";

        [JsonPropertyName("segundos_total")]
        public double SegundosTotal { get; set; }

        [JsonPropertyName("segundos_restantes")]
        public double SegundosRestantes { get; set; }

        /// <summary>Promedio actual de sem/m sobre los surcos tipo "semilla".</summary>
        [JsonPropertyName("sem_m_actual")]
        public double SemMActual { get; set; }

        /// <summary>True si la ventana ya está topando el max_densidad_sensor
        /// (recomendación: usar modo "saturado").</summary>
        [JsonPropertyName("saturado")]
        public bool Saturado { get; set; }

        /// <summary>Cantidad de muestras agregadas a la ventana.</summary>
        [JsonPropertyName("muestras")]
        public int Muestras { get; set; }

        /// <summary>Surcos que participaron de la captura (solo tipo "semilla",
        /// no muted, con telemetría reciente).</summary>
        [JsonPropertyName("surcos")]
        public List<int> Surcos { get; set; } = new List<int>();

        /// <summary>Resultado disponible para apply (true al terminar la ventana).</summary>
        [JsonPropertyName("listo_para_aplicar")]
        public bool ListoParaAplicar { get; set; }

        /// <summary>Promedio final de la ventana (válido cuando Running=false y
        /// ListoParaAplicar=true). Es lo que iría al insumo si Aceptar=true sin override.</summary>
        [JsonPropertyName("valor_final_sem_m")]
        public double ValorFinalSemM { get; set; }
    }
}
