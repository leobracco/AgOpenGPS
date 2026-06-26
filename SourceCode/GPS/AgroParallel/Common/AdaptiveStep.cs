// ============================================================================
// AdaptiveStep.cs - paso variable segun escala del valor.
//
// Mismo criterio que wwwroot/js/steps.js (mantener tablas en sync).
//   · GetDoseStep(5)   = 0.1   (kg/ha bajo → ajuste fino de 100 g)
//   · GetDoseStep(100) = 0.5   (kg/ha alto → ajuste grueso de 500 g)
//   · GetPidStep(0.3)  = 0.01
//   · GetPidStep(80)   = 1
//
// Uso pensado: botones +/- de dosis manual en el panel del piloto y cualquier
// otro spinner WinForms que toque magnitudes con rango ancho.
// ============================================================================

using System;

namespace AgroParallel.Common
{
    public static class AdaptiveStep
    {
        // Devuelve el paso de ajuste para una dosis en kg/ha (o L/ha, misma escala).
        public static double GetDoseStep(double value)
        {
            double v = Math.Abs(value);
            if (v < 10)  return 0.1;    //   0–10  → 100 g
            if (v < 50)  return 0.25;   //  10–50  → 250 g
            if (v < 150) return 0.5;    //  50–150 → 500 g
            return 1.0;                 // 150+    →   1 kg
        }

        // Paso de ajuste para una ganancia PID (Kp/Ki/Kd).
        public static double GetPidStep(double value)
        {
            double v = Math.Abs(value);
            if (v < 1)   return 0.01;
            if (v < 10)  return 0.1;
            if (v < 100) return 1.0;
            return 5.0;
        }

        // Suma/resta un paso adaptativo y hace snap al multiplo del paso
        // (evita drift de coma flotante cuando el usuario clickea muchas veces).
        public static double BumpDose(double value, int direction)
        {
            return Bump(value, direction, GetDoseStep(value));
        }

        public static double BumpPid(double value, int direction)
        {
            return Bump(value, direction, GetPidStep(value));
        }

        private static double Bump(double value, int direction, double step)
        {
            double next = value + Math.Sign(direction) * step;
            if (next < 0) next = 0;
            // Snap al multiplo del paso.
            return Math.Round(next / step) * step;
        }

        // Decimales razonables para mostrar un valor dado su paso.
        public static int DecimalsFor(double step)
        {
            if (step >= 1)    return 0;
            if (step >= 0.1)  return 1;
            if (step >= 0.01) return 2;
            return 3;
        }
    }
}
