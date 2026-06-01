// CoveragePoller.cs
//
// Poller dedicado para /api/aog/coverage. Cadencia 1 Hz (mucho mas lenta
// que el HUD a 4 Hz) porque el payload puede ser ~3 MB en jornadas
// largas. La revision incremental que devuelve el snapshot permite al
// receptor (MapGlSurface) saltarse la re-upload del VBO si no cambio
// nada — el costo de polling se reduce a parse + comparacion.
//
// Solo se instancia cuando App.UseGl == true. El MapSkiaSurface legacy
// nunca recibe coverage; consume solo el snapshot HUD (que ya trae el
// boundary). Cuando GL llegue a paridad y MapSkiaSurface se retire, el
// poller pasa a ser obligatorio.
//
// Lifetime: el padre (MainWindow) llama Start/Stop. Stop completa la
// task pendiente con cancelacion limpia para no dejar HttpClient activo
// al cerrar la app.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace PilotX.Desktop.Services;

public sealed class CoveragePoller
{
    private readonly CoverageClient _client;
    private readonly Action<CoverageSnapshot> _onSnapshot;
    private readonly int _periodMs;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastRevision = -1;

    public CoveragePoller(CoverageClient client, Action<CoverageSnapshot> onSnapshot, int periodMs = 1000)
    {
        _client = client;
        _onSnapshot = onSnapshot;
        _periodMs = periodMs;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        var cts = _cts; _cts = null;
        if (cts != null)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await _client.GetSnapshotAsync(ct).ConfigureAwait(false);
                if (snap != null && snap.Revision != _lastRevision)
                {
                    _lastRevision = snap.Revision;
                    // Marshal al UI thread: MapGlSurface dispara
                    // RequestNextFrameRendering desde ahi.
                    Dispatcher.UIThread.Post(() => _onSnapshot(snap));
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoveragePoller: " + ex.Message);
            }
            try { await Task.Delay(_periodMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
