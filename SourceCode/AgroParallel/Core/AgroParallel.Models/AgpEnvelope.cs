// ============================================================================
// AgpEnvelope.cs
// Sobre estándar para mensajes MQTT del ecosistema Agro Parallel.
//
// Filosofía:
//   · ADITIVO: el envelope vive en un campo "_meta" del JSON publicado. Los
//     firmwares viejos (que no leen _meta) ignoran el campo y siguen
//     funcionando — no rompe nada al desplegar el cambio en el bridge antes
//     de actualizar el firmware.
//   · MÍNIMO: solo los campos que dan valor real industrial — schema/ver para
//     evolución, seq para detectar pérdidas, cmd_id+ttl para
//     idempotencia/expiración, ts_ms para latencia, source para auditoría.
//   · SIMÉTRICO: tiene contraparte exacta en C++ (AgpEnvelope.h en
//     lib/AgpEnvelope/ de cada firmware).
//
// Para comandos críticos (PWM, OTA, calibración, reboot) el envelope habilita:
//   · Dedup en firmware (cmd_id en ring buffer de últimos 16).
//   · Rechazo de comandos vencidos (ts_ms + ttl_ms < ahora_fw).
//   · ACK explícito por `agp/{prod}/{uid}/ack` con el mismo cmd_id.
//
// Para telemetría/announcement el envelope habilita:
//   · Detectar pérdidas (huecos en seq por nodo).
//   · Filtrar mensajes obsoletos al reconectar (ts_ms viejo + retained).
// ============================================================================

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>
    /// Encabezado estándar para todo mensaje MQTT industrial. Se serializa
    /// como `_meta` dentro del payload JSON existente (additive — no reemplaza
    /// los campos del comando/telemetría, los acompaña).
    /// </summary>
    public sealed class AgpEnvelope
    {
        /// <summary>
        /// Identificador semántico del payload, ej. "ap.cmd.v1", "ap.ack.v1",
        /// "ap.quantix.status.v1", "ap.announcement.v1". Permite que un consumidor
        /// nuevo distinga un payload v1 de uno v2 sin sniffear campos.
        /// </summary>
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = "ap.msg.v1";

        /// <summary>Versión numérica del schema (redundante con la "vN" del string, pero útil para comparaciones).</summary>
        [JsonPropertyName("ver")]
        public int Ver { get; set; } = 1;

        /// <summary>
        /// Secuencia monotónica del EMISOR. En PC = contador estático por topic.
        /// En firmware = contador en RAM por uid. Permite al receptor detectar
        /// pérdidas (`seq[N+2]` después de `seq[N]` ⇒ se perdió N+1) y duplicados.
        /// </summary>
        [JsonPropertyName("seq")]
        public long Seq { get; set; }

        /// <summary>Unix epoch en milisegundos del EMISOR al construir el mensaje.</summary>
        [JsonPropertyName("ts_ms")]
        public long TsMs { get; set; }

        /// <summary>
        /// Identificador único del comando (GUID/string). Solo presente en
        /// payloads tipo cmd. El firmware lo devuelve en el ack y lo deduplica
        /// si llega más de una vez (re-entrega del broker, retry del bridge).
        /// </summary>
        [JsonPropertyName("cmd_id")]
        public string CmdId { get; set; }

        /// <summary>
        /// Tiempo de validez del comando en milisegundos desde TsMs. Si el
        /// firmware lo procesa después de TsMs + TtlMs, lo rechaza y publica
        /// ack `{status:"rejected","detail":"expired"}`. Evita comandos
        /// "zombies" después de un corte de red largo. 0 = sin TTL.
        /// </summary>
        [JsonPropertyName("ttl_ms")]
        public int TtlMs { get; set; }

        /// <summary>Origen lógico del mensaje: "pilotx" | "hub" | "orbitx" | "operador".</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "pilotx";

        // ---- Helpers de construcción ---------------------------------------

        /// <summary>
        /// Construye un envelope listo para envoltorio en cmd. Genera cmd_id
        /// nuevo, ts_ms = now, schema = "ap.cmd.v1".
        /// </summary>
        public static AgpEnvelope NewCmd(int ttlMs = 5000, string source = "pilotx", long seq = 0)
        {
            return new AgpEnvelope
            {
                Schema = "ap.cmd.v1",
                Ver = 1,
                Seq = seq,
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CmdId = Guid.NewGuid().ToString("N").Substring(0, 16),
                TtlMs = ttlMs > 0 ? ttlMs : 5000,
                Source = string.IsNullOrEmpty(source) ? "pilotx" : source
            };
        }

        /// <summary>Construye un envelope para telemetría (sin cmd_id/ttl).</summary>
        public static AgpEnvelope NewTelemetry(string schema, long seq, string source = "pilotx")
        {
            return new AgpEnvelope
            {
                Schema = string.IsNullOrEmpty(schema) ? "ap.tel.v1" : schema,
                Ver = 1,
                Seq = seq,
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Source = string.IsNullOrEmpty(source) ? "pilotx" : source
            };
        }

        /// <summary>
        /// Serializa el payload de comando wrap-eado en envelope. El "payload"
        /// son los pares que ya iban (cmd, url, version, etc.) — se mantienen
        /// en el root del JSON; el envelope se agrega como `_meta`.
        ///
        /// Ej:  payload = {"cmd":"ota","url":"...","version":"1.2"}
        ///      result  = {"_meta":{...},"cmd":"ota","url":"...","version":"1.2"}
        /// </summary>
        public static string SerializeWithEnvelope(AgpEnvelope envelope, System.Collections.Generic.IDictionary<string, object> payload)
        {
            if (envelope == null) envelope = NewCmd();
            var merged = new System.Collections.Generic.Dictionary<string, object>();
            merged["_meta"] = envelope;
            if (payload != null)
            {
                foreach (var kv in payload)
                    if (kv.Key != "_meta" && !merged.ContainsKey(kv.Key))
                        merged[kv.Key] = kv.Value;
            }
            return JsonSerializer.Serialize(merged);
        }

        /// <summary>
        /// Parsea un envelope del JSON; devuelve null si no hay `_meta` (mensaje
        /// legacy de firmware sin envelope) o si falla el parse. NO tira excepciones.
        /// </summary>
        public static AgpEnvelope TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                    if (!doc.RootElement.TryGetProperty("_meta", out var metaElem)) return null;
                    return JsonSerializer.Deserialize<AgpEnvelope>(metaElem.GetRawText());
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Payload del topic `agp/{prod}/{uid}/ack` — respuesta del firmware a un cmd.
    /// El campo CmdId hace match con el del cmd original. El bridge espera este
    /// mensaje hasta TtlMs después de publicar, sino marca el cmd como timeout.
    /// </summary>
    public sealed class AgpAck
    {
        [JsonPropertyName("cmd_id")] public string CmdId { get; set; }
        /// <summary>"ok" | "rejected" | "error"</summary>
        [JsonPropertyName("status")] public string Status { get; set; }
        /// <summary>Causa del rejected/error. Vacío en ok.</summary>
        [JsonPropertyName("detail")] public string Detail { get; set; }
        /// <summary>Unix ms del firmware al despachar el ack.</summary>
        [JsonPropertyName("ts_ms")]  public long TsMs { get; set; }
    }
}
