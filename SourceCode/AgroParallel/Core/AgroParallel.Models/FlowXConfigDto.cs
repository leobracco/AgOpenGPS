// FlowXConfigDto — DTO de la config de FlowX (flowX.json).
//
// FlowX es el controlador de corte y dosis para pulverizadoras. Cada nodo
// FlowX maneja N "productos" (típicamente 1 = caudal de fitosanitario), con
// PID sobre PWM del motor de la bomba y control de hasta 16 secciones de
// barra vía PCA9685 + relé master.
//
// Topics MQTT del firmware:
//   agp/flow/{uid}/target    PC→ESP  {target:L/min, sec:bits, pwmMin, pid:{kp,ki,kd}}
//   agp/flow/{uid}/config    PC→ESP  {meterCal, is3Wire, invertRelay}
//   agp/flow/{uid}/status_live  ESP→PC telemetría (caudal real, PWM, etc.)
//   agp/flow/announcement    ESP→PC  {uid, ip, fw, motors}

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>
    /// Calibración y parámetros PID por "producto" (caudal de salida).
    /// Un nodo FlowX puede manejar varios productos en paralelo, cada uno
    /// con su propio caudalímetro + bomba.
    /// </summary>
    public sealed class FxProductoDto
    {
        /// <summary>Id del producto dentro del nodo (0-based, hasta MaxProductCount del firmware).</summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>Nombre legible (ej. "Herbicida", "Foliar").</summary>
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        /// <summary>Pulsos/L del caudalímetro (meterCal).</summary>
        [JsonPropertyName("meter_cal")]
        public double MeterCal { get; set; }

        /// <summary>Dosis objetivo (L/ha) — fuente para calcular target L/min.</summary>
        [JsonPropertyName("dosis_lha")]
        public double DosisLha { get; set; }

        /// <summary>PWM mínimo bajo el cual el motor no gira (anti-windup).</summary>
        [JsonPropertyName("pwm_min")]
        public int PwmMin { get; set; }

        [JsonPropertyName("kp")] public double Kp { get; set; }
        [JsonPropertyName("ki")] public double Ki { get; set; }
        [JsonPropertyName("kd")] public double Kd { get; set; }

        public FxProductoDto()
        {
            Id = 0;
            Nombre = "Producto";
            MeterCal = 100.0;   // típico 100 pulsos/L
            DosisLha = 100.0;
            PwmMin = 40;
            Kp = 1.0; Ki = 0.1; Kd = 0.0;
        }
    }

    /// <summary>Mapeo de un cable del PCA9685 del nodo FlowX a una sección PilotX.</summary>
    public sealed class FxCableMapDto
    {
        [JsonPropertyName("cable")]
        public int Cable { get; set; }

        /// <summary>Sección PilotX (1-based, 0 = no asignado).</summary>
        [JsonPropertyName("seccion_aog")]
        public int SeccionAOG { get; set; }
    }

    public sealed class FxNodoConfigDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; }

        /// <summary>Ancho total de la barra (metros) — usado para L/min ↔ L/ha.</summary>
        [JsonPropertyName("ancho_barra_m")]
        public double AnchoBarraM { get; set; }

        /// <summary>Caudalímetro de 3 hilos (HALL alimentado) vs 2 hilos (reed).</summary>
        [JsonPropertyName("is_3wire")]
        public bool Is3Wire { get; set; }

        /// <summary>Invierte la lógica de los relés (NA vs NC en el hardware).</summary>
        [JsonPropertyName("invert_relay")]
        public bool InvertRelay { get; set; }

        [JsonPropertyName("productos")]
        public List<FxProductoDto> Productos { get; set; }

        [JsonPropertyName("cables")]
        public List<FxCableMapDto> Cables { get; set; }

        public FxNodoConfigDto()
        {
            Uid = "";
            Nombre = "Nodo FlowX";
            Habilitado = true;
            AnchoBarraM = 0;
            Is3Wire = false;
            InvertRelay = false;
            Productos = new List<FxProductoDto>();
            Cables = new List<FxCableMapDto>();
        }
    }

    public sealed class FlowXConfigDto
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("nodos")]
        public List<FxNodoConfigDto> Nodos { get; set; }

        [JsonPropertyName("ignorados")]
        public List<string> Ignorados { get; set; }

        public FlowXConfigDto()
        {
            Enabled = true;
            Nodos = new List<FxNodoConfigDto>();
            Ignorados = new List<string>();
        }
    }

    // =============================================================
    // DTOs de telemetría live — producidos por FlowXLiveService a
    // partir del topic agp/flow/{uid}/status_live. La UI los consume
    // por GET /api/flowx/live (sin tocar MQTT).
    // =============================================================

    /// <summary>Telemetría agregada de un nodo FlowX en vivo.</summary>
    public sealed class FxNodoLiveDto
    {
        /// <summary>UID del nodo (típicamente MAC sin separadores).</summary>
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        /// <summary>Nombre legible (copia desde config — null si el nodo no está en flowX.json).</summary>
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        /// <summary>Online si recibimos status_live recientemente.</summary>
        [JsonPropertyName("online")]
        public bool Online { get; set; }

        /// <summary>Caudal real medido por el caudalímetro (L/min). 0 si no se está pulverizando.</summary>
        [JsonPropertyName("caudal_lmin")]
        public double CaudalLmin { get; set; }

        /// <summary>Target L/min que el bridge le mandó al nodo. Útil para la UI sin recalcular.</summary>
        [JsonPropertyName("target_lmin")]
        public double TargetLmin { get; set; }

        /// <summary>PWM aplicado a la bomba (0..255 o 0..1023 según firmware — el nodo lo envía).</summary>
        [JsonPropertyName("pwm")]
        public int Pwm { get; set; }

        /// <summary>Estado del lazo PID reportado por el firmware: "ok" | "saturado" | "sin_pulsos" | "off".</summary>
        [JsonPropertyName("pid_estado")]
        public string PidEstado { get; set; }

        /// <summary>Error instantáneo (target - real) en L/min, si lo reporta el firmware.</summary>
        [JsonPropertyName("error_lmin")]
        public double ErrorLmin { get; set; }

        /// <summary>ISO timestamp de la última telemetría recibida.</summary>
        [JsonPropertyName("last_seen_iso")]
        public string LastSeenIso { get; set; }

        public FxNodoLiveDto()
        {
            Uid = "";
            Nombre = "";
            PidEstado = "";
            LastSeenIso = "";
        }
    }

    /// <summary>Snapshot consolidado de todos los nodos FlowX para la UI.</summary>
    public sealed class FlowXLiveSnapshotDto
    {
        [JsonPropertyName("monitoreo_activo")]
        public bool MonitoreoActivo { get; set; }

        [JsonPropertyName("nodos")]
        public List<FxNodoLiveDto> Nodos { get; set; }

        public FlowXLiveSnapshotDto()
        {
            Nodos = new List<FxNodoLiveDto>();
        }
    }

    /// <summary>
    /// Resultado de auto-tune PID emitido por el firmware en
    /// agp/flow/{uid}/autotune_result. La UI lo poolea hasta recibirlo o
    /// agotar timeout, luego propone aplicar al producto.
    /// </summary>
    public sealed class FxTuneResultDto
    {
        [JsonPropertyName("uid")]     public string Uid { get; set; }
        [JsonPropertyName("producto_id")] public int ProductoId { get; set; }
        [JsonPropertyName("ok")]      public bool Ok { get; set; }
        [JsonPropertyName("kp")]      public double Kp { get; set; }
        [JsonPropertyName("ki")]      public double Ki { get; set; }
        [JsonPropertyName("kd")]      public double Kd { get; set; }
        [JsonPropertyName("ku")]      public double Ku { get; set; }
        [JsonPropertyName("tu_ms")]   public double TuMs { get; set; }
        [JsonPropertyName("error")]   public string Error { get; set; }
        [JsonPropertyName("received_utc")] public string ReceivedUtc { get; set; }
    }

    /// <summary>
    /// Resultado de calibración de caudalímetro emitido en
    /// agp/flow/{uid}/calibrar_result tras un cmd calibrar_stop.
    /// El firmware reporta pulsos contados; la UI calcula meter_cal = pulsos / vol_l.
    /// </summary>
    public sealed class FxCalibrarResultDto
    {
        [JsonPropertyName("uid")]         public string Uid { get; set; }
        [JsonPropertyName("producto_id")] public int ProductoId { get; set; }
        [JsonPropertyName("ok")]          public bool Ok { get; set; }
        [JsonPropertyName("pulsos")]      public long Pulsos { get; set; }
        [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
        [JsonPropertyName("error")]       public string Error { get; set; }
        [JsonPropertyName("received_utc")] public string ReceivedUtc { get; set; }
    }
}
