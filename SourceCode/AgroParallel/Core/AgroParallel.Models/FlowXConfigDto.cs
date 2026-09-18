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

        /// <summary>PWM mínimo bajo el cual el motor no gira (anti-windup
        /// y arranque rápido — el PID arranca acá, no en 0).</summary>
        [JsonPropertyName("pwm_min")]
        public int PwmMin { get; set; }

        /// <summary>PWM máximo permitido al PID (clip del output).
        /// Útil para limitar bombas sobredimensionadas o proteger la barra.
        /// 0 o &lt;= PwmMin => el firmware usa 4095 (sin techo extra).</summary>
        [JsonPropertyName("pwm_max")]
        public int PwmMax { get; set; }

        [JsonPropertyName("kp")] public double Kp { get; set; }
        [JsonPropertyName("ki")] public double Ki { get; set; }
        [JsonPropertyName("kd")] public double Kd { get; set; }

        /// <summary>Modo manual: el target L/min queda FIJO (manual_lmin) e
        /// independiente de la velocidad — la válvula sostiene ese caudal aunque
        /// el operario suba o baje la velocidad. En automático (false) el target
        /// se recalcula con dosis_lha · vel · ancho / 600. El firmware regula el
        /// lazo igual en ambos casos; lo que cambia es de dónde sale el target.</summary>
        [JsonPropertyName("modo_manual")]
        public bool ModoManual { get; set; }

        /// <summary>Target fijo de caudal (L/min) usado en modo manual.</summary>
        [JsonPropertyName("manual_lmin")]
        public double ManualLmin { get; set; }

        /// <summary>Paso de ajuste de la dosis (L/ha) con los botones −/+ del
        /// widget, en modo automático.</summary>
        [JsonPropertyName("paso_lha")]
        public double PasoLha { get; set; }

        /// <summary>Paso de ajuste del caudal (L/min) con los botones −/+ del
        /// widget, en modo manual.</summary>
        [JsonPropertyName("paso_lmin")]
        public double PasoLmin { get; set; }

        /// <summary>Tipo de actuador de la reguladora: "valvula" (válvula
        /// motorizada puente H, abre/cierra) o "motor" (bomba/motor proporcional).
        /// Fase 1: se persiste; el firmware lo honra en Fase 2 (ControlType).</summary>
        [JsonPropertyName("tipo")]
        public string Tipo { get; set; }

        /// <summary>Caudalímetro físico que lee esta reguladora (0 o 1). Un nodo
        /// FlowX tiene 2 sensores; cada producto/línea lee el suyo. Fase 1:
        /// se persiste; se actúa en Fase 2 (target multi-producto).</summary>
        [JsonPropertyName("flow_index")]
        public int FlowIndex { get; set; }

        /// <summary>Invierte el sentido del motor de ESTA reguladora. Equivalente
        /// por-producto del invert_motor del nodo (que se conserva para el
        /// producto 0 / compat firmware actual).</summary>
        [JsonPropertyName("invert_motor")]
        public bool InvertMotor { get; set; }

        public FxProductoDto()
        {
            Id = 0;
            Nombre = "Producto";
            MeterCal = 100.0;   // típico 100 pulsos/L
            DosisLha = 100.0;
            PwmMin = 40;
            PwmMax = 4095;
            Kp = 1.0; Ki = 0.1; Kd = 0.0;
            ModoManual = false;
            ManualLmin = 0.0;
            PasoLha = 5.0;
            PasoLmin = 1.0;
            Tipo = "valvula";
            FlowIndex = 0;
            InvertMotor = false;
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

        /// <summary>Válvula master (general) del nodo:
        ///   -1 = master en salida dedicada (la maneja el firmware con su MasterPin:
        ///        abre si hay cualquier sección abierta, cierra cuando todas cerradas).
        ///    0 = sin master.
        ///   1..N = ese CORTE (cable) hace de master: el bridge pone ese bit = OR de
        ///        todas las secciones abiertas del nodo (el firmware lo ve como canal
        ///        normal). El cable usado como master no debería mapear a una sección.</summary>
        [JsonPropertyName("master_cable")]
        public int MasterCable { get; set; }

        /// <summary>Invierte el sentido del motor de la válvula dosificadora.
        /// Si el cableado del motor hace que abrir/cerrar queden al revés
        /// (comando "abrir" cierra), esto lo corrige sin recablear. Se envía al
        /// firmware como `invertMotor` vía topic agp/flow/{uid}/config.</summary>
        [JsonPropertyName("invert_motor")]
        public bool InvertMotor { get; set; }

        /// <summary>Override 2/3 cables por sección — array de 10 enteros.
        /// Valores válidos: -1 (usar Is3Wire global), 0 (forzar 2 cables), 1 (forzar 3 cables).
        /// Se envía al firmware como `sectionIs3Wire` vía topic agp/flow/{uid}/config.
        /// null o lista vacía → no se envía override y todas las secciones usan Is3Wire global.</summary>
        [JsonPropertyName("section_is_3wire")]
        public List<int> SectionIs3Wire { get; set; }

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
            InvertMotor = false;
            MasterCable = -1; // default: salida dedicada (firmware)
            SectionIs3Wire = new List<int>();
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

        /// <summary>Caudal real expresado en L/ha (dosis aplicada). Derivado de
        /// caudal_lmin reescalado por la dosis configurada: como
        /// target_lmin = dosis_lha · vel · ancho / 600, la velocidad y el ancho
        /// se cancelan ⇒ caudal_lha = caudal_lmin / target_lmin · dosis_lha.
        /// 0 si no hay target (velocidad 0 o sin pulverizar).</summary>
        [JsonPropertyName("caudal_lha")]
        public double CaudalLha { get; set; }

        /// <summary>Dosis objetivo en L/ha (lo que el operario configuró). Es el
        /// valor con el que se compara caudal_lha en la UI.</summary>
        [JsonPropertyName("target_lha")]
        public double TargetLha { get; set; }

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

        /// <summary>Total de pulsos acumulados del caudalímetro desde el último
        /// boot del nodo. Útil para diagnóstico: si la bomba gira pero este
        /// contador no sube, el ISR del firmware no engancha (sensor/cable),
        /// independiente del cálculo Hz→L/min.</summary>
        [JsonPropertyName("pulsos")]
        public long Pulsos { get; set; }

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
