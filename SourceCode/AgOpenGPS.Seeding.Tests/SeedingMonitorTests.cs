using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgOpenGPS.Seeding.Sources;
using NUnit.Framework;

namespace AgOpenGPS.Seeding.Tests
{
    public class SeedingMonitorTests
    {
        private static SeedingMonitor BuildMonitor(int rowCount = 6)
        {
            var targets = new SeedingTargets(targetSeedsPerHectare: 80000, rowSpacingCm: 52.5, boomWidthMeters: rowCount * 0.525);
            var rows = new List<PlanterRow>();
            for (int i = 0; i < rowCount; i++)
                rows.Add(new PlanterRow(i, i * 0.525, i / 2, targets.RowSpacingCm));
            return new SeedingMonitor(rows, targets);
        }

        [Test]
        public async Task Bind_ForwardsSnapshotsAndCachesLast()
        {
            var monitor = BuildMonitor();
            using var src = new SimulatedSeedingDataSource(rowCount: 6, sampleHz: 100, randomSeed: 1);

            int seen = 0;
            monitor.SnapshotPublished += (_, _) => seen++;
            monitor.Bind(src);

            await src.StartAsync(CancellationToken.None);
            await Task.Delay(120);
            await src.StopAsync();

            Assert.That(seen, Is.GreaterThan(0));
            Assert.That(monitor.LastSnapshot, Is.Not.Null);
        }

        [Test]
        public async Task Unbind_StopsForwarding()
        {
            var monitor = BuildMonitor();
            using var src = new SimulatedSeedingDataSource(rowCount: 6, sampleHz: 100, randomSeed: 2);
            monitor.Bind(src);

            int beforeUnbind = 0;
            monitor.SnapshotPublished += (_, _) => beforeUnbind++;

            await src.StartAsync(CancellationToken.None);
            await Task.Delay(80);
            monitor.Unbind(src);
            int snapshotCountAtUnbind = beforeUnbind;
            await Task.Delay(80);
            await src.StopAsync();

            Assert.That(beforeUnbind, Is.EqualTo(snapshotCountAtUnbind),
                "no further snapshots should be forwarded after Unbind");
        }

        [Test]
        public void Constructor_NullRows_Throws()
        {
            var targets = new SeedingTargets(80000, 52.5, 12.6);
            Assert.Throws<ArgumentNullException>(() => new SeedingMonitor(null!, targets));
        }
    }
}
