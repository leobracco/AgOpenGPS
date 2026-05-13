// ============================================================================
// AgpWebHost.cs
// Wrapper sobre EmbedIO.WebServer: bind a 127.0.0.1:5180, registra controllers
// + WebSocket + estáticos de wwwroot. Vida del server gobernada por Start/Stop.
// ============================================================================

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using AgroParallel.WebHost.Controllers;
// (controllers en sub-namespace)
using AgroParallel.WebHost.WebSockets;
using EmbedIO;
using EmbedIO.Files;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost
{
    public sealed class AgpWebHost : IDisposable
    {
        private readonly IAogStateProvider _state;
        private readonly ISistemaService _sistema;
        private readonly INodoRegistryService _nodos;
        private readonly IOrbitXConfigService _orbitxCfg;
        private readonly string _wwwroot;
        private readonly int _port;
        private WebServer _server;
        private CancellationTokenSource _cts;
        private TelemetryHub _telemetry;

        public string Url { get; }
        public bool IsRunning { get; private set; }

        public AgpWebHost(IAogStateProvider state,
                          ISistemaService sistema,
                          INodoRegistryService nodos,
                          IOrbitXConfigService orbitxCfg,
                          string wwwroot,
                          int port = 5180)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _sistema = sistema;     // nullable
            _nodos = nodos;         // nullable
            _orbitxCfg = orbitxCfg; // nullable
            _wwwroot = wwwroot;
            _port = port;
            Url = "http://127.0.0.1:" + port + "/";
        }

        public void Start()
        {
            if (IsRunning) return;

            _telemetry = new TelemetryHub(_state);

            _server = new WebServer(o => o
                    .WithUrlPrefix(Url)
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithLocalSessionManager()
                .WithModule(_telemetry)
                .WithWebApi("/api", m => m
                    .WithController(() => new AogStateController(_state))
                    .WithController(() => new SistemaController(_sistema))
                    .WithController(() => new NodosController(_nodos))
                    .WithController(() => new OrbitXController(_orbitxCfg)));

            if (!string.IsNullOrEmpty(_wwwroot) && Directory.Exists(_wwwroot))
            {
                _server = _server.WithStaticFolder("/", _wwwroot, true, m =>
                {
                    m.WithContentCaching(false); // dev-friendly
                });
            }

            _cts = new CancellationTokenSource();
            _ = _server.RunAsync(_cts.Token);
            _telemetry.Start();
            IsRunning = true;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _telemetry?.Stop(); } catch { }
            try { _cts?.Cancel(); } catch { }
            try { _server?.Dispose(); } catch { }
            _server = null;
            _cts = null;
        }

        public void Dispose() => Stop();
    }
}
