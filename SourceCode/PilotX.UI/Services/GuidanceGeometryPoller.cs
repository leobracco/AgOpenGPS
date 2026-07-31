// GuidanceGeometryPoller.cs
//
// Poller dedicado para /api/aog/guidance/geometry (+ el XTE de
// /api/aog/guidance en el mismo viaje). El callback corre en CADA poll:
// la GEOMETRIA solo cambia al redefinir la linea, pero el XTE cambia
// siempre que el tractor se mueve — y alimenta la distancia a la linea
// del cluster del piloto y el lightbar. El filtrado por `revision` para
// no re-subir la geometria al GPU lo hace el consumidor
// (MapGlSurface.OnGuidance guarda el XTE y recien despues corta por
// revision). Antes el filtro estaba ACA y el XTE quedaba clavado en el
// ultimo valor hasta que alguien tocara la guia.
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
                if (snap != null)
                    Dispatcher.UIThread.Post(() => _onSnapshot(snap));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] GuidanceGeometryPoller: " + ex.Message);
            }
            try { await Task.Delay(_periodMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
