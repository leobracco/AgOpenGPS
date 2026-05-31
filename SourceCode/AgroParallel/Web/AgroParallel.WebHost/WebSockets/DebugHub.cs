// ============================================================================
// DebugHub.cs — WebSocket en /ws/debug. Push de cada entrada nueva del log.
// ============================================================================

using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO.WebSockets;

namespace AgroParallel.WebHost.WebSockets
{
    public sealed class DebugHub : WebSocketModule
    {
        private readonly IDebugLogService _log;
        private IDisposable _sub;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public DebugHub(IDebugLogService log) : base("/ws/debug", true)
        {
            _log = log;
        }

        public void Start()
        {
            if (_sub != null || _log == null) return;
            _sub = _log.Subscribe(OnEntry);
        }

        public void Stop()
        {
            if (_sub != null) { try { _sub.Dispose(); } catch { } _sub = null; }
        }

        protected override Task OnMessageReceivedAsync(
            IWebSocketContext context, byte[] rxBuffer, IWebSocketReceiveResult rxResult)
        {
            return Task.CompletedTask;
        }

        private void OnEntry(DebugEntryDto entry)
        {
            try
            {
                string json = JsonSerializer.Serialize(entry, JsonOpts);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                _ = BroadcastAsync(bytes);
            }
            catch { }
        }
    }
}
