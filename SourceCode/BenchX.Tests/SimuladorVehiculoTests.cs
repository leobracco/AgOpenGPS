using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

public class SimuladorVehiculoTests
{
    private static SimuladorVehiculo Sim() => new()
    {
        Latitude = -33.0, Longitude = -60.0, HeadingRad = 0.0,
    };

    [Test]
    public void Derecho_al_norte_a_36kmh_avanza_1m_por_tick()
    {
        // 36 km/h = 10 m/s → 1 m por tick de 100 ms → ~8.9932e-6° de latitud (R=6371 km)
        var s = Sim();
        s.SpeedKmh = 36;
        s.Avanzar();
        Assert.That(s.Latitude, Is.EqualTo(-33.0 + 8.9932e-6).Within(1e-8));
        Assert.That(s.Longitude, Is.EqualTo(-60.0).Within(1e-9));
        Assert.That(s.HeadingRad, Is.EqualTo(0).Within(1e-12));
    }

    [Test]
    public void La_velocidad_nmea_sale_en_nudos_redondeada()
    {
        var s = Sim();
        s.SpeedKmh = 36;
        s.Avanzar();
        Assert.That(s.SpeedKnots, Is.EqualTo(19.4).Within(1e-9)); // 36 km/h ≈ 19.44 kn, round a 1 decimal
    }

    [Test]
    public void El_angulo_de_direccion_gira_el_rumbo_con_la_formula_de_modsim()
    {
        // heading += paso_m * tan(deg*0.02) / 2.5 — fórmula histórica de ModSim
        var s = Sim();
        s.SpeedKmh = 36; s.SteerAngleDeg = 30;
        s.Avanzar();
        Assert.That(s.HeadingRad, Is.EqualTo(System.Math.Tan(30 * 0.02) / 2.5).Within(1e-9));
    }

    [Test]
    public void El_rumbo_envuelve_en_2pi()
    {
        var s = Sim();
        s.HeadingRad = 6.28; s.SpeedKmh = 36; s.SteerAngleDeg = 30;
        s.Avanzar();
        Assert.That(s.HeadingRad, Is.LessThan(2 * System.Math.PI));
        Assert.That(s.HeadingRad, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void El_estado_gps_queda_listo_para_nmea()
    {
        var s = Sim();
        s.SpeedKmh = 36; s.RollDeg = -2.5;
        s.Avanzar();
        Assert.That(s.Estado.NS, Is.EqualTo('S'));
        Assert.That(s.Estado.EW, Is.EqualTo('W'));
        // -32.99999... → grados -32, minutos ≈ -59.99946
        Assert.That(System.Math.Abs(s.Estado.LatNmea), Is.EqualTo(3259.99946).Within(0.001));
        // -60° exacto cae en el borde del grado: el roundtrip rad↔deg deja
        // -59.999..., y el split entero de ModSim lo representa como
        // 59°60.0' → 5960.0. Paridad con el original, no un bug.
        Assert.That(System.Math.Abs(s.Estado.LonNmea), Is.EqualTo(5960.0).Within(0.001));
        Assert.That(s.Estado.RollDeg, Is.EqualTo(-2.5));
        Assert.That(s.Estado.RollImu, Is.EqualTo(-25));
        Assert.That(s.Estado.HeadingImu, Is.EqualTo(0));
        Assert.That(s.Estado.TimeNow, Is.EqualTo("")); // la hora la pone el ViewModel
    }

    [Test]
    public void Marcha_atras_retrocede()
    {
        var s = Sim();
        s.SpeedKmh = -10;
        s.Avanzar();
        Assert.That(s.Latitude, Is.LessThan(-33.0));
    }
}
