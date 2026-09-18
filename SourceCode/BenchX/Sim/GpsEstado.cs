namespace BenchX.Sim;

// Foto del fix que consumen las sentencias NMEA. La llena SimuladorVehiculo
// en cada tick; TimeNow la pone el ViewModel (hora real, no testeable adentro).
public sealed class GpsEstado
{
    public string TimeNow = "";        // "HHmmss.fff," — la coma final viaja, igual que en ModSim
    public double LatNmea, LonNmea;    // grados*100 + minutos (formato ddmm.mmmmmmm)
    public char NS = 'N', EW = 'W';
    public double Latitude, Longitude; // grados con signo (solo KSXT los usa crudos)
    public double HeadingDeg;
    public double SpeedKnots;
    public double RollDeg;
    public double Altitude = 300;      // solo KSXT; GGA/OGI/NDA llevan el "1000" fijo histórico
    public int HeadingImu, RollImu;    // grados*10, para PANDA
    public bool ImuValido = true;      // false = PANDA con 65535/32767 ("sin IMU", el engine los ignora)
}
