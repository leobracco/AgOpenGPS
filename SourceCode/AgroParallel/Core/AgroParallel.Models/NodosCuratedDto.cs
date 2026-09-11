// ============================================================================
// NodosCuratedDto.cs
// DTOs de la lista *curada* de nodos del Hub PilotX.
//
// Diferencia con NodoStatus:
//   - NodoStatus = lo que el broker MQTT está viendo en este instante
//     (volátil, lo maneja NodoRegistryService).
//   - NodosCuratedDto = lo que el operario aceptó/ignoró + alias humano
//     persistido en nodos.json (sobrevive reboots, sirve para reemplazo).
//
// La vista unificada (/api/nodos/unified) combina ambas fuentes:
//   estado = "pendiente" | "aceptado" | "ignorado" | "offline"
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>
    /// Nodo aceptado por el operario. Persistido en nodos.json.
    /// El config funcional (PID, mapeo de cables, calibración) sigue viviendo
    /// en flowX.json / quantix.json / vistax.json — esto es solo la capa de
    /// identidad + alias humano.
    /// </summary>
    public sealed class NodoAceptadoDto
    {
        [JsonPropertyName("uid")] public string Uid { get; set; }
        [JsonPropertyName("tipo")] public string Tipo { get; set; }
        [JsonPropertyName("alias")] public string Alias { get; set; }
        [JsonPropertyName("fecha_alta_utc")] public string FechaAltaUtc { get; set; }

        public NodoAceptadoDto()
        {
            Uid = ""; Tipo = ""; Alias = ""; FechaAltaUtc = "";
        }
    }

    /// <summary>
    /// Archivo nodos.json — fuente curada de identidad de nodos.
    /// </summary>
    public sealed class NodosCuratedDto
    {
        [JsonPropertyName("aceptados")] public List<NodoAceptadoDto> Aceptados { get; set; }
        [JsonPropertyName("ignorados")] public List<string> Ignorados { get; set; }

        public NodosCuratedDto()
        {
            Aceptados = new List<NodoAceptadoDto>();
            Ignorados = new List<string>();
        }
    }

    /// <summary>
    /// Vista unificada para la página /pages/nodos.html.
    /// Cada fila combina: identidad curada (alias + estado) + telemetría runtime
    /// del NodoRegistryService (ip, fw, last seen, online).
    /// </summary>
    public sealed class NodoUnifiedDto
    {
        [JsonPropertyName("uid")] public string Uid { get; set; }
        [JsonPropertyName("tipo")] public string Tipo { get; set; }
        [JsonPropertyName("alias")] public string Alias { get; set; }

        /// <summary>"pendiente" | "aceptado" | "ignorado" | "offline".</summary>
        [JsonPropertyName("estado")] public string Estado { get; set; }

        [JsonPropertyName("ip")] public string Ip { get; set; }
        [JsonPropertyName("firmware")] public string Firmware { get; set; }
        [JsonPropertyName("last_seen_utc")] public string LastSeenUtc { get; set; }
        [JsonPropertyName("online")] public bool Online { get; set; }

        /// <summary>"poweron" | "task_wdt" | "brownout" | "panic" | ...
        /// Reportado por el firmware en el announcement. null si versión vieja.
        /// Útil para mostrar badge ⚠ en la fila del nodo cuando hubo reset anormal.</summary>
        [JsonPropertyName("boot_reason")] public string BootReason { get; set; }

        /// <summary>Tanda 2: nodo en safe-mode tras ≥3 crashes seguidos (firmwares con AgpSafeMode).
        /// La UI muestra un botón "Resetear safe-mode" cuando es true.</summary>
        [JsonPropertyName("safe_mode")] public bool SafeMode { get; set; }

        /// <summary>Tanda 2: crashes consecutivos persistidos en NVS del nodo.
        /// 0 en boot limpio. Sólo orientativo para diagnóstico — el flag operativo es SafeMode.</summary>
        [JsonPropertyName("crash_count")] public int CrashCount { get; set; }

        /// <summary>Solo presente para nodos aceptados. ISO UTC.</summary>
        [JsonPropertyName("fecha_alta_utc")] public string FechaAltaUtc { get; set; }

        /// <summary>true si el UID figura en ImplementoDto.NodosUids del implemento ACTIVO.
        /// La UI usa este flag para filtrar alarmas: si false, una caída del nodo no
        /// dispara banner (puede pertenecer a otro implemento que ahora no está montado).
        /// Lo popula NodosController combinando el unified con IImplementoService — no se persiste.</summary>
        [JsonPropertyName("del_implemento_activo")] public bool DelImplementoActivo { get; set; }

        public NodoUnifiedDto()
        {
            Uid = ""; Tipo = ""; Alias = ""; Estado = "pendiente";
            Ip = ""; Firmware = ""; LastSeenUtc = ""; FechaAltaUtc = "";
            DelImplementoActivo = false;
        }
    }
}
