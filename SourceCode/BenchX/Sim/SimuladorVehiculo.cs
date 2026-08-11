using System;

namespace BenchX.Sim;

// Cinemática del tractor simulado — port fiel del simTimer_Tick de ModSim.
// Un tick = 100 ms. La fórmula de giro (tan(deg*0.02)/2.5) no es física de
// verdad: es la histórica de ModSim y PilotX está afinado contra ella.
public sealed class SimuladorVehiculo
{
    private const double ToRadians = 0.01745329251994329576923690768489;
    private const double ToDegrees = 57.295779513082325225835265587528;

    public double Latitude, Longitude;   // grados
    public double HeadingRad;            // 0..2π
    public double SpeedKmh;              // entrada (slider)
    public double SteerAngleDeg;         // entrada (slider o setpoint de PilotX)
    public double RollDeg;               // entrada (slider)

    public double HeadingDeg => HeadingRad * ToDegrees;
    public double SpeedKnots { get; private set; }
    public GpsEstado Estado { get; } = new();

    public void Avanzar()
    {
        // metros por tick: kmh/3.6 * 0.1 s (en ModSim: tbar(=kmh*10)*0.02777*0.1)
        double paso = SpeedKmh * 0.027777777777;

        HeadingRad += paso * Math.Tan(SteerAngleDeg * 0.02) / 2.5;
        if (HeadingRad > 2.0 * Math.PI) HeadingRad -= 2.0 * Math.PI;
        if (HeadingRad < 0) HeadingRad += 2.0 * Math.PI;

        AvanzarPosicion(ToRadians * Latitude, ToRadians * Longitude, HeadingRad, paso / 1000.0);

        SpeedKnots = Math.Round(1.944 * paso / 0.1, 1);

        Estado.Latitude = Latitude;
        Estado.Longitude = Longitude;
        Estado.HeadingDeg = HeadingDeg;
        Estado.SpeedKnots = SpeedKnots;
        Estado.RollDeg = RollDeg;
        Estado.RollImu = (int)(RollDeg * 10);
        Estado.HeadingImu = (int)(HeadingDeg * 10);
    }

    // Port de CalculateNewPostionFromBearingDistance (distancia en km, R=6371).
    private void AvanzarPosicion(double lat, double lng, double rumbo, double distanciaKm)
    {
        double r = distanciaKm / 6371.0;

        double lat2 = Math.Asin((Math.Sin(lat) * Math.Cos(r)) + (Math.Cos(lat) * Math.Sin(r) * Math.Cos(rumbo)));
        double lon2 = lng + Math.Atan2(Math.Sin(rumbo) * Math.Sin(r) * Math.Cos(lat), Math.Cos(r) - (Math.Sin(lat) * Math.Sin(lat2)));

        Latitude = ToDegrees * lat2;
        Longitude = ToDegrees * lon2;

        double latMinu = Latitude, lonMinu = Longitude;
        double latDeg = (int)Latitude, lonDeg = (int)Longitude;
        latMinu -= latDeg; lonMinu -= lonDeg;
        latMinu = Math.Round(latMinu * 60.0, 7);
        lonMinu = Math.Round(lonMinu * 60.0, 7);

        Estado.LatNmea = latMinu + (latDeg * 100.0);
        Estado.LonNmea = lonMinu + (lonDeg * 100.0);
        Estado.NS = Latitude >= 0 ? 'N' : 'S';
        Estado.EW = Longitude >= 0 ? 'E' : 'W';
    }
}
