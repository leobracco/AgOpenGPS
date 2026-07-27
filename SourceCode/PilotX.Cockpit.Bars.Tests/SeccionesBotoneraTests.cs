// Botonera de secciones de la barra inferior: que aparezcan tantos botones como
// secciones tenga el implemento y que cada uno se pinte por su ESTADO
// (Off/Auto/On), no por si está aplicando en este instante.
//
// La distinción importa: con `SectionOnRequest` sola, "Auto" y "On" dan las dos
// true y el operario no puede ver cuál dejó forzada a mano.

using NUnit.Framework;
using System.Net.Http;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Tests;

public class SeccionesBotoneraTests
{
    private static BarraAbajoViewModel Make() =>
        new(new GuidanceCommandClient(new HttpClient()));

    [Test]
    public void Apply_CreaUnBotonPorSeccion()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot
        {
            IsJobStarted = true,
            NumSections = 6,
            IsSectionsNotZones = true,
            SectionStates = new[] { 0, 1, 2, 0, 1, 2 },
        });

        Assert.That(vm.Secciones, Has.Count.EqualTo(6));
        Assert.That(vm.Secciones[0].Numero, Is.EqualTo(1), "los botones se numeran desde 1");
        Assert.That(vm.Secciones[5].Numero, Is.EqualTo(6));
        Assert.That(vm.SeccionesVisible, Is.True);
    }

    [Test]
    public void Apply_PintaCadaBotonSegunSuEstado()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot
        {
            IsJobStarted = true,
            IsSectionsNotZones = true,
            NumSections = 3,
            SectionStates = new[] { 0, 1, 2 },
        });

        Assert.That(vm.Secciones[0].Color, Is.EqualTo(SeccionBotonViewModel.ColorOff),  "Off = rojo");
        Assert.That(vm.Secciones[1].Color, Is.EqualTo(SeccionBotonViewModel.ColorAuto), "Auto = verde");
        Assert.That(vm.Secciones[2].Color, Is.EqualTo(SeccionBotonViewModel.ColorOn),   "On = ambar");
    }

    [Test]
    public void Apply_MandaElComandoDeLaSeccionCorrecta()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot { IsJobStarted = true, IsSectionsNotZones = true, NumSections = 4, SectionStates = new[] { 0, 0, 0, 0 } });

        Assert.That(vm.Secciones[2].Comando, Is.EqualTo("seccion_3"),
            "el 3er boton tiene que mandar seccion_3, no seccion_2");
    }

    [Test]
    public void Apply_ReusaLosBotones_SiNoCambioLaCantidad()
    {
        var vm = Make();
        var snap = new CockpitSnapshot { IsJobStarted = true, IsSectionsNotZones = true, NumSections = 3, SectionStates = new[] { 0, 0, 0 } };
        vm.Apply(snap);
        var primero = vm.Secciones[0];

        // Segundo tick con la misma cantidad: no debe recrear la lista (si la
        // recreara, la UI parpadearia 4 veces por segundo).
        vm.Apply(new CockpitSnapshot { IsJobStarted = true, IsSectionsNotZones = true, NumSections = 3, SectionStates = new[] { 1, 1, 1 } });

        Assert.That(vm.Secciones[0], Is.SameAs(primero), "no tiene que recrear los botones en cada tick");
        Assert.That(vm.Secciones[0].Color, Is.EqualTo(SeccionBotonViewModel.ColorAuto), "pero si actualizar el color");
    }

    [Test]
    public void Apply_SinLote_NoMuestraLaBotonera()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot { IsJobStarted = false, IsSectionsNotZones = true, NumSections = 8, SectionStates = new[] { 0, 0, 0, 0, 0, 0, 0, 0 } });

        Assert.That(vm.SeccionesVisible, Is.False,
            "sin lote abierto el control de secciones no aplica: igual que el nativo");
    }

    // ---- Modo ZONAS -------------------------------------------------------
    // El implemento puede estar configurado de dos formas (config.html, solapa
    // Secciones): individuales (<=16, cada una con su ancho) o por ZONAS (<=8
    // grupos). En modo zonas el operario NO maneja secciones sueltas: toca la
    // zona y se prende/apaga el grupo entero, y el comando es zona_<n>.

    [Test]
    public void Zonas_MuestraUnBotonPorZona_NoPorSeccion()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot
        {
            IsJobStarted = true,
            NumSections = 12,
            IsSectionsNotZones = false,
            // 3 zonas: secciones 1-4, 5-8, 9-12. El resto en 0 = no existen.
            ZoneRanges = new[] { 4, 8, 12, 0, 0, 0, 0, 0 },
            SectionStates = new[] { 1, 1, 1, 1, 0, 0, 0, 0, 2, 2, 2, 2 },
        });

        Assert.That(vm.Secciones, Has.Count.EqualTo(3), "3 zonas configuradas = 3 botones, no 12");
        Assert.That(vm.Secciones[0].Comando, Is.EqualTo("zona_1"));
        Assert.That(vm.Secciones[2].Comando, Is.EqualTo("zona_3"));
    }

    [Test]
    public void Zonas_ElColorSaleDeLaUltimaSeccionDeLaZona()
    {
        // Igual que btnZoneX_Click nativo, que lee section[zoneRanges[zona]-1].
        var vm = Make();
        vm.Apply(new CockpitSnapshot
        {
            IsJobStarted = true,
            NumSections = 12,
            IsSectionsNotZones = false,
            ZoneRanges = new[] { 4, 8, 12, 0, 0, 0, 0, 0 },
            SectionStates = new[] { 1, 1, 1, 1, 0, 0, 0, 0, 2, 2, 2, 2 },
        });

        Assert.That(vm.Secciones[0].Color, Is.EqualTo(SeccionBotonViewModel.ColorAuto), "zona 1 (secs 1-4) en Auto");
        Assert.That(vm.Secciones[1].Color, Is.EqualTo(SeccionBotonViewModel.ColorOff),  "zona 2 (secs 5-8) apagada");
        Assert.That(vm.Secciones[2].Color, Is.EqualTo(SeccionBotonViewModel.ColorOn),   "zona 3 (secs 9-12) forzada");
    }

    [Test]
    public void Zonas_IgnoraLasQueNoExisten()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot
        {
            IsJobStarted = true,
            NumSections = 8,
            IsSectionsNotZones = false,
            ZoneRanges = new[] { 8, 0, 0, 0, 0, 0, 0, 0 },   // una sola zona
            SectionStates = new[] { 1, 1, 1, 1, 1, 1, 1, 1 },
        });

        Assert.That(vm.Secciones, Has.Count.EqualTo(1), "zoneRanges en 0 = zona inexistente");
    }

    [Test]
    public void Zonas_SinRangos_NoRompe()
    {
        // Modo zonas declarado pero sin zone_ranges (backend viejo): no mostrar
        // botones en vez de mostrar 16 secciones que el operario no puede usar.
        var vm = Make();
        vm.Apply(new CockpitSnapshot
        {
            IsJobStarted = true,
            NumSections = 12,
            IsSectionsNotZones = false,
            ZoneRanges = null,
            SectionStates = new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        });

        Assert.That(vm.Secciones, Is.Empty);
        Assert.That(vm.SeccionesVisible, Is.False);
    }

    [Test]
    public void Apply_TolerarEstadosAusentes()
    {
        // El motor viejo (o un backend a medio actualizar) puede no mandar
        // section_states: no debe romper, todo queda en Off.
        var vm = Make();
        vm.Apply(new CockpitSnapshot { IsJobStarted = true, IsSectionsNotZones = true, NumSections = 2, SectionStates = null });

        Assert.That(vm.Secciones, Has.Count.EqualTo(2));
        Assert.That(vm.Secciones[0].Color, Is.EqualTo(SeccionBotonViewModel.ColorOff));
    }
}
