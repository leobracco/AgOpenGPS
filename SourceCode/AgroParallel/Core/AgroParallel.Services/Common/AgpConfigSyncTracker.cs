// ============================================================================
// AgpConfigSyncTracker.cs
// Tanda 2 industrial doctrine #8: split de /config en desired (PC publica
// retained) y reported (firmware publica retained tras aplicar).
//
// El tracker mantiene un Dictionary<uid, ConfigSyncEntry> con:
//   · DesiredHash + DesiredTs  → última config que la PC mandó a `.../config/desired`
//   · ReportedHash + ReportedTs → último estado que el firmware reportó en `.../config/reported`
//   · Status: in_sync | drift | no_report | pending
//
// Reglas:
//   · Nunca recibí desired NI reported → entry no existe (uid sin config syncing).
//   · Recibí desired pero NO reported (firmware legacy o ack pendiente) → "no_report".
//   · ReportedTs >= DesiredTs → "in_sync" (asumimos que el firmware echó back tras aplicar).
//   · ReportedTs <  DesiredTs → "pending" si delta < 10s, "drift" si delta >= 10s.
//
// Sobre el hash:
// ----------------
// Usamos FNV-1a 64-bit del JSON raw. NO canonicalizamos por orden de claves —
// el firmware puede re-serializar y romper la igualdad byte-a-byte. Por eso la
// señal primaria de "in_sync" es la TIMESTAMP (reported >= desired), no el hash.
// El hash queda guardado para mostrar al operador en diag y para futuros usos
// (ej: detectar firmware que reporta algo distinto de lo que se le pidió —
// pero eso requiere canonicalización JSON que se puede agregar después).
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Services.Common
{
    /// <summary>Estado de sincronización de la config de un nodo.</summary>
    public enum ConfigSyncStatus
    {
        /// <summary>No hay datos suficientes (ej: solo se vio reported, sin desired).</summary>
        Unknown = 0,
        /// <summary>PC publicó desired y el firmware aún no respondió (delta &lt; 10s).</summary>
        Pending = 1,
        /// <summary>El firmware echó back reported después del desired más reciente.</summary>
        InSync = 2,
        /// <summary>PC publicó desired pero el firmware no reportó en &gt; 10s.</summary>
        Drift = 3,
        /// <summary>PC publicó desired y nunca llegó ningún reported (firmware legacy o muerto).</summary>
        NoReport = 4
    }

    public sealed class ConfigSyncEntry
    {
        public string Uid { get; set; }
        public string DesiredHash { get; set; }
        public DateTime? DesiredTsUtc { get; set; }
        public string ReportedHash { get; set; }
        public DateTime? ReportedTsUtc { get; set; }
        public ConfigSyncStatus Status { get; set; }
    }

    /// <summary>Thread-safe, sin deps externas.</summary>
    public sealed class AgpConfigSyncTracker
    {
        /// <summary>Segundos a partir de los cuales un desired sin reported se
        /// considera no_report (firmware legacy o muerto), no pending.</summary>
        public const int NoReportThresholdSeconds = 10;

        private readonly Dictionary<string, ConfigSyncEntry> _entries =
            new Dictionary<string, ConfigSyncEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        /// <summary>Registra que la PC publicó una config desired a un uid.</summary>
        public void TrackDesired(string uid, string payload)
        {
            if (string.IsNullOrEmpty(uid)) return;
            string hash = HashPayload(payload);
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                ConfigSyncEntry e;
                if (!_entries.TryGetValue(uid, out e))
                {
                    e = new ConfigSyncEntry { Uid = uid };
                    _entries[uid] = e;
                }
                e.DesiredHash = hash;
                e.DesiredTsUtc = now;
                e.Status = ComputeStatusLocked(e, now);
            }
        }

        /// <summary>Registra el reported que llegó del firmware.</summary>
        public void TrackReported(string uid, string payload)
        {
            if (string.IsNullOrEmpty(uid)) return;
            string hash = HashPayload(payload);
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                ConfigSyncEntry e;
                if (!_entries.TryGetValue(uid, out e))
                {
                    e = new ConfigSyncEntry { Uid = uid };
                    _entries[uid] = e;
                }
                e.ReportedHash = hash;
                e.ReportedTsUtc = now;
                e.Status = ComputeStatusLocked(e, now);
            }
        }

        /// <summary>Snapshot del estado de un uid, o null si no hay datos.</summary>
        public ConfigSyncEntry GetByUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (_lock)
            {
                ConfigSyncEntry e;
                if (!_entries.TryGetValue(uid, out e)) return null;
                // Recalcular el status: el tiempo pasa aunque no lleguen mensajes.
                e.Status = ComputeStatusLocked(e, DateTime.UtcNow);
                return CloneLocked(e);
            }
        }

        /// <summary>Snapshot completo, principalmente para diag.</summary>
        public IReadOnlyList<ConfigSyncEntry> GetAll()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var copy = new List<ConfigSyncEntry>(_entries.Count);
                foreach (var e in _entries.Values)
                {
                    e.Status = ComputeStatusLocked(e, now);
                    copy.Add(CloneLocked(e));
                }
                return copy;
            }
        }

        private static ConfigSyncStatus ComputeStatusLocked(ConfigSyncEntry e, DateTime now)
        {
            // Sin desired: el tracker no aplica al uid (deberíamos haber filtrado antes).
            if (!e.DesiredTsUtc.HasValue) return ConfigSyncStatus.Unknown;

            if (!e.ReportedTsUtc.HasValue)
            {
                // PC publicó pero el firmware nunca respondió.
                var elapsed = (now - e.DesiredTsUtc.Value).TotalSeconds;
                return elapsed >= NoReportThresholdSeconds
                    ? ConfigSyncStatus.NoReport
                    : ConfigSyncStatus.Pending;
            }

            if (e.ReportedTsUtc.Value >= e.DesiredTsUtc.Value)
                return ConfigSyncStatus.InSync;

            // Reported es más viejo que desired → el firmware aún no reaccionó.
            var deltaSec = (now - e.DesiredTsUtc.Value).TotalSeconds;
            return deltaSec >= NoReportThresholdSeconds
                ? ConfigSyncStatus.Drift
                : ConfigSyncStatus.Pending;
        }

        private static ConfigSyncEntry CloneLocked(ConfigSyncEntry src)
        {
            return new ConfigSyncEntry
            {
                Uid = src.Uid,
                DesiredHash = src.DesiredHash,
                DesiredTsUtc = src.DesiredTsUtc,
                ReportedHash = src.ReportedHash,
                ReportedTsUtc = src.ReportedTsUtc,
                Status = src.Status
            };
        }

        /// <summary>FNV-1a 64-bit en hex de 16 chars. NO criptográfico — solo
        /// para distinguir payloads visualmente y posible matching futuro.</summary>
        private static string HashPayload(string payload)
        {
            if (payload == null) payload = "";
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;
            ulong hash = fnvOffset;
            for (int i = 0; i < payload.Length; i++)
            {
                hash ^= payload[i];
                hash *= fnvPrime;
            }
            return hash.ToString("x16");
        }
    }
}
