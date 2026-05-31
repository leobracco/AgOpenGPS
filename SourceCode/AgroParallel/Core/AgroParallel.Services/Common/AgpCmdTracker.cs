// ============================================================================
// AgpCmdTracker.cs
// Rastreador de comandos MQTT pendientes de ack — completa Tanda 2 (industrial
// doctrine, punto 3): "Comandos nunca crudos · siempre responder ack".
//
// Flujo:
//   1. Bridge llama TrackAsync(uid, cmd_id, ttl_ms) → recibe un Task<CmdAckResult>.
//   2. Bridge hace PublishAsync del cmd con envelope que incluye cmd_id.
//   3. Cuando llega un mensaje a `agp/{prod}/{uid}/ack` (capturado por
//      NodoRegistryService.MessageReceived), el caller llama OnAckReceived(...)
//      con el cmd_id parseado.
//   4. Si llega antes del ttl, el Task resuelve con CmdAckResult{ok=true,...}.
//      Si no, se completa por timeout con ok=false, detail="timeout".
//
// El tracker NO conoce MQTT ni JSON. Es puro estado en memoria — el wiring
// con NodoRegistryService.MessageReceived lo hace el caller. Esto lo hace
// testeable y reusable entre AOG y otras herramientas.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgroParallel.Services.Common
{
    /// <summary>Resultado de un comando: ack recibido, expirado o error.</summary>
    public sealed class CmdAckResult
    {
        public bool Ok { get; set; }
        /// <summary>"ok" | "rejected" | "error" | "timeout" | "publish_failed"</summary>
        public string Status { get; set; }
        public string Detail { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Mantiene un diccionario de cmd_id → TaskCompletionSource. Thread-safe.
    /// Cada entrada arma su propio Timer para liberar el slot al vencer el ttl.
    /// </summary>
    public sealed class AgpCmdTracker : IDisposable
    {
        private sealed class Pending
        {
            public string CmdId;
            public string Uid;
            public DateTime StartedUtc;
            public TaskCompletionSource<CmdAckResult> Tcs;
            public CancellationTokenSource TimeoutCts;
        }

        private readonly Dictionary<string, Pending> _byCmdId =
            new Dictionary<string, Pending>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private bool _disposed;

        /// <summary>
        /// Registra un cmd_id como pendiente. Devuelve el Task que el caller
        /// debe await-ear después de publicar el cmd. Si ttlMs<=0 se usa 5000 ms.
        /// </summary>
        public Task<CmdAckResult> TrackAsync(string uid, string cmdId, int ttlMs)
        {
            if (string.IsNullOrEmpty(cmdId))
                return Task.FromResult(new CmdAckResult { Ok = false, Status = "error", Detail = "cmd_id_vacio" });

            var tcs = new TaskCompletionSource<CmdAckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new Pending
            {
                CmdId = cmdId,
                Uid = uid ?? "",
                StartedUtc = DateTime.UtcNow,
                Tcs = tcs,
                TimeoutCts = new CancellationTokenSource()
            };

            lock (_lock)
            {
                if (_disposed)
                {
                    return Task.FromResult(new CmdAckResult { Ok = false, Status = "error", Detail = "tracker_disposed" });
                }
                _byCmdId[cmdId] = entry;
            }

            // Timer de timeout: si nadie llama OnAckReceived antes, completamos como timeout.
            int waitMs = ttlMs > 0 ? ttlMs : 5000;
            Task.Delay(waitMs, entry.TimeoutCts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return; // ack llegó antes
                Pending p;
                lock (_lock)
                {
                    if (!_byCmdId.TryGetValue(cmdId, out p)) return;
                    _byCmdId.Remove(cmdId);
                }
                try
                {
                    p.Tcs.TrySetResult(new CmdAckResult
                    {
                        Ok = false,
                        Status = "timeout",
                        Detail = "no_ack_en_" + waitMs + "ms",
                        Elapsed = DateTime.UtcNow - p.StartedUtc
                    });
                }
                catch { }
                finally
                {
                    try { p.TimeoutCts?.Dispose(); } catch { }
                }
            }, TaskScheduler.Default);

            return tcs.Task;
        }

        /// <summary>
        /// El caller (NodoRegistryService listener) llama este método cuando
        /// recibe un mensaje en `agp/{prod}/{uid}/ack` con cmd_id parseado.
        /// </summary>
        public void OnAckReceived(string cmdId, string status, string detail)
        {
            if (string.IsNullOrEmpty(cmdId)) return;
            Pending p;
            lock (_lock)
            {
                if (!_byCmdId.TryGetValue(cmdId, out p)) return;
                _byCmdId.Remove(cmdId);
            }
            try { p.TimeoutCts?.Cancel(); } catch { }
            bool ok = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
            try
            {
                p.Tcs.TrySetResult(new CmdAckResult
                {
                    Ok = ok,
                    Status = string.IsNullOrEmpty(status) ? "ok" : status,
                    Detail = detail ?? "",
                    Elapsed = DateTime.UtcNow - p.StartedUtc
                });
            }
            catch { }
            finally
            {
                try { p.TimeoutCts?.Dispose(); } catch { }
            }
        }

        /// <summary>Cantidad de comandos pendientes — útil para diagnostico/UI.</summary>
        public int PendingCount
        {
            get { lock (_lock) { return _byCmdId.Count; } }
        }

        public void Dispose()
        {
            List<Pending> pending;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                pending = new List<Pending>(_byCmdId.Values);
                _byCmdId.Clear();
            }
            foreach (var p in pending)
            {
                try { p.TimeoutCts?.Cancel(); } catch { }
                try
                {
                    p.Tcs.TrySetResult(new CmdAckResult
                    {
                        Ok = false, Status = "error", Detail = "tracker_disposed",
                        Elapsed = DateTime.UtcNow - p.StartedUtc
                    });
                }
                catch { }
                try { p.TimeoutCts?.Dispose(); } catch { }
            }
        }
    }
}
