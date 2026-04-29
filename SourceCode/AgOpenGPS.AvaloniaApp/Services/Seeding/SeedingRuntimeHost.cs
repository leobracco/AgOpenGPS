using System;
using System.Threading;
using System.Threading.Tasks;
using AgOpenGPS.Seeding;

namespace AgOpenGPS.AvaloniaApp.Services.Seeding
{
    // Owns the lifecycle of the data source binding to the SeedingMonitor.
    // Started by MainWindow on Loaded and stopped on Closing.
    public sealed class SeedingRuntimeHost : IDisposable
    {
        private readonly SeedingMonitor _monitor;
        private readonly ISeedingDataSource _source;
        private readonly CancellationTokenSource _cts = new();
        private bool _started;

        public SeedingRuntimeHost(SeedingMonitor monitor, ISeedingDataSource source)
        {
            _monitor = monitor;
            _source = source;
        }

        public async Task StartAsync()
        {
            if (_started) return;
            _started = true;
            _monitor.Bind(_source);
            await _source.StartAsync(_cts.Token).ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            if (!_started) return;
            _started = false;
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { await _source.StopAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
            _monitor.Unbind(_source);
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch { /* best effort */ }
            _cts.Dispose();
        }
    }
}
