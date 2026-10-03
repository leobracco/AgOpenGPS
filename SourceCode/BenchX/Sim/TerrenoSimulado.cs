using System;

namespace BenchX.Sim;

// Relieve de mentira para probar el registro de alturas de PilotX
// (planimetría): la altitud depende SOLO de la posición, así que pasar dos
// veces por el mismo lugar da la misma altura — que es lo que hay que poder
// verificar en el mapa de alturas. Lomas suaves de ±2 m en ~300 m: nada que el
// filtro de saltos de PilotX (0,5 m en 5 m) deba descartar.
public static class TerrenoSimulado
{
    public const double AlturaBase = 100.0;

    /// <summary>Altitud de la ANTENA (m) en esa lat/lon: suelo + alturaAntenaM.</summary>
    public static double Altitud(double latitud, double longitud, double alturaAntenaM = 3.0)
    {
        double norteM = latitud * 111320.0;
        double esteM = longitud * 111320.0 * Math.Cos(latitud * Math.PI / 180.0);
        double suelo = AlturaBase
            + 2.0 * Math.Sin(2.0 * Math.PI * esteM / 300.0)
            + 1.0 * Math.Cos(2.0 * Math.PI * norteM / 220.0);
        return Math.Round(suelo + alturaAntenaM, 3);
    }
}
