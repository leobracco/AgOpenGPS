using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgOpenGPS.Seeding.Sources;
using NUnit.Framework;

namespace AgOpenGPS.Seeding.Tests
{
    public class SimulatedSeedingDataSourceTests
    {
        [Test]
        public async Task Start_EmitsSnapshotsWithExpectedRowCount()
        {
            using var src = new SimulatedSeedingDataSource(rowCount: 24, sampleHz: 100, randomSeed: 42);
            var snapshots = new List<SeedingSnapshot>();
            src.SnapshotReceived += (_, s) => snapshots.Add(s);

            await src.StartAsync(CancellationToken.None);
            await Task.Delay(150);
            await src.StopAsync();

            Assert.That(snapshots, Is.Not.Empty, "expected at least one snapshot");
            Assert.That(snapshots[0].Rows.Count, Is.EqualTo(24));
        }

        [Test]
        public async Task Start_PopulatesPerRowMetricsWithinSaneRange()
        {
            using var src = new SimulatedSeedingDataSource(rowCount: 12, sampleHz: 100, randomSeed: 7);
            SeedingSnapshot? captured = null;
            src.SnapshotReceived += (_, s) => captured ??= s;

            await src.StartAsync(CancellationToken.None);
            await Task.Delay(80);
            await src.StopAsync();

            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.SpeedKmh, Is.GreaterThan(0));
            Assert.That(captured.MeterShaftRpm, Is.GreaterThan(0));
            foreach (var row in captured.Rows)
            {
                Assert.That(row.SeedsPerMeter, Is.GreaterThanOrEqualTo(0));
                Assert.That(row.SingulationPercent, Is.InRange(0, 100));
            }
        }

        [Test]
        public async Task StopAsync_CompletesEvenWithoutStart()
        {
            using var src = new SimulatedSeedingDataSource(rowCount: 4, sampleHz: 50);
            await src.StopAsync();
            Assert.That(src.IsRunning, Is.False);
        }

        [Test]
        public async Task ExternalCancellation_StopsLoop()
        {
            using var src = new SimulatedSeedingDataSource(rowCount: 8, sampleHz: 50);
            using var cts = new CancellationTokenSource();

            await src.StartAsync(cts.Token);
            cts.Cancel();
            await Task.Delay(100);
            await src.StopAsync();

            Assert.That(src.IsRunning, Is.False);
        }
    }
}
