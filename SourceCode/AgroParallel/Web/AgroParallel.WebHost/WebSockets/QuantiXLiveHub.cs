// ============================================================================
// QuantiXLiveHub.cs
// WebSocket en /ws/quantix. Pushea el live de nodos QuantiX (mismo shape que
// GET /api/quantix/live, serializado con AgpJson snake_case) a 5 Hz,
// diff-aware: si el JSON no cambió desde el último tick, no se reenvía.
// Reemplaza el polling HTTP 2 Hz del monitor (quantix.js) — el firmware
// publica status_live a 10 Hz, con el poll se percibían ~500 ms de lag.
// ============================================================================

using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO.WebSockets;

namespace AgroParallel.WebHost.WebSockets
{
    public sealed class QuantiXLiveHub : WebSocketModule
    {
        private readonly INodoRegistryService _registry;
        private System.Threading.Timer _timer;
        private string _lastJson;

        public QuantiXLiveHub(INodoRegistryService registry)
            : base("/ws/quantix", true)
        {
            _registry = registry;
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new System.Threading.Timer(OnTick, null, 200, 200);
        }

        public void Stop()
        {
            if (_timer == null) return;
            _timer.Dispose();
            _timer = null;
        }

        // Push-only: se ignora cualquier mensaje entrante.
        protected override Task OnMessageReceivedAsync(
            IWebSocketContext context, byte[] rxBuffer, IWebSocketReceiveResult rxResult)
        {
            return Task.CompletedTask;
        }

        // Al conectar, mandamos el último snapshot conocido para que la UI
        // pinte al instante sin esperar el próximo cambio.
        protected override Task OnClientConnectedAsync(IWebSocketContext context)
        {
            string json = _lastJson;
            if (json != null)
                return SendAsync(context, json);
            return Task.CompletedTask;
        }

        private void OnTick(object _)
        {
            try
            {
                if (_registry == null) return;

                var all = _registry.GetAll();
                var qx = new List<NodoStatus>();
                for (int i = 0; i < all.Count; i++)
                {
                    var n = all[i];
                    if (n.Type != null && n.Type.IndexOf("quantix", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        qx.Add(n);
                }

                // Mismo shape que GET /api/quantix/live (snake_case vía AgpJson)
                // para que quantix.js procese WS y fallback HTTP con el mismo código.
                string json = AgpJson.Serialize(new { ok = true, count = qx.Count, nodos = qx });
                if (json == _lastJson) return;
                _lastJson = json;

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                _ = BroadcastAsync(bytes);
            }
            catch { }
        }
    }
}
