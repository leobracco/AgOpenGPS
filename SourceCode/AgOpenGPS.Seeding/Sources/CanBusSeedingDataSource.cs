using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenGPS.Seeding.Sources
{
    // Stub. Future: read PGN frames from AgIO via an injected ICanBusFrameReader and
    // translate them into RowMetric/RowAlert events. ISO 11783 TC-SC compatible target.
    public sealed class CanBusSeedingDataSource : ISeedingDataSource
    {
        public CanBusSeedingDataSource(int rowCount)
        {
            if (rowCount <= 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
            RowCount = rowCount;
        }

        public event EventHandler<SeedingSnapshot>? SnapshotReceived;
        public event EventHandler<RowAlert>? AlertReceived;

        public int RowCount { get; }

        public bool IsRunning { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            IsRunning = true;
            // No real frames yet. Hardware integration lands in a follow-up phase.
            _ = SnapshotReceived;
            _ = AlertReceived;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }
    }
}
