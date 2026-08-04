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
            TrackIdx = 10,
            ToolWidth = 7.0
        });
        Assert.That(vm.SpeedText, Is.EqualTo("8,5"));       // coma decimal, 1 dígito
        Assert.That(vm.HaText, Is.EqualTo("1,2"));          // 12345 m² → 1,2 ha
        Assert.That(vm.GpsText, Is.EqualTo("RTK FIJO"));    // fix 4
        Assert.That(vm.LoteEnabled, Is.True);
        // El contador de guías ("11/23") se fue del centro de la barra superior
        // y en su lugar va el ritmo de trabajo: ancho x velocidad x 0,1.
        Assert.That(vm.HaHoraVisible, Is.True);
        Assert.That(vm.HaHoraText, Is.EqualTo("6,0"));      // 7,0 m x 8,53 km/h x 0,1
    }

    [Test]
    public void Apply_NoFix_ShowsSinFix()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot { FixQuality = 0, TrackIdx = -1 });
        Assert.That(vm.GpsText, Is.EqualTo("SIN FIX"));
        // Sin lote ni ancho configurado el ritmo se oculta, en vez de mostrar un
        // cero fijo que parece un dato roto.
        Assert.That(vm.HaHoraVisible, Is.False);
    }

    [Test]
    public void Apply_SinLote_OcultaElRitmo()
    {
        // Con ancho cargado pero el lote cerrado, ha/h no significa nada.
        var vm = Make();
        vm.Apply(new CockpitSnapshot { IsJobStarted = false, ToolWidth = 7.0, AvgSpeed = 8.0 });
        Assert.That(vm.HaHoraVisible, Is.False);
    }
}
