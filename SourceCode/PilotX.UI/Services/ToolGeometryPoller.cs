// ToolGeometryPoller.cs
//
// Poller dedicado para /api/aog/tool/geometry. Cadencia 4 Hz (igual que
// HudPoller — el tractor se mueve, las secciones se mueven, hay que
// refrescar a la misma frecuencia que el HUD).
//
// A diferencia de CoveragePoller y GuidanceGeometryPoller, este NO filtra
// por revision: los puntos cambian cada frame asi que cada snapshot que
// llegue se enrutea al callback. El payload es chico (~1 KB) asi que el
// costo no se nota.
//
// Solo se instancia cuando App.UseGl == true. La surface Skia legacy no
// pinta la barra del implemento (apenas pinta el tractor) — Stage 4a la
// hace especifica del render GL.
//
// Lifetime: Start/Stop manual desde MainWindow; cancela limpio al cerrar.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace PilotX.Desktop.Services;

public sealed class ToolGeometryPoller
{
    private readonly ToolGeometryClient _client;
    private readonly Action<ToolGeometrySnapshot> _onSnapshot;
    private readonly int _periodMs;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ToolGeometryPoller(ToolGeometryClient client, Action<ToolGeometrySnapshot> onSnapshot, int periodMs = 250)
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
                {
                    // No filter por revision: cada snapshot va al render.
                    Dispatcher.UIThread.Post(() => _onSnapshot(snap));
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] ToolGeometryPoller: " + ex.Message);
            }
            try { await Task.Delay(_periodMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
