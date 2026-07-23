// TramGeometryPoller.cs
//
// Poller dedicado para /api/aog/tram. Cadencia 1 Hz (igual que guidance —
// tram cambia muy de tanto en tanto, solo al regenerar). Usa revision-cache
// para evitar entregar al render snapshots identicos al anterior.
//
// Solo se instancia cuando App.UseGl == true. La surface Skia legacy no
// pinta tram (apenas pinta el tractor); Stage 4b lo hace especifico de GL.
//
// Lifetime: Start/Stop manual desde MainWindow; cancela limpio al cerrar.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace PilotX.Desktop.Services;

public sealed class TramGeometryPoller
{
    private readonly TramGeometryClient _client;
    private readonly Action<TramGeometrySnapshot> _onSnapshot;
    private readonly int _periodMs;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastRevision = -1;

    public TramGeometryPoller(TramGeometryClient client, Action<TramGeometrySnapshot> onSnapshot, int periodMs = 1000)
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
                    Dispatcher.UIThread.Post(() => _onSnapshot(snap));
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] TramGeometryPoller: " + ex.Message);
            }
            try { await Task.Delay(_periodMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
