using System.Globalization;
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

public class NmeaBuilderTests
{
    private static GpsEstado Fix() => new()
    {
        TimeNow = "123519.000,",
        LatNmea = 5323.1633840,
        NS = 'N',
        LonNmea = 11109.6028200,
        EW = 'W',
        Latitude = 53.4360564,
        Longitude = -111.160047,
        HeadingDeg = 87.65432,
        SpeedKnots = 4.5,
        RollDeg = 1.5,
        HeadingImu = 876,
        RollImu = 15,
    };

    // Recalcula el XOR entre '$' y '*' y lo compara con los 2 hex del final.
    private static void AssertChecksumValido(string s)
    {
        Assert.That(s, Does.EndWith("\r\n"));
        int ast = s.IndexOf('*');
        Assert.That(ast, Is.GreaterThan(0));
        int sum = 0;
        for (int i = 1; i < ast; i++) sum ^= s[i];
        Assert.That(s.Substring(ast + 1, 2), Is.EqualTo(sum.ToString("X2")));
    }

    [Test]
    public void Gga_lleva_lat_lon_y_constantes_historicas()
    {
        string s = NmeaBuilder.BuildGga(Fix());
        Assert.That(s, Does.StartWith(
            "$GPGGA,123519.000,5323.1633840,N,11109.6028200,W,8,12,0.9,1000,M,46.9,M,37.1,,*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Vtg_lleva_rumbo_y_velocidad_en_nudos_y_kmh()
    {
        string s = NmeaBuilder.BuildVtg(Fix());
        Assert.That(s, Does.StartWith("$GPVTG,87.65432,T,034.4,M,4.5,N,8.334,K*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Hdt_lleva_solo_el_rumbo()
    {
        string s = NmeaBuilder.BuildHdt(Fix());
        Assert.That(s, Does.StartWith("$GNHDT,87.65432,T*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Avr_lleva_rumbo_y_roll()
    {
        string s = NmeaBuilder.BuildAvr(Fix());
        Assert.That(s, Does.StartWith("$PTNL,AVR,123519.000,87.65432,Yaw,-2.1,Tilt,1.5,Roll,444.232,3,1.2,17*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Ogi_lleva_fix_velocidad_rumbo_y_roll()
    {
        string s = NmeaBuilder.BuildOgi(Fix());
        Assert.That(s, Does.StartWith(
            "$PAOGI,123519.000,5323.1633840,N,11109.6028200,W,8,12,0.9,1000,3.2,4.5,87.65432,1.5,0.12,359.9,T*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Nda_lleva_imu_en_decimas()
    {
        string s = NmeaBuilder.BuildNda(Fix());
        Assert.That(s, Does.StartWith(
            "$PANDA,123519.000,5323.1633840,N,11109.6028200,W,8,12,0.9,1000,3.2,4.5,876,15,32,298*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Rmc_lleva_fix_y_velocidad()
    {
        string s = NmeaBuilder.BuildRmc(Fix());
        Assert.That(s, Does.StartWith(
            "$GPRMC,123519.000,A,5323.1633840,N,11109.6028200,W,4.5,87.65432,230394,359.9*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Ksxt_usa_grados_crudos_y_checksum_fijo_historico()
    {
        // KSXT en ModSim nunca calculó checksum real: viaja el literal 3FCF0C9B.
        // El formato "0000.0000000" fuerza 4 dígitos enteros: -111.16 → "-0111.1600470".
        string s = NmeaBuilder.BuildKsxt(Fix());
        Assert.That(s, Does.StartWith("$KSXT,123519.000,-0111.1600470,0053.4360564,300,87.65432,22,35,4.5,1.5,3,3,13,-1075,-98,-8,,,,37,13,,*3FCF0C9B"));
        Assert.That(s, Does.EndWith("\r\n"));
    }

    [Test]
    public void Nda_sin_imu_manda_los_campos_neutros()
    {
        // IMU apagado (la ECU real trae el suyo): PANDA viaja con la
        // convención "sin IMU" que el engine ignora — heading 65535 (ushort
        // max), roll/pitch/yaw 32767 (short max).
        var g = Fix();
        g.ImuValido = false;
        string s = NmeaBuilder.BuildNda(g);
        Assert.That(s, Does.StartWith(
            "$PANDA,123519.000,5323.1633840,N,11109.6028200,W,8,12,0.9,1000,3.2,4.5,65535,32767,32767,32767*"));
    }

    [Test]
    public void Lat_lon_sur_oeste_van_en_valor_absoluto()
    {
        var g = Fix();
        g.LatNmea = -3359.9994600; g.NS = 'S';
        g.LonNmea = -6023.9997000; g.EW = 'W';
        string s = NmeaBuilder.BuildGga(g);
        Assert.That(s, Does.Contain(",3359.9994600,S,"));
        Assert.That(s, Does.Contain(",06023.9997000,W,"));
    }
}
