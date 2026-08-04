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
            ToolWidth = 4.0
        });
        Assert.That(vm.SpeedText, Is.EqualTo("8,5"));       // coma decimal, 1 dígito
        Assert.That(vm.HaText, Is.EqualTo("1,2"));          // 12345 m² → 1,2 ha
        Assert.That(vm.GpsText, Is.EqualTo("RTK FIJO"));    // fix 4
        Assert.That(vm.LoteEnabled, Is.True);
        Assert.That(vm.HaHoraVisible, Is.True);
        Assert.That(vm.HaHoraText, Is.EqualTo("3,4"));      // 4,0 m x 8,53 km/h x 0,1
    }

    [Test]
    public void Apply_NoFix_ShowsSinFix()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot { FixQuality = 0, IsJobStarted = false });
        Assert.That(vm.GpsText, Is.EqualTo("SIN FIX"));
        Assert.That(vm.HaHoraVisible, Is.False);
    }
}
