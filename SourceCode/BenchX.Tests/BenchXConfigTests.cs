using System.IO;
using BenchX.Config;
using NUnit.Framework;

namespace BenchX.Tests;

public class BenchXConfigTests
{
    private string _dir = "";

    [SetUp]
    public void CrearDir()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchx-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void BorrarDir() => Directory.Delete(_dir, true);

    private string Ruta => Path.Combine(_dir, "benchx.json");

    [Test]
    public void Sin_archivo_devuelve_defaults_de_banco_y_lo_crea()
    {
        var c = BenchXConfig.Cargar(Ruta);
        Assert.That((c.Subred1, c.Subred2, c.Subred3), Is.EqualTo(((byte)127, (byte)255, (byte)255)));
        Assert.That(c.Nda, Is.True);   // PANDA es la sentencia que usa PilotX
        Assert.That(c.Gga, Is.False);
        Assert.That(File.Exists(Ruta), Is.True);
    }

    [Test]
    public void Roundtrip_guardar_y_cargar()
    {
        var c = new BenchXConfig { Subred1 = 192, Subred2 = 168, Subred3 = 5, Latitud = -33.5, Longitud = -60.1, Gga = true, Nda = false, EmularDireccion = false, EmularImu = false };
        c.Guardar(Ruta);
        var c2 = BenchXConfig.Cargar(Ruta);
        Assert.That((c2.Subred1, c2.Subred2, c2.Subred3), Is.EqualTo(((byte)192, (byte)168, (byte)5)));
        Assert.That(c2.Latitud, Is.EqualTo(-33.5));
        Assert.That(c2.Gga, Is.True);
        Assert.That(c2.Nda, Is.False);
        Assert.That(c2.EmularDireccion, Is.False);
        Assert.That(c2.EmularMaquina, Is.True);   // default: emular todo
        Assert.That(c2.EmularImu, Is.False);
        Assert.That(c2.EmularGps, Is.True);
    }

    [Test]
    public void El_json_va_en_snake_case()
    {
        new BenchXConfig().Guardar(Ruta);
        Assert.That(File.ReadAllText(Ruta), Does.Contain("\"subred1\""));
    }

    [Test]
    public void Corrupto_vuelve_a_defaults_y_reescribe()
    {
        File.WriteAllText(Ruta, "{esto no es json");
        var c = BenchXConfig.Cargar(Ruta);
        Assert.That(c.Subred1, Is.EqualTo(127));
        Assert.That(File.ReadAllText(Ruta), Does.Contain("subred1"));
    }
}
