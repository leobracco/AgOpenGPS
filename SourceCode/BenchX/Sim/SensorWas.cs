using System;

namespace BenchX.Sim;

// WAS analógico (potenciómetro al ADS1115, entrada single) de la placa AiO
// con firmware Keya.hex. Dos mitades:
//   · LeerCrudo: lo FÍSICO — el sensor real convierte el ángulo de la rueda
//     en cuentas del ADC, con los errores de montaje que se le inyecten.
//   · AnguloEcu: lo que hace el FIRMWARE con esas cuentas usando lo que
//     PilotX le mandó por PGN 252/251 (cuentas/grado, offset, Ackermann,
//     invertir WAS). Port de Autosteer.ino ~455-478 (AIO_v4 Keya CANBUS).
// La calibración de PilotX "anda" cuando lo segundo deshace lo primero.
public sealed class SensorWas
{
    // Media escala del ADS1115 después del >>1 del firmware (0..13610 = 0..5 V).
    public const int Centro = 6805;
    public const int FondoEscala = 13610;

    // Cuentas por grado del sensor real SIN errores. 110 = default de PilotX
    // (setAS_countsPerDegree), así sin errores inyectados el banco ya está
    // calibrado. Es una propiedad del sensor: PilotX no la cambia.
    public double CuentasPorGrado = 110;

    // Errores de montaje inyectables.
    public double OffsetGrados;          // el sensor lee 0 con la rueda torcida
    public double GananciaPct;           // ±% de cuentas/grado respecto del nominal
    public double AckermannPct = 100;    // % de cuentas del lado IZQUIERDO (ángulo < 0) respecto del derecho
    public double RuidoGrados;           // desvío estándar del ruido, en grados
    public bool Invertido;               // sensor montado al revés: cuentas crecen hacia la izquierda

    private readonly Random _rnd;

    public SensorWas(int semilla = 1234) => _rnd = new Random(semilla);

    public int LeerCrudo(double anguloFisicoGrados)
    {
        double cpg = CuentasPorGrado * (1.0 + GananciaPct / 100.0);
        double ang = anguloFisicoGrados + OffsetGrados;
        if (RuidoGrados > 0) ang += Gauss() * RuidoGrados;
        double cuentas = ang * cpg;
        if (ang < 0) cuentas *= AckermannPct / 100.0;
        if (Invertido) cuentas = -cuentas;
        return (int)Math.Clamp(Math.Round(Centro + cuentas), 0, FondoEscala);
    }

    // Conversión del firmware con los settings que mandó PilotX.
    public static double AnguloEcu(int crudo, PgnProcessor p)
    {
        // El firmware divide sin guardia; acá un 0 no puede tirar abajo el banco.
        double counts = p.SensorCounts <= 0 ? 1 : p.SensorCounts;
        int offset = (short)p.WasOffset;   // int16 en el firmware; el PgnProcessor lo guarda sin signo
        double a = p.InvertWas != 0
            ? (crudo - Centro - offset) / -counts
            : (crudo - Centro + offset) / counts;
        if (a < 0) a *= p.AckermanPct * 0.01;   // "Ackerman fix": solo el lado izquierdo
        return a;
    }

    private double Gauss()
    {
        // Box-Muller
        double u1 = 1.0 - _rnd.NextDouble(), u2 = _rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
