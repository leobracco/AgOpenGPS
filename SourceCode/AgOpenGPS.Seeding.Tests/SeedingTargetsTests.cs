using NUnit.Framework;

namespace AgOpenGPS.Seeding.Tests
{
    public class SeedingTargetsTests
    {
        [Test]
        public void TargetSeedsPerMeterPerRow_With52_5cmRows_And80kPopulation_IsAround4_2()
        {
            var targets = new SeedingTargets(targetSeedsPerHectare: 80000, rowSpacingCm: 52.5, boomWidthMeters: 12.6);

            // 80000 seeds/ha * 0.525 m / 10000 m^2/ha = 4.2 seeds/m per row.
            Assert.That(targets.TargetSeedsPerMeterPerRow, Is.EqualTo(4.2).Within(0.001));
        }

        [Test]
        public void TargetSeedsPerMeterPerRow_WithZeroRowSpacing_ReturnsZero()
        {
            var targets = new SeedingTargets(targetSeedsPerHectare: 80000, rowSpacingCm: 0, boomWidthMeters: 12.6);

            Assert.That(targets.TargetSeedsPerMeterPerRow, Is.EqualTo(0));
        }
    }
}
