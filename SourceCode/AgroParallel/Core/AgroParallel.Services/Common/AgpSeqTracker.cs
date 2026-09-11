// ============================================================================
// AgpSeqTracker.cs
// Detecta gaps en la secuencia monotónica `seq` que los firmwares (QuantiX,
// VistaX, FlowX) emiten en sus payloads MQTT junto con `schema`.
//
// Completa Tanda 2 (industrial doctrine, punto 7 — schema + seq monotónico):
// la PC ya recibía esos campos pero los ignoraba. Acá los usamos para
// REPORTAR (no alarmar) cuando un nodo perdió mensajes — útil para:
//   · detectar pérdidas de paquete en LAN ruidosa (cable malo, switch saturado)
//   · diagnosticar nodos que bajan/suben el broker sin que se note (reset implícito)
//   · validar que la PC está procesando todo lo que el firmware emite
//
// Convención de los firmwares:
//   · `schema` ej: "agp.quantix.announcement/1"
//   · `seq` int, monotónico POR (uid, schema). Resetea a 0 en reboot del nodo.
//
// Política de gap:
//   · seq == último+1            → Normal (caso típico)
//   · seq <= último              → Reset (reboot del firmware → bumpear estado)
//   · seq > último+1             → Gap (missed = seq − último − 1)
//   · firmware sin `schema`/`seq`→ se ignora (firmware legacy, no gap-tracking)
//
// El tracker no escribe a stdout ni a archivo — guarda los últimos N gaps en
// un ring buffer en memoria y los expone vía GetRecentGaps(). El consumidor
// (NodoRegistryService.GetDiagnostic) los muestra en la página de diag.
// Si un día se quiere persistir, se puede engancharle un evento — pero hoy
// no hace falta: en LAN sana esperamos 0 gaps.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Services.Common
{
    /// <summary>Resultado de observar un (uid, schema, seq).</summary>
    public enum AgpSeqResult
    {
        /// <summary>seq == último+1. Caso esperado en LAN sana.</summary>
        Normal = 0,
        /// <summary>Primera observación para ese (uid, schema). No es gap.</summary>
        First = 1,
        /// <summary>seq cayó por debajo del último visto — el nodo rebooteó. Estado se actualiza al nuevo seq.</summary>
        Reset = 2,
        /// <summary>seq > último+1 → faltaron mensajes intermedios.</summary>
        Gap = 3
    }

    /// <summary>Detalle de un gap detectado, para el ring buffer de diagnóstico.</summary>
    public sealed class AgpSeqGap
    {
        public DateTime TimestampUtc { get; set; }
        public string Uid { get; set; }
        public string Schema { get; set; }
        public int LastSeq { get; set; }
        public int NewSeq { get; set; }
        /// <summary>Cantidad de mensajes perdidos (NewSeq - LastSeq - 1).</summary>
        public int Missed { get; set; }
    }

    /// <summary>
    /// Mantiene Dictionary&lt;(uid, schema), último seq&gt;. Thread-safe.
    /// Costo de Observe: O(1). Memoria: una entrada por (uid, schema) único.
    /// </summary>
    public sealed class AgpSeqTracker
    {
        private const int RecentGapsCapacity = 50;

        private readonly Dictionary<string, int> _lastSeq =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<AgpSeqGap> _recentGaps = new LinkedList<AgpSeqGap>();
        private readonly object _lock = new object();
        private long _totalGapMessages;
        private long _totalResets;

        /// <summary>Total acumulado de mensajes perdidos detectados (suma de Missed).</summary>
        public long TotalGapMessages { get { lock (_lock) return _totalGapMessages; } }

        /// <summary>Cantidad de resets detectados (reboots implícitos del firmware).</summary>
        public long TotalResets { get { lock (_lock) return _totalResets; } }

        /// <summary>
        /// Registra una observación. Si <paramref name="schema"/> está vacío o
        /// <paramref name="seq"/> &lt; 0 (firmware legacy), retorna Normal sin
        /// alterar estado — equivale a "no gap-tracking para este payload".
        /// </summary>
        public AgpSeqResult Observe(string uid, string schema, int seq)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(schema) || seq < 0)
                return AgpSeqResult.Normal;

            string key = uid + "|" + schema;
            lock (_lock)
            {
                int last;
                if (!_lastSeq.TryGetValue(key, out last))
                {
                    _lastSeq[key] = seq;
                    return AgpSeqResult.First;
                }

                if (seq == last + 1)
                {
                    _lastSeq[key] = seq;
                    return AgpSeqResult.Normal;
                }

                if (seq <= last)
                {
                    // Reboot del nodo: contador volvió a 0 o a un valor más bajo.
                    _lastSeq[key] = seq;
                    _totalResets++;
                    return AgpSeqResult.Reset;
                }

                // seq > last + 1 → gap
                int missed = seq - last - 1;
                _lastSeq[key] = seq;
                _totalGapMessages += missed;
                var gap = new AgpSeqGap
                {
                    TimestampUtc = DateTime.UtcNow,
                    Uid = uid,
                    Schema = schema,
                    LastSeq = last,
                    NewSeq = seq,
                    Missed = missed
                };
                _recentGaps.AddLast(gap);
                while (_recentGaps.Count > RecentGapsCapacity)
                    _recentGaps.RemoveFirst();
                return AgpSeqResult.Gap;
            }
        }

        /// <summary>Snapshot inmutable de los últimos gaps, más nuevos primero.</summary>
        public IReadOnlyList<AgpSeqGap> GetRecentGaps()
        {
            lock (_lock)
            {
                var copy = new List<AgpSeqGap>(_recentGaps.Count);
                for (var node = _recentGaps.Last; node != null; node = node.Previous)
                    copy.Add(node.Value);
                return copy;
            }
        }

        /// <summary>Resetea el estado interno. Solo para tests o reload de bridge.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _lastSeq.Clear();
                _recentGaps.Clear();
                _totalGapMessages = 0;
                _totalResets = 0;
            }
        }
    }
}
