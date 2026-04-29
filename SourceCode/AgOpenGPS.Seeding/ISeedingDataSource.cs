using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenGPS.Seeding
{
    public interface ISeedingDataSource
    {
        event EventHandler<SeedingSnapshot>? SnapshotReceived;
        event EventHandler<RowAlert>? AlertReceived;

        int RowCount { get; }
        bool IsRunning { get; }

        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync();
    }
}
