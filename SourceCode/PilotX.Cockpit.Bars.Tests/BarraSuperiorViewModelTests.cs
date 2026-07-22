using NUnit.Framework;
using System.Net.Http;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Tests;

public class BarraSuperiorViewModelTests
{
    private static BarraSuperiorViewModel Make() =>
        new(new GuidanceCommandClient(new HttpClient()));

    [Test]
    public void Apply_FormatsSpeedAndArea_AndRtkStatus()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot
        {
            AvgSpeed = 8.53,
            WorkedAreaTotalM2 = 12345,
            FixQuality = 4,
            IsJobStarted = true,
            TracksTotal = 23,
            TrackIdx = 10
        });
        Assert.That(vm.SpeedText, Is.EqualTo("8,5"));       // coma decimal, 1 dígito
        Assert.That(vm.HaText, Is.EqualTo("1,2"));          // 12345 m² → 1,2 ha
        Assert.That(vm.GpsText, Is.EqualTo("RTK FIJO"));    // fix 4
        Assert.That(vm.LoteEnabled, Is.True);
        Assert.That(vm.LineVisible, Is.True);
        Assert.That(vm.LineBadge, Is.EqualTo("11/23"));     // (idx+1)/total
    }

    [Test]
    public void Apply_NoFix_ShowsSinFix()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot { FixQuality = 0, TrackIdx = -1 });
        Assert.That(vm.GpsText, Is.EqualTo("SIN FIX"));
        Assert.That(vm.LineVisible, Is.False);
    }
}
