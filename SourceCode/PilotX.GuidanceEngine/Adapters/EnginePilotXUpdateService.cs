// ============================================================================
// EnginePilotXUpdateService.cs
// Adapter IPilotXUpdateService → PilotXSelfUpdate (static) para el stack
// desktop (Engine headless + PilotX.Desktop). Reemplaza al viejo
// FormGpsPilotXUpdateService, que se fue con el WinForms (953aa9b3).
//
// El controller (PilotXUpdateController, página /actualizar del Hub) llama
// esta interfaz; el adapter arma su HttpClient + OrbitXConfig.Load() y delega
// al motor portable. ApplyAsync() lanza el Updater externo y dispara
// PilotXSelfUpdate.ApplyRequested — el Engine se suscribe en Program.cs para
// bajarse limpio (si nadie se suscribe, el Updater lo mata a los 60 s y ese
// kill saltea el cierre ordenado del lote).
// ============================================================================

using System;
using System.Net.Http;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.OrbitX;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    public sealed class EnginePilotXUpdateService : IPilotXUpdateService, IDisposable
    {
        private readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5) // el ZIP de update pesa cientos de MB
        };

        private static OrbitXConfig LoadCfg()
        {
            // Igual que Android y el viejo FormGps: la config se relee en cada
            // acción — si el operario vincula el tractor DESPUÉS de arrancar,
            // el próximo "Buscar" ya sale con el token nuevo, sin reiniciar.
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

        public void Dispose() => _http.Dispose();
    }
}
