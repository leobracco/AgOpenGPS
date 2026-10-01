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

    // --- Física realista (checkbox de BenchX). Apagada = ModSim, idéntico. ---
    // Encendida: bicicleta ω = v·tan(δ)/L sobre el eje trasero (pivote), en
    // un plano local (este/norte en m) anclado donde estaba la antena al
    // prenderla; la antena va AntenaAdelanteM por delante del eje trasero.
    public bool FisicaRealista;
    public double DistanciaEntreEjesM = 3.3;   // setVehicle_wheelbase de PilotX
    public double AntenaAdelanteM = 0.1;       // setVehicle_antennaPivot de PilotX
    public double PivotEste { get; private set; }    // eje trasero, m desde el origen local
    public double PivotNorte { get; private set; }
    public double PasoM { get; private set; }        // metros recorridos en el último tick
    private bool _planoListo;
    private double _lat0, _lon0;
    private const double RadioTierraM = 6371000.0;

    // Tras mover Latitude/Longitude a mano con la física realista prendida
    // (posición de arranque, demo): el plano local se vuelve a anclar ahí.
    public void Reanclar() => _planoListo = false;

    public void Avanzar()
    {
        if (FisicaRealista) { AvanzarRealista(); return; }
        _planoListo = false;

        // metros por tick: kmh/3.6 * 0.1 s (en ModSim: tbar(=kmh*10)*0.02777*0.1)
        double paso = SpeedKmh * 0.027777777777;
        PasoM = paso;

        HeadingRad += paso * Math.Tan(SteerAngleDeg * 0.02) / 2.5;
        if (HeadingRad > 2.0 * Math.PI) HeadingRad -= 2.0 * Math.PI;
        if (HeadingRad < 0) HeadingRad += 2.0 * Math.PI;

        AvanzarPosicion(ToRadians * Latitude, ToRadians * Longitude, HeadingRad, paso / 1000.0);

        SpeedKnots = Math.Round(1.944 * paso / 0.1, 1);
        CopiarEstado();
    }

    private void CopiarEstado()
    {
        Estado.Latitude = Latitude;
        Estado.Longitude = Longitude;
        Estado.HeadingDeg = HeadingDeg;
        Estado.SpeedKnots = SpeedKnots;
        Estado.RollDeg = RollDeg;
        Estado.RollImu = (int)(RollDeg * 10);
        Estado.HeadingImu = (int)(HeadingDeg * 10);
    }

    private void AvanzarRealista()
    {
        if (!_planoListo)
        {
            // Origen = antena actual; el eje trasero queda atrás sobre el rumbo.
            _lat0 = Latitude; _lon0 = Longitude;
            PivotEste = -Math.Sin(HeadingRad) * AntenaAdelanteM;
            PivotNorte = -Math.Cos(HeadingRad) * AntenaAdelanteM;
            _planoListo = true;
        }

        double paso = SpeedKmh / 3.6 * 0.1;   // m por tick de 100 ms
        PasoM = paso;
        double l = DistanciaEntreEjesM > 0.1 ? DistanciaEntreEjesM : 0.1;
        double dh = paso * Math.Tan(SteerAngleDeg * ToRadians) / l;

        // Avance exacto sobre el arco: cuerda 2R·sin(dh/2) en la dirección media.
        double cuerda = Math.Abs(dh) < 1e-9 ? paso : 2.0 * (paso / dh) * Math.Sin(dh / 2.0);
        double rumboMedio = HeadingRad + dh / 2.0;
        PivotEste += cuerda * Math.Sin(rumboMedio);
        PivotNorte += cuerda * Math.Cos(rumboMedio);

        HeadingRad += dh;
        if (HeadingRad >= 2.0 * Math.PI) HeadingRad -= 2.0 * Math.PI;
        if (HeadingRad < 0) HeadingRad += 2.0 * Math.PI;

        double antE = PivotEste + Math.Sin(HeadingRad) * AntenaAdelanteM;
        double antN = PivotNorte + Math.Cos(HeadingRad) * AntenaAdelanteM;
        double lat = _lat0 + antN / RadioTierraM * ToDegrees;
        double lon = _lon0 + antE / (RadioTierraM * Math.Cos(_lat0 * ToRadians)) * ToDegrees;
        FijarPosicion(lat, lon);

        SpeedKnots = Math.Round(1.944 * paso / 0.1, 1);
        CopiarEstado();
    }

    // Port de CalculateNewPostionFromBearingDistance (distancia en km, R=6371).
    private void AvanzarPosicion(double lat, double lng, double rumbo, double distanciaKm)
    {
        double r = distanciaKm / 6371.0;

        double lat2 = Math.Asin((Math.Sin(lat) * Math.Cos(r)) + (Math.Cos(lat) * Math.Sin(r) * Math.Cos(rumbo)));
        double lon2 = lng + Math.Atan2(Math.Sin(rumbo) * Math.Sin(r) * Math.Cos(lat), Math.Cos(r) - (Math.Sin(lat) * Math.Sin(lat2)));

        FijarPosicion(ToDegrees * lat2, ToDegrees * lon2);
    }

    private void FijarPosicion(double latitud, double longitud)
    {
        Latitude = latitud;
        Longitude = longitud;

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
