// GuidanceGeometryPoller.cs
//
// Poller dedicado para /api/aog/guidance/geometry. Cadencia 1 Hz: la
// geometria solo cambia al redefinir la linea (AB nueva, curva trazada,
// modo Off↔Curve...). El campo `revision` del snapshot permite saltar
// el callback cuando no hubo cambio, asi el render solo se actualiza
// cuando realmente paso algo.
//
// Solo se instancia cuando App.UseGl == true (igual que CoveragePoller).
// El MapSkiaSurface legacy no pinta la linea de guidance todavia —
// queda pendiente para una segunda pasada si vale la pena (Stage GL
// llegando a paridad lo deja obsoleto).
//
// Lifetime: Start/Stop manual desde MainWindow; cancela limpio al cerrar.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace PilotX.Desktop.Services;

public sealed class GuidanceGeometryPoller
{
    private readonly GuidanceGeometryClient _client;
    private readonly Action<GuidanceGeometrySnapshot> _onSnapshot;
    private readonly int _periodMs;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastRevision = -1;

    public GuidanceGeometryPoller(GuidanceGeometryClient client, Action<GuidanceGeometrySnapshot> onSnapshot, int periodMs = 1000)
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
                System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] GuidanceGeometryPoller: " + ex.Message);
            }
            try { await Task.Delay(_periodMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
