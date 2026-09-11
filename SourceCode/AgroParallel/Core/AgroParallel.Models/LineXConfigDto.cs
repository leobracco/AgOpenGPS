// LineXConfigDto — DTO de la config de LineX (lineX.json).
//
// LineX es el sistema de corte de siembra surco por surco: cada surco es una
// "sección" independiente que se abre o cierra individualmente (máxima
// granularidad, precisión en cabeceras). El firmware actual maneja la salida
// con servomotores (PCA9685), pero el hardware también puede equiparse con
// EMBRAGUES MAGNÉTICOS (on/off binario). Por eso cada nodo tiene un selector
// board_type ("servo" | "embrague") que es PC-side: el firmware no distingue,
// solo recibe el mismo bitmask de secciones; lo que cambia es qué parámetros
// expone la UI (ángulos/pulsos para servo, nada para embrague).
//
// Topics MQTT del firmware (agp/linex/{uid}/...):
//   sections     PC→ESP  {lo, hi}  bitmask de surcos abiertos
//   config       PC→ESP  {mdl:{...}, sections:[{idx,backend,channel,open_angle,
//                          close_angle,min_us,max_us,travel_ms,failsafe_open,invert}], net:{...}}
//   test         PC→ESP  {ch, angle} | {ch, state:"open"|"close"}
//   cmd          PC→ESP  {cmd:"ping"|"get_config"|"reboot"|"clear_safe_mode"|"ota", ...}
//   status_live  ESP→PC  {uid, sections:[{id,state,angle,us}], rssi, uptime}
//   announcement ESP→PC  {uid, ip, type:"LineX", device:"linex", fw, sections, ...}

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>
    /// Configuración de un surco (sección) del nodo LineX. Mapea 1:1 con el
    /// objeto de <c>sections[]</c> que consume el firmware en
    /// agp/linex/{uid}/config, más el campo PC-side <c>seccion_aog</c> para el
    /// mapeo a la sección de PilotX (usado por el bridge runtime).
    /// </summary>
    public sealed class LxSurcoDto
    {
        /// <summary>Índice del surco dentro del nodo (0-based, &lt; section_count).</summary>
        [JsonPropertyName("idx")]
        public int Idx { get; set; }

        /// <summary>Backend de salida del firmware: 0 = PCA9685 (servo), 1 = LEDC (PWM GPIO directo).</summary>
        [JsonPropertyName("backend")]
        public int Backend { get; set; }

        /// <summary>Canal PCA9685 (0..15) o pin GPIO si backend LEDC.</summary>
        [JsonPropertyName("channel")]
        public int Channel { get; set; }

        /// <summary>Ángulo (grados) del servo en estado ABIERTO (sembrando). Solo servo.</summary>
        [JsonPropertyName("open_angle")]
        public int OpenAngle { get; set; }

        /// <summary>Ángulo (grados) del servo en estado CERRADO (corte). Solo servo.</summary>
        [JsonPropertyName("close_angle")]
        public int CloseAngle { get; set; }

        /// <summary>Pulso (µs) calibrado a 0°. Solo servo.</summary>
        [JsonPropertyName("min_us")]
        public int MinUs { get; set; }

        /// <summary>Pulso (µs) calibrado a 180°. Solo servo.</summary>
        [JsonPropertyName("max_us")]
        public int MaxUs { get; set; }

        /// <summary>Tiempo (ms) de rampa para recorrer todo el arco (suaviza el servo). Solo servo.</summary>
        [JsonPropertyName("travel_ms")]
        public int TravelMs { get; set; }

        /// <summary>Estado seguro ante pérdida de comunicación: false = CERRAR (corta siembra).</summary>
        [JsonPropertyName("failsafe_open")]
        public bool FailsafeOpen { get; set; }

        /// <summary>Invierte la lógica abrir/cerrar (corrige cableado/montaje sin recablear).</summary>
        [JsonPropertyName("invert")]
        public bool Invert { get; set; }

        /// <summary>Sección de PilotX (1-based, 0 = sin asignar) que comanda este
        /// surco. Campo PC-side: NO se manda al firmware, lo usa el bridge para
        /// derivar el bitmask de agp/linex/{uid}/sections desde el estado de
        /// secciones de PilotX.</summary>
        [JsonPropertyName("seccion_aog")]
        public int SeccionAOG { get; set; }

        public LxSurcoDto()
        {
            Idx = 0;
            Backend = 0;        // PCA9685
            Channel = 0;
            OpenAngle = 0;
            CloseAngle = 90;
            MinUs = 500;
            MaxUs = 2500;
            TravelMs = 400;
            FailsafeOpen = false;
            Invert = false;
            SeccionAOG = 0;
        }
    }

    public sealed class LxNodoConfigDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; }

        /// <summary>Tipo de placa de salida: "servo" (PCA9685, ángulos/pulsos) o
        /// "embrague" (embrague magnético on/off). Selector PC-side: el firmware
        /// recibe el mismo bitmask de secciones en ambos casos; este campo solo
        /// decide qué parámetros muestra la UI y cómo se interpreta el corte.</summary>
        [JsonPropertyName("board_type")]
        public string BoardType { get; set; }

        /// <summary>Cantidad de surcos/secciones activas del nodo (firmware mdl.section_count).</summary>
        [JsonPropertyName("section_count")]
        public int SectionCount { get; set; }

        /// <summary>Frecuencia PWM del PCA9685 (Hz). 50 = servos estándar.</summary>
        [JsonPropertyName("pwm_freq")]
        public int PwmFreq { get; set; }

        /// <summary>Pin de Output Enable del PCA9685 (firmware mdl.output_enable_pin).</summary>
        [JsonPropertyName("output_enable_pin")]
        public int OutputEnablePin { get; set; }

        /// <summary>Timeout (ms) sin comandos antes de aplicar el estado seguro (failsafe).</summary>
        [JsonPropertyName("comm_timeout_ms")]
        public int CommTimeoutMs { get; set; }

        [JsonPropertyName("surcos")]
        public List<LxSurcoDto> Surcos { get; set; }

        public LxNodoConfigDto()
        {
            Uid = "";
            Nombre = "Nodo LineX";
            Habilitado = true;
            BoardType = "servo";
            SectionCount = 7;
            PwmFreq = 50;
            OutputEnablePin = 27;
            CommTimeoutMs = 3000;
            Surcos = new List<LxSurcoDto>();
        }
    }

    public sealed class LineXConfigDto
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("nodos")]
        public List<LxNodoConfigDto> Nodos { get; set; }

        [JsonPropertyName("ignorados")]
        public List<string> Ignorados { get; set; }

        public LineXConfigDto()
        {
            Enabled = true;
            Nodos = new List<LxNodoConfigDto>();
            Ignorados = new List<string>();
        }
    }

    // =============================================================
    // DTOs de telemetría live — producidos por LineXLiveService a
    // partir del topic agp/linex/{uid}/status_live. La UI los consume
    // por GET /api/linex/live (sin tocar MQTT).
    // =============================================================

    /// <summary>Estado de un surco en vivo (firmware sections[] de status_live).</summary>
    public sealed class LxSurcoLiveDto
    {
        /// <summary>Índice del surco (0-based).</summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>true = abierto (sembrando), false = cerrado (corte).</summary>
        [JsonPropertyName("abierto")]
        public bool Abierto { get; set; }

        /// <summary>Ángulo actual del servo (grados). Solo informativo en servo.</summary>
        [JsonPropertyName("angle")]
        public int Angle { get; set; }

        /// <summary>Ancho de pulso actual (µs). Solo informativo en servo.</summary>
        [JsonPropertyName("us")]
        public int Us { get; set; }
    }

    /// <summary>Telemetría agregada de un nodo LineX en vivo.</summary>
    public sealed class LxNodoLiveDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        /// <summary>Nombre legible (copia desde config — vacío si el nodo no está en lineX.json).</summary>
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        /// <summary>Tipo de placa configurado ("servo" | "embrague"). Vacío si no está en config.</summary>
        [JsonPropertyName("board_type")]
        public string BoardType { get; set; }

        /// <summary>Online si recibimos status_live recientemente.</summary>
        [JsonPropertyName("online")]
        public bool Online { get; set; }

        /// <summary>RSSI WiFi reportado por el nodo (dBm).</summary>
        [JsonPropertyName("rssi")]
        public int Rssi { get; set; }

        /// <summary>Uptime del nodo (segundos).</summary>
        [JsonPropertyName("uptime")]
        public long Uptime { get; set; }

        /// <summary>Estado por surco.</summary>
        [JsonPropertyName("surcos")]
        public List<LxSurcoLiveDto> Surcos { get; set; }

        /// <summary>ISO timestamp de la última telemetría recibida.</summary>
        [JsonPropertyName("last_seen_iso")]
        public string LastSeenIso { get; set; }

        public LxNodoLiveDto()
        {
            Uid = "";
            Nombre = "";
            BoardType = "";
            Surcos = new List<LxSurcoLiveDto>();
            LastSeenIso = "";
        }
    }

    /// <summary>Snapshot consolidado de todos los nodos LineX para la UI.</summary>
    public sealed class LineXLiveSnapshotDto
    {
        [JsonPropertyName("monitoreo_activo")]
        public bool MonitoreoActivo { get; set; }

        [JsonPropertyName("nodos")]
        public List<LxNodoLiveDto> Nodos { get; set; }

        public LineXLiveSnapshotDto()
        {
            Nodos = new List<LxNodoLiveDto>();
        }
    }
}
