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
        private readonly ISectionXConfigService _sectionxCfg;
        private readonly ICamarasConfigService _camarasCfg;
        private readonly IQuantiXConfigService _quantixCfg;
        private readonly IVistaXConfigService _vistaxCfg;
        private readonly IVistaXLiveService _vistaxLive;
        private readonly IDebugLogService _debug;
        private readonly string _wwwroot;
        private readonly int _port;
        private WebServer _server;
        private CancellationTokenSource _cts;
        private TelemetryHub _telemetry;
        private DebugHub _debugHub;

        public string Url { get; }
        public bool IsRunning { get; private set; }

        public AgpWebHost(IAogStateProvider state,
                          ISistemaService sistema,
                          INodoRegistryService nodos,
                          IOrbitXConfigService orbitxCfg,
                          ISectionXConfigService sectionxCfg,
                          ICamarasConfigService camarasCfg,
                          IQuantiXConfigService quantixCfg,
                          IVistaXConfigService vistaxCfg,
                          IVistaXLiveService vistaxLive,
                          IDebugLogService debug,
                          string wwwroot,
                          int port = 5180)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _sistema = sistema;         // nullable
            _nodos = nodos;             // nullable
            _orbitxCfg = orbitxCfg;     // nullable
            _sectionxCfg = sectionxCfg; // nullable
            _camarasCfg = camarasCfg;   // nullable
            _quantixCfg = quantixCfg;   // nullable
            _vistaxCfg = vistaxCfg;     // nullable
            _vistaxLive = vistaxLive;   // nullable
            _debug = debug;             // nullable
            _wwwroot = wwwroot;
            _port = port;
            Url = "http://127.0.0.1:" + port + "/";
        }

        public void Start()
        {
            if (IsRunning) return;

            _telemetry = new TelemetryHub(_state);
            _debugHub = _debug != null ? new DebugHub(_debug) : null;

            _server = new WebServer(o => o
                    .WithUrlPrefix(Url)
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithLocalSessionManager()
                .WithModule(_telemetry);

            if (_debugHub != null) _server = _server.WithModule(_debugHub);

            _server = _server.WithWebApi("/api", m =>
            {
                m.WithController(() => new AogStateController(_state))
                 .WithController(() => new SistemaController(_sistema))
                 .WithController(() => new NodosController(_nodos))
                 .WithController(() => new QuantiXController(_nodos, _quantixCfg))
                 .WithController(() => new OrbitXController(_orbitxCfg))
                 .WithController(() => new SectionXController(_sectionxCfg))
                 .WithController(() => new CamarasController(_camarasCfg));
                if (_vistaxCfg != null || _vistaxLive != null)
                    m.WithController(() => new VistaXController(_vistaxCfg, _vistaxLive));
                if (_debug != null) m.WithController(() => new DebugController(_debug));
            });

            if (!string.IsNullOrEmpty(_wwwroot) && Directory.Exists(_wwwroot))
            {
                // isImmutable: false → no manda Cache-Control: max-age=... immutable,
                // así WebView2 revalida cada request y los cambios en wwwroot se ven
                // sin recompilar / sin borrar el WebView2Data.
                _server = _server.WithStaticFolder("/", _wwwroot, false, m =>
                {
                    m.WithContentCaching(false); // dev-friendly: tampoco cache en memoria
                });
            }

            _cts = new CancellationTokenSource();
            _ = _server.RunAsync(_cts.Token);
            _telemetry.Start();
            _debugHub?.Start();
            _vistaxLive?.Start();
            IsRunning = true;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _vistaxLive?.Stop(); } catch { }
            try { _debugHub?.Stop(); } catch { }
            try { _telemetry?.Stop(); } catch { }
            try { _cts?.Cancel(); } catch { }
            try { _server?.Dispose(); } catch { }
            _server = null;
            _cts = null;
        }

        public void Dispose() => Stop();
    }
}
