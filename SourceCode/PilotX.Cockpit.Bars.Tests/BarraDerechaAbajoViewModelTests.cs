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
    public void Abajo_FlagImageAndHyd()
    {
        var vm = new BarraAbajoViewModel(Cmd());
        vm.Apply(new CockpitSnapshot { FlagColor = 1, HasHydLift = true, HasHeadland = true, IsHeadlandOn = false });
        Assert.That(vm.FlagImg, Is.EqualTo("barra-abajo/FlagGrn.png"));
        Assert.That(vm.HydVisible, Is.True);
        Assert.That(vm.HydEnabled, Is.False);
    }

    [Test]
    public void Derecha_PilotoImage_ReflectsSteerAndSnap()
    {
        var vm = new BarraDerechaViewModel(Cmd());
        vm.Apply(new CockpitSnapshot { IsAutoSteerOn = true, IsAutoSnapToPivot = true, TrackIdx = 0 });
        Assert.That(vm.PilotoImg, Is.EqualTo("barra-derecha/AutoSteerOnSnapToPivot.png"));
        Assert.That(vm.PilotoEnabled, Is.True);
    }
}
