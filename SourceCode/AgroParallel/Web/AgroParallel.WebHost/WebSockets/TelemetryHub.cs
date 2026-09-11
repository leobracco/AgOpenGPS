// ============================================================================
// TelemetryHub.cs
// WebSocket en /ws/telemetry. Pushea snapshot 10Hz a clientes conectados.
// Diff-aware: si el JSON no cambió desde el último tick, no se reenvía.
// ============================================================================

using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO.WebSockets;

namespace AgroParallel.WebHost.WebSockets
{
    public sealed class TelemetryHub : WebSocketModule
    {
        private readonly IAogStateProvider _state;
        private System.Threading.Timer _timer;
        private string _lastJson;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public TelemetryHub(IAogStateProvider state)
            : base("/ws/telemetry", true)
        {
            _state = state;
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new System.Threading.Timer(OnTick, null, 100, 100);
        }

        public void Stop()
        {
            if (_timer == null) return;
            _timer.Dispose();
            _timer = null;
        }

        // Hub push-only: ignoramos cualquier mensaje entrante.
        protected override Task OnMessageReceivedAsync(
            IWebSocketContext context, byte[] rxBuffer, IWebSocketReceiveResult rxResult)
        {
            return Task.CompletedTask;
        }

        private void OnTick(object _)
        {
            try
            {
                AogStateSnapshot snap;
                try { snap = _state.GetSnapshot(); } catch { return; }
                if (snap == null) return;

                string json = JsonSerializer.Serialize(snap, JsonOpts);
                if (json == _lastJson) return;
                _lastJson = json;

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                _ = BroadcastAsync(bytes);
            }
            catch { }
        }
    }
}
