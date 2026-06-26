// ============================================================================
// FormGpsPilotXUpdateService.cs
// Adapter IPilotXUpdateService → PilotXSelfUpdate (static).
// El controller (PilotXUpdateController) llama a esta interfaz; el adapter
// toma el HttpClient compartido + OrbitXConfig.Load() y delega al motor.
// ============================================================================

using System;
using System.Net.Http;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.OrbitX;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    public sealed class FormGpsPilotXUpdateService : IPilotXUpdateService, IDisposable
    {
        private readonly HttpClient _http;
        private bool _ownsHttp;

        public FormGpsPilotXUpdateService(HttpClient http = null)
        {
            if (http != null) { _http = http; _ownsHttp = false; }
            else
            {
                _http = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(5) // updates pueden pesar varios MB
                };
                _ownsHttp = true;
            }
        }

        private OrbitXConfig LoadCfg()
        {
            try { return OrbitXConfig.Load(); }
            catch { return new OrbitXConfig(); }
        }

        public PilotXUpdateStatus GetStatus() => PilotXSelfUpdate.Snapshot();

        public Task<PilotXUpdateStatus> CheckAsync()
            => PilotXSelfUpdate.CheckAsync(_http, LoadCfg());

        public Task<PilotXUpdateStatus> DownloadAsync()
            => PilotXSelfUpdate.DownloadAsync(_http, LoadCfg());

        public Task<PilotXUpdateStatus> ApplyAsync()
            => PilotXSelfUpdate.ApplyAsync();

        public void Dispose()
        {
            if (_ownsHttp) { try { _http.Dispose(); } catch { } }
        }
    }
}
