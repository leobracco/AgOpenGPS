namespace AgOpenGPS.Seeding
{
    public sealed class SeedingTargets
    {
        public SeedingTargets(double targetSeedsPerHectare, double rowSpacingCm, double boomWidthMeters)
        {
            TargetSeedsPerHectare = targetSeedsPerHectare;
            RowSpacingCm = rowSpacingCm;
            BoomWidthMeters = boomWidthMeters;
        }

        public double TargetSeedsPerHectare { get; set; }
        public double RowSpacingCm { get; set; }
        public double BoomWidthMeters { get; set; }

        public double TargetSeedsPerMeterPerRow
        {
            get
            {
                if (RowSpacingCm <= 0) return 0;
                double rowSpacingM = RowSpacingCm / 100.0;
                return TargetSeedsPerHectare * rowSpacingM / 10000.0;
            }
        }
    }
}
