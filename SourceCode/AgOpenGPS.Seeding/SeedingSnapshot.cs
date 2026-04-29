using System;
using System.Collections.Generic;

namespace AgOpenGPS.Seeding
{
    public sealed class SeedingSnapshot
    {
        public SeedingSnapshot(
            IReadOnlyList<RowMetric>? rows,
            double targetSeedsPerMeter,
            double averageSeedsPerMeter,
            double variationPercent,
            double meterShaftRpm,
            double hectaresWorked,
            double speedKmh,
            DateTimeOffset timestamp)
        {
            Rows = rows ?? Array.Empty<RowMetric>();
            TargetSeedsPerMeter = targetSeedsPerMeter;
            AverageSeedsPerMeter = averageSeedsPerMeter;
            VariationPercent = variationPercent;
            MeterShaftRpm = meterShaftRpm;
            HectaresWorked = hectaresWorked;
            SpeedKmh = speedKmh;
            Timestamp = timestamp;
        }

        public IReadOnlyList<RowMetric> Rows { get; }
        public double TargetSeedsPerMeter { get; }
        public double AverageSeedsPerMeter { get; }
        public double VariationPercent { get; }
        public double MeterShaftRpm { get; }
        public double HectaresWorked { get; }
        public double SpeedKmh { get; }
        public DateTimeOffset Timestamp { get; }
    }
}
