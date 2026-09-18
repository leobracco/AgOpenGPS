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

    // El menú muestra guías SALTEADAS (0 = contigua); el motor habla en ANCHO
    // (rowSkipsWidth, 1 = contigua). display = ancho − 1. Sin esta conversión,
    // elegir 0 hacía que el combo saltara solo a 1 en el próximo snapshot.
    [Test]
    public void Derecha_SaltoDelGiro_MuestraSalteadasNoAncho()
    {
        var vm = new BarraDerechaViewModel(Cmd());

        vm.Apply(new CockpitSnapshot { YouTurnSkipWidth = 1 });   // contigua
        Assert.That(vm.SaltoDelGiro, Is.EqualTo(0));

        vm.Apply(new CockpitSnapshot { YouTurnSkipWidth = 3 });   // saltea 2
        Assert.That(vm.SaltoDelGiro, Is.EqualTo(2));

        vm.Apply(new CockpitSnapshot { YouTurnSkipWidth = 10 });  // tope
        Assert.That(vm.SaltoDelGiro, Is.EqualTo(9));

        // Ancho fuera de rango (motor recién arrancado, 0) no rompe el combo.
        vm.Apply(new CockpitSnapshot { YouTurnSkipWidth = 0 });
        Assert.That(vm.SaltoDelGiro, Is.EqualTo(0));
    }
}
