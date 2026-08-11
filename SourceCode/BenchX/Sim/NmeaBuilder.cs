using System.Globalization;
using System.Text;

namespace BenchX.Sim;

// Port fiel del generador NMEA de ModSim (Controls.Designer.cs). Las
// constantes raras (fix 8, 12 sats, "1000", "034.4,M", "444.232,3,1.2,17",
// "32,298", "230394,359.9") son las que SIEMPRE mandó ModSim y PilotX acepta:
// no "corregirlas", son parte del wire.
public static class NmeaBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // XOR de los chars entre '$' y '*', como hex de 2 dígitos.
    public static string Checksum(string sentencia)
    {
        int sum = 0;
        for (int i = 1; i < sentencia.Length && sentencia[i] != '*'; i++)
            sum ^= sentencia[i];
        return sum.ToString("X2", Inv);
    }

    private static string Cerrar(StringBuilder sb)
    {
        sb.Append(Checksum(sb.ToString()));
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static string Lat(GpsEstado g) => System.Math.Abs(g.LatNmea).ToString("0000.0000000", Inv);
    private static string LonAncho(GpsEstado g) => System.Math.Abs(g.LonNmea).ToString("00000.0000000", Inv);
    private static string LonCorto(GpsEstado g) => System.Math.Abs(g.LonNmea).ToString("0000.0000000", Inv);

    public static string BuildGga(GpsEstado g)
    {
        var sb = new StringBuilder("$GPGGA,");
        sb.Append(g.TimeNow)
          .Append(Lat(g)).Append(',').Append(g.NS).Append(',')
          .Append(LonAncho(g)).Append(',').Append(g.EW).Append(',')
          .Append("8,12,0.9,1000,M,46.9,M,37.1,,*");
        return Cerrar(sb);
    }

    public static string BuildVtg(GpsEstado g)
    {
        var sb = new StringBuilder("$GPVTG,");
        sb.Append(g.HeadingDeg.ToString("N5", Inv))
          .Append(",T,034.4,M,")
          .Append(g.SpeedKnots.ToString(Inv))
          .Append(",N,")
          .Append((g.SpeedKnots * 1.852).ToString(Inv))
          .Append(",K*");
        return Cerrar(sb);
    }

    public static string BuildHdt(GpsEstado g)
    {
        var sb = new StringBuilder("$GNHDT,");
        sb.Append(g.HeadingDeg.ToString("N5", Inv)).Append(",T*");
        return Cerrar(sb);
    }

    public static string BuildAvr(GpsEstado g)
    {
        var sb = new StringBuilder("$PTNL,AVR,");
        sb.Append(g.TimeNow)
          .Append(g.HeadingDeg.ToString("N5", Inv))
          .Append(",Yaw,-2.1,Tilt,")
          .Append(g.RollDeg.ToString(Inv)).Append(",Roll,")
          .Append("444.232,3,1.2,17*");
        return Cerrar(sb);
    }

    public static string BuildOgi(GpsEstado g)
    {
        var sb = new StringBuilder("$PAOGI,");
        sb.Append(g.TimeNow)
          .Append(Lat(g)).Append(',').Append(g.NS).Append(',')
          .Append(LonCorto(g)).Append(',').Append(g.EW).Append(',')
          .Append("8,12,0.9,1000,3.2,")
          .Append(g.SpeedKnots.ToString(Inv)).Append(',')
          .Append(g.HeadingDeg.ToString("N5", Inv)).Append(',')
          .Append(g.RollDeg.ToString(Inv)).Append(",0.12,359.9,T*");
        return Cerrar(sb);
    }

    public static string BuildNda(GpsEstado g)
    {
        var sb = new StringBuilder("$PANDA,");
        sb.Append(g.TimeNow)
          .Append(Lat(g)).Append(',').Append(g.NS).Append(',')
          .Append(LonCorto(g)).Append(',').Append(g.EW).Append(',')
          .Append("8,12,0.9,1000,3.2,")
          .Append(g.SpeedKnots.ToString(Inv)).Append(',')
          .Append(g.HeadingImu.ToString(Inv)).Append(',')
          .Append(g.RollImu.ToString(Inv)).Append(",32,298*");
        return Cerrar(sb);
    }

    public static string BuildRmc(GpsEstado g)
    {
        var sb = new StringBuilder("$GPRMC,");
        sb.Append(g.TimeNow).Append("A,")
          .Append(Lat(g)).Append(',').Append(g.NS).Append(',')
          .Append(LonCorto(g)).Append(',').Append(g.EW).Append(',')
          .Append(g.SpeedKnots.ToString(Inv)).Append(',')
          .Append(g.HeadingDeg.ToString("N5", Inv))
          .Append(",230394,359.9*");
        return Cerrar(sb);
    }

    public static string BuildKsxt(GpsEstado g)
    {
        // ModSim nunca calculó el checksum del KSXT: manda el literal 3FCF0C9B.
        var sb = new StringBuilder("$KSXT,");
        sb.Append(g.TimeNow)
          .Append(g.Longitude.ToString("0000.0000000", Inv)).Append(',')
          .Append(g.Latitude.ToString("0000.0000000", Inv)).Append(',')
          .Append(g.Altitude.ToString(Inv)).Append(',')
          .Append(g.HeadingDeg.ToString("N5", Inv))
          .Append(",22,35,")
          .Append(g.SpeedKnots.ToString(Inv)).Append(',')
          .Append(g.RollDeg.ToString(Inv))
          .Append(",3,3,13,-1075,-98,-8,,,,37,13,,")
          .Append("*3FCF0C9B\r\n");
        return sb.ToString();
    }
}
