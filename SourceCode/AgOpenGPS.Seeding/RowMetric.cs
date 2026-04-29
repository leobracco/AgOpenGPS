using System;

namespace AgOpenGPS.Seeding
{
    public readonly struct RowMetric : IEquatable<RowMetric>
    {
        public RowMetric(
            int rowIndex,
            double seedsPerSecond,
            double seedsPerMeter,
            double singulationPercent,
            double doublesPercent,
            double skipsPercent,
            double meterRpm,
            DateTimeOffset timestamp)
        {
            RowIndex = rowIndex;
            SeedsPerSecond = seedsPerSecond;
            SeedsPerMeter = seedsPerMeter;
            SingulationPercent = singulationPercent;
            DoublesPercent = doublesPercent;
            SkipsPercent = skipsPercent;
            MeterRpm = meterRpm;
            Timestamp = timestamp;
        }

        public int RowIndex { get; }
        public double SeedsPerSecond { get; }
        public double SeedsPerMeter { get; }
        public double SingulationPercent { get; }
        public double DoublesPercent { get; }
        public double SkipsPercent { get; }
        public double MeterRpm { get; }
        public DateTimeOffset Timestamp { get; }

        public bool Equals(RowMetric other) =>
            RowIndex == other.RowIndex &&
            SeedsPerSecond.Equals(other.SeedsPerSecond) &&
            SeedsPerMeter.Equals(other.SeedsPerMeter) &&
            SingulationPercent.Equals(other.SingulationPercent) &&
            DoublesPercent.Equals(other.DoublesPercent) &&
            SkipsPercent.Equals(other.SkipsPercent) &&
            MeterRpm.Equals(other.MeterRpm) &&
            Timestamp.Equals(other.Timestamp);

        public override bool Equals(object? obj) => obj is RowMetric other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + RowIndex.GetHashCode();
                hash = hash * 31 + SeedsPerSecond.GetHashCode();
                hash = hash * 31 + SeedsPerMeter.GetHashCode();
                hash = hash * 31 + SingulationPercent.GetHashCode();
                hash = hash * 31 + DoublesPercent.GetHashCode();
                hash = hash * 31 + SkipsPercent.GetHashCode();
                hash = hash * 31 + MeterRpm.GetHashCode();
                hash = hash * 31 + Timestamp.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(RowMetric left, RowMetric right) => left.Equals(right);
        public static bool operator !=(RowMetric left, RowMetric right) => !left.Equals(right);
    }
}
