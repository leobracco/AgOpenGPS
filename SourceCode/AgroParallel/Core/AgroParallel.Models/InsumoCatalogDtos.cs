// ============================================================================
// InsumoCatalogDtos.cs
// Catálogo compartido de INSUMOS (semillas, fertilizantes, fitosanitarios).
// Un solo lugar donde el operario carga "Soja Don Mario 46i17" con sus
// parámetros (densidad objetivo, densidad asumida al saturar, % singulación,
// dosis kg/ha o L/ha, precio) y los tres productos lo consumen:
//
//   · VistaX   → DensidadObjetivoSemM + DensidadAsumidaSaturadoSemM + SingulacionObjetivoPct
//   · QuantiX  → DosisKgha (semilla / fertilizante sólido)
//   · FlowX    → DosisLha (pulverización líquida)
//   · datos-lote.html → PrecioUsdPorKg / PrecioUsdPorL para "Ahorro estimado"
//
// Persistencia: insumos.json en el AppDomain.BaseDirectory de PilotX (mismo
// patrón que vistaX.json / flowX.json). Versión bumpeable por migraciones.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class InsumoDto
    {
        /// <summary>Slug único persistente. Ej: "soja-dm-46i17". Lo genera la UI
        /// al crear el insumo y nunca cambia (referenciado por otros configs).</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Nombre visible. Ej: "Soja Don Mario 46i17".</summary>
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        /// <summary>"semilla" | "fertilizante" | "fitosanitario".
        /// Define qué bloque del DTO aplica (Densidad* sólo para semilla).</summary>
        [JsonPropertyName("tipo")]
        public string Tipo { get; set; } = "semilla";

        /// <summary>Cultivo si Tipo=="semilla". Ej: "soja", "maiz", "trigo".
        /// null/"" para no-semillas. Filtro útil para la UI VistaX.</summary>
        [JsonPropertyName("cultivo")]
        public string Cultivo { get; set; } = "";

        // ---- VistaX (solo si Tipo=="semilla") -------------------------------

        /// <summary>Semillas por metro objetivo (típico: soja 14, maíz 5, trigo 270).
        /// El monitor VistaX compara la lectura del sensor contra este número.</summary>
        [JsonPropertyName("densidad_objetivo_sem_m")]
        public double DensidadObjetivoSemM { get; set; }

        /// <summary>Densidad reportada cuando el sensor satura (>= maxDensidadSensor).
        /// Típico: soja 90, maíz 8. Lo que el monitor muestra como "flujo · X".</summary>
        [JsonPropertyName("densidad_asumida_saturado_sem_m")]
        public double DensidadAsumidaSaturadoSemM { get; set; }

        /// <summary>% de singulación objetivo (default 97). VistaX marca rojo
        /// si la singulación del sensor cae por debajo de este número.</summary>
        [JsonPropertyName("singulacion_objetivo_pct")]
        public double SingulacionObjetivoPct { get; set; } = 97.0;

        /// <summary>Mínimo aceptable de sem/m. Por debajo, VistaX marca el surco
        /// como "bajo". 0 = derivar de DensidadObjetivoSemM * (1 - tolerancia)
        /// usando la tolerancia del setup VistaX. Por insumo porque soja vs maíz
        /// tienen rangos aceptables muy distintos.</summary>
        [JsonPropertyName("drop_min_sem_m")]
        public double DropMinSemM { get; set; }

        /// <summary>Máximo aceptable de sem/m. Por encima, VistaX marca "exceso".
        /// 0 = derivar de DensidadObjetivoSemM * (1 + tolerancia). Por insumo
        /// porque la singulación falla diferente según el calibre de semilla.</summary>
        [JsonPropertyName("drop_max_sem_m")]
        public double DropMaxSemM { get; set; }

        // ---- QuantiX (semilla o fertilizante sólido) ------------------------

        /// <summary>kg/ha o sem/ha — depende del contexto del cultivo. QuantiX
        /// usa esto como DosisFija cuando el operario elige este insumo.</summary>
        [JsonPropertyName("dosis_kgha")]
        public double DosisKgha { get; set; }

        // ---- FlowX (líquido pulverización) ----------------------------------

        /// <summary>L/ha — FlowX lo usa como dosis_lha en su nodo activo.</summary>
        [JsonPropertyName("dosis_lha")]
        public double DosisLha { get; set; }

        // ---- Económicos (opcionales) ----------------------------------------

        /// <summary>USD/kg, para el cálculo "Ahorro estimado" en datos-lote.html.</summary>
        [JsonPropertyName("precio_usd_kg")]
        public double PrecioUsdPorKg { get; set; }

        /// <summary>USD/L, idem pero para líquidos.</summary>
        [JsonPropertyName("precio_usd_l")]
        public double PrecioUsdPorL { get; set; }

        // ---- Libre ----------------------------------------------------------

        /// <summary>Notas libres del operario. Sin uso técnico, solo memoria.</summary>
        [JsonPropertyName("notas")]
        public string Notas { get; set; } = "";
    }

    /// <summary>Archivo persistido completo (insumos.json).</summary>
    public sealed class InsumoCatalogDto
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        /// <summary>ID del insumo seleccionado como activo en este momento.
        /// VistaX/QuantiX/FlowX leen este ID al arrancar para elegir defaults.
        /// "" si no hay ninguno seleccionado.</summary>
        [JsonPropertyName("activo_id")]
        public string ActivoId { get; set; } = "";

        [JsonPropertyName("items")]
        public List<InsumoDto> Items { get; set; } = new List<InsumoDto>();
    }
}
