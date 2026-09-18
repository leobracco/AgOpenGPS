// FlagsPoller.cs
//
// Poller para /api/flags/state. Cadencia baja (2 s): las banderas las carga
// el operario a mano de a una, no hay urgencia de tiempo real como con la
// posición del tractor. Sin revision-cache — la lista es chica (unas pocas
// docenas como mucho) y el DTO no trae una, así que se compara por cantidad +
// suma de IDs: barato y alcanza para no re-subir el mismo estado a cada poll.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace PilotX.Desktop.Services;

public sealed class FlagsPoller
{
    private readonly FlagsClient _client;
    private readonly Action<List<FlagPoint>> _onSnapshot;
    private readonly int _periodMs;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _lastHash = int.MinValue;

    public FlagsPoller(FlagsClient client, Action<List<FlagPoint>> onSnapshot, int periodMs = 2000)
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

    private static int HashOf(List<FlagPoint> flags)
    {
        // Cambia si se agrega/borra una bandera o si se mueve/recolorea una
        // existente (easting/northing/color entran en el hash truncados a cm).
        unchecked
        {
            int h = flags.Count;
            foreach (var f in flags)
            {
                h = h * 31 + f.Id;
                h = h * 31 + f.Color;
                h = h * 31 + (int)Math.Round(f.Easting * 100);
                h = h * 31 + (int)Math.Round(f.Northing * 100);
            }
            return h;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var flags = await _client.GetFlagsAsync(ct).ConfigureAwait(false);
                if (flags != null)
                {
                    int h = HashOf(flags);
                    if (h != _lastHash)
                    {
                        _lastHash = h;
                        Dispatcher.UIThread.Post(() => _onSnapshot(flags));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] FlagsPoller: " + ex.Message);
            }
            try { await Task.Delay(_periodMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
