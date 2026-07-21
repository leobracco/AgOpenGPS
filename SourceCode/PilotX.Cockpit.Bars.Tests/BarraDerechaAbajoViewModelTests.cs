using NUnit.Framework;
using System.Net.Http;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Tests;

public class BarraDerechaAbajoViewModelTests
{
    private static GuidanceCommandClient Cmd() => new(new HttpClient());

    [Test]
    public void Derecha_UturnVisibility_RequiresTrackNoContourAndBoundary()
    {
        var vm = new BarraDerechaViewModel(Cmd());
        vm.Apply(new CockpitSnapshot { TrackIdx = 3, IsContourOn = false, HasBoundary = true });
        Assert.That(vm.UturnVisible, Is.True);
        vm.Apply(new CockpitSnapshot { TrackIdx = 3, IsContourOn = true, HasBoundary = true });
        Assert.That(vm.UturnVisible, Is.False);
    }

    [Test]
    public void Abajo_FlagColorAndHyd()
    {
        var vm = new BarraAbajoViewModel(Cmd());
        vm.Apply(new CockpitSnapshot { FlagColor = 1, HasHydLift = true, HasHeadland = true, IsHeadlandOn = false });
        Assert.That(vm.FlagColorHex, Is.EqualTo("#4ABA3E"));
        Assert.That(vm.HydVisible, Is.True);
        Assert.That(vm.HydEnabled, Is.False);
    }
}
