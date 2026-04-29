using System;

namespace AgOpenGPS.Seeding
{
    public sealed class PlanterRow
    {
        public PlanterRow(int index, double offsetMeters, int associatedSectionIndex, double nominalSeedSpacingCm)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            Index = index;
            OffsetMeters = offsetMeters;
            AssociatedSectionIndex = associatedSectionIndex;
            NominalSeedSpacingCm = nominalSeedSpacingCm;
            IsEnabled = true;
        }

        public int Index { get; }

        public double OffsetMeters { get; }

        public int AssociatedSectionIndex { get; }

        public double NominalSeedSpacingCm { get; }

        public bool IsEnabled { get; set; }
    }
}
