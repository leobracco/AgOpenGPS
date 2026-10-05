// PlanimetriaDtos.cs — planimetría fase 3 en la cabina: mapa de alturas del
// lote abierto (calculado en la PC con Elevation.txt), ambientes por altura
// con su prescripción y guía por curva de nivel.
//
// Lo arma el motor (PilotX.GuidanceEngine/EnginePlanimetriaService) y lo lee
// la Configuración nativa (GPS / IMU › Planimetría) y la capa del mapa. Todo
// snake_case (AgpJson). APAGADO de fábrica: con habilitada=false el motor no
// lee ni calcula nada.
//
// Archivo nuevo, aditivo: no cambia ningún DTO existente.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Estado de la planimetría en la cabina (GET /api/planimetria).</summary>
    public sealed class PlanimetriaEstadoDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
        /// <summary>Motivo cuando ok=false: "service-unavailable" | "apagado" | otro.</summary>
        [JsonPropertyName("error")] public string Error { get; set; }

        /// <summary>Interruptor general (default false).</summary>
        [JsonPropertyName("habilitada")] public bool Habilitada { get; set; }
        /// <summary>Capa de alturas visible en el mapa (default false).</summary>
        [JsonPropertyName("capa_visible")] public bool CapaVisible { get; set; }
        /// <summary>"alturas" (escala de colores) | "ambientes" (loma/media/bajo).</summary>
        [JsonPropertyName("modo_capa")] public string ModoCapa { get; set; } = "alturas";

        /// <summary>"apagado" | "sin_lote" | "sin_datos" | "calculando" | "listo" | "error".</summary>
        [JsonPropertyName("estado")] public string Estado { get; set; } = "apagado";
        [JsonPropertyName("motivo")] public string Motivo { get; set; }
        [JsonPropertyName("lote")] public string Lote { get; set; }
        /// <summary>Cambia cada vez que cambia la capa (cálculo nuevo, ambientes, cota elegida).</summary>
        [JsonPropertyName("rev")] public int Rev { get; set; }

        [JsonPropertyName("puntos_rtk")] public int PuntosRtk { get; set; }
        [JsonPropertyName("pasadas")] public int Pasadas { get; set; }
        [JsonPropertyName("res_m")] public double? ResM { get; set; }
        [JsonPropertyName("intervalo_m")] public double? IntervaloM { get; set; }
        [JsonPropertyName("z_min_m")] public double? ZMinM { get; set; }
        [JsonPropertyName("z_max_m")] public double? ZMaxM { get; set; }
        [JsonPropertyName("desnivel_m")] public double? DesnivelM { get; set; }
        [JsonPropertyName("area_ha")] public double? AreaHa { get; set; }
        [JsonPropertyName("pendiente_media_pct")] public double? PendienteMediaPct { get; set; }
        [JsonPropertyName("pendiente_max_pct")] public double? PendienteMaxPct { get; set; }
        [JsonPropertyName("bajos_cantidad")] public int BajosCantidad { get; set; }
        [JsonPropertyName("bajos_area_ha")] public double? BajosAreaHa { get; set; }
        [JsonPropertyName("nivelacion_aplicada")] public bool NivelacionAplicada { get; set; }
        [JsonPropertyName("sesgo_antes_cm")] public double? SesgoAntesCm { get; set; }
        [JsonPropertyName("sesgo_despues_cm")] public double? SesgoDespuesCm { get; set; }
        [JsonPropertyName("calculo_ms")] public long CalculoMs { get; set; }
        [JsonPropertyName("calculado_utc")] public string CalculadoUtc { get; set; }

        [JsonPropertyName("ambientes")] public PlanimetriaAmbientesDto Ambientes { get; set; } = new PlanimetriaAmbientesDto();
        [JsonPropertyName("dosis")] public PlanimetriaDosisDto Dosis { get; set; } = new PlanimetriaDosisDto();

        /// <summary>Altura del mapa bajo el tractor (null sin GPS o fuera del mapa).</summary>
        [JsonPropertyName("cota_tractor_m")] public double? CotaTractorM { get; set; }
        /// <summary>Cota elegida para la guía (se resalta en el mapa). null = ninguna.</summary>
        [JsonPropertyName("cota_guia_m")] public double? CotaGuiaM { get; set; }

        [JsonPropertyName("ultima_guia")] public string UltimaGuia { get; set; }
        [JsonPropertyName("ultima_prescripcion")] public string UltimaPrescripcion { get; set; }
    }

    public sealed class PlanimetriaAmbientesDto
    {
        /// <summary>"percentil" | "desnivel".</summary>
        [JsonPropertyName("modo")] public string Modo { get; set; } = "percentil";
        [JsonPropertyName("p_bajo")] public double PBajo { get; set; } = 25;
        [JsonPropertyName("p_loma")] public double PLoma { get; set; } = 75;
        [JsonPropertyName("d_bajo_m")] public double DBajoM { get; set; } = 0.30;
        [JsonPropertyName("d_loma_m")] public double DLomaM { get; set; } = 0.30;
        // Resultado (solo lectura):
        [JsonPropertyName("cota_bajo_m")] public double? CotaBajoM { get; set; }
        [JsonPropertyName("cota_loma_m")] public double? CotaLomaM { get; set; }
        [JsonPropertyName("area_bajo_ha")] public double? AreaBajoHa { get; set; }
        [JsonPropertyName("area_media_ha")] public double? AreaMediaHa { get; set; }
        [JsonPropertyName("area_loma_ha")] public double? AreaLomaHa { get; set; }
    }

    /// <summary>Dosis por ambiente (unidad del producto del dosificador: kg/ha, sem/ha, L/ha…).</summary>
    public sealed class PlanimetriaDosisDto
    {
        [JsonPropertyName("bajo")] public double Bajo { get; set; }
        [JsonPropertyName("media")] public double Media { get; set; }
        [JsonPropertyName("loma")] public double Loma { get; set; }
    }

    /// <summary>POST /api/planimetria/config — cambios PARCIALES (null = no tocar).</summary>
    public sealed class PlanimetriaConfigRequest
    {
        [JsonPropertyName("habilitada")] public bool? Habilitada { get; set; }
        [JsonPropertyName("capa_visible")] public bool? CapaVisible { get; set; }
        [JsonPropertyName("modo_capa")] public string ModoCapa { get; set; }
        [JsonPropertyName("modo_ambientes")] public string ModoAmbientes { get; set; }
        [JsonPropertyName("p_bajo")] public double? PBajo { get; set; }
        [JsonPropertyName("p_loma")] public double? PLoma { get; set; }
        [JsonPropertyName("d_bajo_m")] public double? DBajoM { get; set; }
        [JsonPropertyName("d_loma_m")] public double? DLomaM { get; set; }
        [JsonPropertyName("dosis_bajo")] public double? DosisBajo { get; set; }
        [JsonPropertyName("dosis_media")] public double? DosisMedia { get; set; }
        [JsonPropertyName("dosis_loma")] public double? DosisLoma { get; set; }
        /// <summary>Cota a resaltar para la guía. Con <see cref="SinCotaGuia"/> = true se borra.</summary>
        [JsonPropertyName("cota_guia_m")] public double? CotaGuiaM { get; set; }
        [JsonPropertyName("sin_cota_guia")] public bool SinCotaGuia { get; set; }
    }

    /// <summary>POST /api/planimetria/guia.</summary>
    public sealed class PlanimetriaGuiaRequest
    {
        /// <summary>Cota de la curva (m). null = la del mapa bajo el tractor.</summary>
        [JsonPropertyName("cota_m")] public double? CotaM { get; set; }
        [JsonPropertyName("nombre")] public string Nombre { get; set; }
    }

    /// <summary>Respuesta de las acciones (guía, prescripción, calcular).</summary>
    public sealed class PlanimetriaAccionDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
        /// <summary>"apagado" | "sin_lote" | "sin_mapa" | "sin_curva" | "smartpath" |
        /// "sin_dosis" | "disco" | "service-unavailable" | otro.</summary>
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("mensaje")] public string Mensaje { get; set; }
        [JsonPropertyName("nombre")] public string Nombre { get; set; }
        [JsonPropertyName("cota_m")] public double? CotaM { get; set; }
        [JsonPropertyName("puntos")] public int Puntos { get; set; }
        [JsonPropertyName("poligonos")] public int Poligonos { get; set; }
        [JsonPropertyName("prescripcion_id")] public string PrescripcionId { get; set; }
    }

    /// <summary>
    /// Capa del mapa (GET /api/planimetria/capa), YA en el plano local del lote
    /// (easting/northing, sin deriva): el mapa no sabe de lat/lon.
    /// Grilla: fila 0 = NORTE, row-major; z = z_base + cm/100 (UInt16 LE en
    /// base64, 65535 = sin dato). Zona: un byte por celda (0 sin dato, 1 bajo,
    /// 2 media loma, 3 loma) en base64.
    /// </summary>
    public sealed class PlanimetriaCapaDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("rev")] public int Rev { get; set; }
        [JsonPropertyName("nx")] public int Nx { get; set; }
        [JsonPropertyName("ny")] public int Ny { get; set; }
        [JsonPropertyName("e_oeste")] public double EOeste { get; set; }
        [JsonPropertyName("e_este")] public double EEste { get; set; }
        [JsonPropertyName("n_sur")] public double NSur { get; set; }
        [JsonPropertyName("n_norte")] public double NNorte { get; set; }
        [JsonPropertyName("z_base")] public double ZBase { get; set; }
        [JsonPropertyName("z_min")] public double ZMin { get; set; }
        [JsonPropertyName("z_max")] public double ZMax { get; set; }
        [JsonPropertyName("z_cm")] public string ZCm { get; set; }
        [JsonPropertyName("zona")] public string Zona { get; set; }
        [JsonPropertyName("intervalo_m")] public double IntervaloM { get; set; }
        [JsonPropertyName("modo_capa")] public string ModoCapa { get; set; }
        [JsonPropertyName("cota_guia_m")] public double? CotaGuiaM { get; set; }
        [JsonPropertyName("curvas")] public List<PlanimetriaCurvaDto> Curvas { get; set; } = new List<PlanimetriaCurvaDto>();
        /// <summary>La curva de la cota elegida para la guía (resaltada), si hay.</summary>
        [JsonPropertyName("curva_guia")] public PlanimetriaCurvaDto CurvaGuia { get; set; }
    }

    public sealed class PlanimetriaCurvaDto
    {
        [JsonPropertyName("elev")] public double Elev { get; set; }
        [JsonPropertyName("maestra")] public bool Maestra { get; set; }
        /// <summary>Cada línea: e0,n0,e1,n1,… (metros, plano local).</summary>
        [JsonPropertyName("lineas")] public List<float[]> Lineas { get; set; } = new List<float[]>();
    }
}
