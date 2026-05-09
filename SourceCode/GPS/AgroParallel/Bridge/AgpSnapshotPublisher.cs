using System;
using System.Windows.Forms;
using AgOpenGPS.AgroParallel.Core.Bridge;
using AgOpenGPS.AgroParallel.Core.Runtime;

namespace AgOpenGPS.AgroParallel.Bridge;

/// <summary>
/// WinForms-side publisher for live runtime snapshots.
/// It is intentionally optional: FormGPS can create/start it later without changing engine logic.
/// </summary>
public sealed class AgpSnapshotPublisher : IAgpStatePublisher, IDisposable
{
    private readonly IAgpEngineBridge _bridge;
    private readonly Timer _timer;

    public event EventHandler<AgpRuntimeState>? SnapshotPublished;

    public AgpSnapshotPublisher(IAgpEngineBridge bridge, int intervalMilliseconds = 100)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _timer = new Timer { Interval = intervalMilliseconds };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        SnapshotPublished?.Invoke(this, _bridge.GetSnapshot());
    }
}
