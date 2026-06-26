// ============================================================================
// ImplementoDto.cs
// Modelo CENTRAL del implemento — fuente única de verdad de la geometría
// física de la sembradora / herramienta. Cada producto X-* (VistaX, QuantiX,
// SectionX) se referencia a este modelo en vez de redefinirlo.
//
// Persistencia: implemento.json en AppDomain.BaseDirectory (mismo path que
// vistaX.json). Migración auto desde el VistaXImplementoDto legacy en el
// primer GET del ImplementoService — los datos viejos se preservan.
//
// Diseño: el implemento es una colección de SURCOS (hileras). Cada surco
// pertenece a un TREN y está cubierto por una SECCIÓN PilotX. El resto
// del shape (overlap/hitch/lookahead) se mantiene compatible con la
// ToolConfigDto AOG legacy para que el guiado nativo siga funcionando.
//
// Snake_case en JsonPropertyName por consistencia con el resto del Core.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Tren físico de siembra (ej. delantero / trasero).</summary>
    public sealed class TrenDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        /// <summary>Distancia (m) hacia atrás del tren 0. Tren 0 = referencia.</summary>
        [JsonPropertyName("distancia_m")]
        public double DistanciaM { get; set; }
    }

    /// <summary>Una hilera física del implemento.</summary>
    public sealed class SurcoDto
    {
        /// <summary>Número de surco (1-based, 1..N).</summary>
        [JsonPropertyName("numero")]
        public int Numero { get; set; }

        /// <summary>Id del tren al que pertenece (referencia a TrenDto.Id).</summary>
        [JsonPropertyName("tren_id")]
        public int TrenId { get; set; }

        /// <summary>Sección PilotX que cubre este surco (1-based, 0 = sin asignar).</summary>
        [JsonPropertyName("seccion_pilotx")]
        public int SeccionPilotX { get; set; }
    }

    /// <summary>
    /// Una sección PilotX (subdivisión de la herramienta que se prende/apaga
    /// como unidad). El ancho efectivo y los surcos cubiertos se derivan
    /// recorriendo SurcoDto.SeccionPilotX.
    /// </summary>
    public sealed class SeccionDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        /// <summary>Lookahead encendido (s) — override del global si > 0.</summary>
        [JsonPropertyName("lookahead_on")]
        public double LookaheadOn { get; set; }

        /// <summary>Lookahead apagado (s) — override del global si > 0.</summary>
        [JsonPropertyName("lookahead_off")]
        public double LookaheadOff { get; set; }
    }

    /// <summary>Implemento completo — fuente única de verdad.</summary>
    public sealed class ImplementoDto
    {
        // ----- Geometría global -----

        /// <summary>Nombre legible del implemento.</summary>
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        /// <summary>Ancho total (m). Espejo de ToolConfigDto.Width nativo.</summary>
        [JsonPropertyName("ancho_total_m")]
        public double AnchoTotalM { get; set; }

        /// <summary>Cantidad total de surcos físicos.</summary>
        [JsonPropertyName("numero_surcos")]
        public int NumeroSurcos { get; set; }

        /// <summary>Espaciado entre surcos (m). Default 0.525m (sembradora 21").</summary>
        [JsonPropertyName("distancia_entre_surcos_m")]
        public double DistanciaEntreSurcosM { get; set; } = 0.525;

        // ----- Estructura física -----

        [JsonPropertyName("trenes")]
        public List<TrenDto> Trenes { get; set; } = new List<TrenDto>();

        [JsonPropertyName("surcos")]
        public List<SurcoDto> Surcos { get; set; } = new List<SurcoDto>();

        [JsonPropertyName("secciones")]
        public List<SeccionDto> Secciones { get; set; } = new List<SeccionDto>();

        /// <summary>
        /// UIDs de los nodos AgroParallel asociados a ESTE implemento. Subconjunto
        /// de los nodos aceptados (NodosCuratedDto.Aceptados). Se usa como filtro
        /// de alarmas: si un nodo cae offline pero NO está en esta lista, no
        /// generamos alarma (puede ser de otro implemento). Se mantiene aparte de
        /// las listas por producto (flowX.json / quantix.json / etc.) para evitar
        /// migrar todos esos archivos a per-implement de una sola vez.
        /// </summary>
        [JsonPropertyName("nodos_uids")]
        public List<string> NodosUids { get; set; } = new List<string>();

        // ----- Campos heredados de ToolConfigDto (AOG nativo) -----
        // Estos se sincronizan con Properties.Settings via FormGpsVehicleToolService.

        [JsonPropertyName("overlap_m")]
        public double OverlapM { get; set; }

        [JsonPropertyName("offset_m")]
        public double OffsetM { get; set; }

        [JsonPropertyName("hitch_length_m")]
        public double HitchLengthM { get; set; }

        [JsonPropertyName("trailing_hitch_length_m")]
        public double TrailingHitchLengthM { get; set; }

        [JsonPropertyName("trailing_tool_to_pivot_m")]
        public double TrailingToolToPivotM { get; set; }

        [JsonPropertyName("lookahead_on_s")]
        public double LookaheadOnS { get; set; }

        [JsonPropertyName("lookahead_off_s")]
        public double LookaheadOffS { get; set; }

        [JsonPropertyName("turn_off_delay_s")]
        public double TurnOffDelayS { get; set; }

        [JsonPropertyName("is_trailing")]
        public bool IsTrailing { get; set; }

        [JsonPropertyName("is_tbt")]
        public bool IsTBT { get; set; }

        [JsonPropertyName("is_rear_fixed")]
        public bool IsRearFixed { get; set; }

        [JsonPropertyName("is_front_fixed")]
        public bool IsFrontFixed { get; set; }

        [JsonPropertyName("section_off_when_out")]
        public bool SectionOffWhenOut { get; set; }

        // ----- Metadata del modelo de sembradora (catalogo) -----
        // Estos campos se llenan cuando el usuario aplica una plantilla desde
        // SembradorasCatalog. Sirven para que cada producto X-* sepa qué tipo
        // de hardware tiene enfrente sin tener que adivinarlo:
        //  · QuantiX: si TipoDosificador == "electrica", asume 1 motor por surco.
        //  · LineX:   solo se habilita si TipoDosificador == "electrica".
        //  · SectionX: queda redundante si LineX está activo.
        //  · VistaX:   usa NumeroTorres para auto-paquetar sensores por torre.

        /// <summary>
        /// Tipo de máquina: "sembradora" (default) | "cosechadora" |
        /// "pulverizadora" | "fertilizadora". Lo setea el catálogo al aplicar
        /// una plantilla. Vacío en implementos viejos = se trata como sembradora.
        /// La UI lo usa para mostrar los campos que aplican a cada máquina; los
        /// surcos/trenes/torres solo tienen sentido en sembradoras.
        /// </summary>
        [JsonPropertyName("categoria")]
        public string Categoria { get; set; } = "";

        [JsonPropertyName("marca")]
        public string Marca { get; set; } = "";

        [JsonPropertyName("modelo")]
        public string Modelo { get; set; } = "";

        /// <summary>"fino" | "grueso" | "combinada"</summary>
        [JsonPropertyName("tipo_cultivo")]
        public string TipoCultivo { get; set; } = "";

        /// <summary>"chorrillo" | "monograno" | "air-drill" | "air-planter"</summary>
        [JsonPropertyName("tipo_siembra")]
        public string TipoSiembra { get; set; } = "";

        /// <summary>"rodillo" | "chevron" | "placa" | "neumatico-vacio" |
        /// "neumatico-presion" | "central" | "electrica"</summary>
        [JsonPropertyName("tipo_dosificador")]
        public string TipoDosificador { get; set; } = "";

        /// <summary>Cantidad de torres físicas (0 = no aplica, mecánica).</summary>
        [JsonPropertyName("numero_torres")]
        public int NumeroTorres { get; set; }

        [JsonPropertyName("tiene_fertilizacion")]
        public bool TieneFertilizacion { get; set; }

        /// <summary>"rigida" | "plegable" | "telescopica"</summary>
        [JsonPropertyName("tipo_estructura")]
        public string TipoEstructura { get; set; } = "";
    }
}
