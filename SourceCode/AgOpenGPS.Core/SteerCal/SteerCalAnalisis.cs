// ============================================================================
// SteerCalAnalisis.cs — cuentas puras del asistente de calibración.
//
// Separadas de la máquina de estados para poder probarlas solas:
//   · RespuestaEscalon: subida, sobrepico y oscilación de un escalón (P6/P7).
//   · AnalisisPasadas: sesgo, RMS y período de oscilación del error a la
//     guía en las pasadas del ajuste fino (P8).
//   · Calibracion: cero promedio (P3), cuentas/grado y Ackermann contra el
//     modelo bicicleta δ = atan(L·ω/v) (P4) y la propuesta del ajuste fino.
// ============================================================================

#if NETCOREAPP
#nullable disable
#endif

using System;
using System.Collections.Generic;

namespace AgOpenGPS.SteerCal
{
    /// <summary>Resultado de un escalón de ángulo (objetivo fijo, rueda partiendo de "inicio").</summary>
    public sealed class RespuestaEscalon
    {
        /// <summary>Llegó al 90 % del salto.</summary>
        public bool Llego;
        /// <summary>Tiempo de subida 10→90 %, en segundos (NaN si no llegó).</summary>
        public double SubidaS = double.NaN;
        /// <summary>Cuánto se pasó del objetivo, en % del salto.</summary>
        public double SobrepicoPct;
        /// <summary>Después de llegar cruzó el objetivo de un lado a otro 2 veces o más.</summary>
        public bool Oscila;
        /// <summary>Fracción de la subida con el PWM pegado al tope alto.</summary>
        public double FraccionSaturado;

        /// <summary>Sin pasarse del 10 %, sin oscilar y llegando.</summary>
        public bool Bueno => Llego && SobrepicoPct < SteerCalWizard.SobrepicoMaxPct && !Oscila;

        /// <summary>
        /// t/angulo/pwm: muestras desde el instante del escalón. Banda de
        /// oscilación: cruces de más de 0,3° del objetivo para cada lado.
        /// </summary>
        public static RespuestaEscalon Analizar(IList<double> t, IList<double> angulo, IList<int> pwm,
                                               double inicio, double objetivo, int pwmAlto)
        {
            var r = new RespuestaEscalon();
            double salto = objetivo - inicio;
            if (t == null || angulo == null || t.Count == 0 || Math.Abs(salto) < 0.5) return r;

            double t10 = double.NaN, t90 = double.NaN, maxY = double.NegativeInfinity;
            int i90 = -1;
            for (int i = 0; i < angulo.Count; i++)
            {
                double y = (angulo[i] - inicio) / salto;
                if (y > maxY) maxY = y;
                if (double.IsNaN(t10) && y >= 0.1) t10 = t[i];
                if (double.IsNaN(t90) && y >= 0.9) { t90 = t[i]; i90 = i; }
            }

            r.Llego = !double.IsNaN(t90);
            if (r.Llego) r.SubidaS = t90 - (double.IsNaN(t10) ? t[0] : t10);
            r.SobrepicoPct = Math.Max(0, maxY - 1.0) * 100.0;

            if (r.Llego)
            {
                int ultimoSigno = 0, cambios = 0;
                for (int i = i90; i < angulo.Count; i++)
                {
                    double e = angulo[i] - objetivo;
                    if (Math.Abs(e) < 0.3) continue;
                    int s = Math.Sign(e);
                    if (ultimoSigno != 0 && s != ultimoSigno) cambios++;
                    ultimoSigno = s;
                }
                r.Oscila = cambios >= 2;
            }

            if (pwm != null && pwm.Count == angulo.Count)
            {
                int fin = r.Llego ? i90 : angulo.Count - 1;
                int n = 0, sat = 0;
                for (int i = 0; i <= fin; i++)
                {
                    n++;
                    if (Math.Abs(pwm[i]) >= pwmAlto - 2) sat++;
                }
                r.FraccionSaturado = n > 0 ? sat / (double)n : 0;
            }
            return r;
        }
    }

    /// <summary>Error a la guía en las pasadas del ajuste fino.</summary>
    public sealed class AnalisisPasadas
    {
        public double SesgoM;
        public double RmsM;
        /// <summary>Desvío alrededor del sesgo (lo que serpentea).</summary>
        public double DesvioM;
        /// <summary>Cruces del promedio por cada 100 m (aprox).</summary>
        public double CrucesPor100m;
        /// <summary>Período de la oscilación en segundos (NaN si no oscila).</summary>
        public double PeriodoS = double.NaN;

        public static AnalisisPasadas Analizar(IList<double> t, IList<double> errorM, double distanciaM)
        {
            var a = new AnalisisPasadas();
            if (t == null || errorM == null || errorM.Count < 3) return a;

            double suma = 0, suma2 = 0;
            foreach (double x in errorM) { suma += x; suma2 += x * x; }
            int n = errorM.Count;
            a.SesgoM = suma / n;
            a.RmsM = Math.Sqrt(suma2 / n);
            double var = Math.Max(0, suma2 / n - a.SesgoM * a.SesgoM);
            a.DesvioM = Math.Sqrt(var);

            // Cruces del sesgo con histéresis de 1 cm (el ruido del RTK no cuenta).
            int cruces = 0, signo = 0;
            double tPrimero = double.NaN, tUltimo = double.NaN;
            for (int i = 0; i < n; i++)
            {
                double d = errorM[i] - a.SesgoM;
                if (Math.Abs(d) < 0.01) continue;
                int s = Math.Sign(d);
                if (signo != 0 && s != signo)
                {
                    cruces++;
                    if (double.IsNaN(tPrimero)) tPrimero = t[i];
                    tUltimo = t[i];
                }
                signo = s;
            }
            a.CrucesPor100m = distanciaM > 1 ? cruces * 100.0 / distanciaM : 0;
            if (cruces >= 3 && tUltimo > tPrimero)
                a.PeriodoS = 2.0 * (tUltimo - tPrimero) / (cruces - 1);
            return a;
        }
    }

    /// <summary>Cuentas de calibración (sin estado).</summary>
    public static class Calibracion
    {
        /// <summary>Tope del form nativo: más de ±3900 cuentas de offset es sensor mal montado.</summary>
        public const int OffsetMax = 3900;

        /// <summary>
        /// Offset nuevo para que el promedio medido en recta pase a ser 0°.
        /// Misma fórmula que el botón de cero (offset += cuentas × −ángulo),
        /// deshaciendo el Ackermann que la placa aplica al lado izquierdo.
        /// </summary>
        public static int OffsetParaCero(SteerCalConfig c, double promedioGrados)
        {
            double crudo = promedioGrados;
            if (promedioGrados < 0 && c.Ackerman > 0) crudo = promedioGrados * 100.0 / c.Ackerman;
            return c.WasOffset + (int)Math.Round(c.CountsPerDegree * -crudo);
        }

        /// <summary>Ángulo de rueda que corresponde a un giro, por el modelo bicicleta.</summary>
        public static double AnguloBicicletaGrados(double distanciaEjesM, double yawRadS, double velMS)
        {
            if (velMS < 0.1) return double.NaN;
            return Math.Atan(distanciaEjesM * yawRadS / velMS) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Cuentas/grado nuevas con el círculo a la DERECHA: la placa divide
        /// por las cuentas, así que si marca de más hay que subirlas.
        /// </summary>
        public static int CuentasNuevas(int cuentas, double wasDerecha, double referenciaDerecha)
        {
            if (Math.Abs(referenciaDerecha) < 0.5) return cuentas;
            int n = (int)Math.Round(cuentas * wasDerecha / referenciaDerecha);
            return Math.Max(1, Math.Min(255, n));
        }

        /// <summary>
        /// Ackermann nuevo con el círculo a la IZQUIERDA (la placa multiplica
        /// solo el lado izquierdo por ackerman/100), contemplando que las
        /// cuentas también cambian.
        /// </summary>
        public static int AckermannNuevo(int ackerman, int cuentasViejas, int cuentasNuevas,
                                         double wasIzquierda, double referenciaIzquierda)
        {
            if (Math.Abs(wasIzquierda) < 0.5 || cuentasViejas <= 0) return ackerman;
            double n = ackerman * (referenciaIzquierda / wasIzquierda) * (cuentasNuevas / (double)cuentasViejas);
            return Math.Max(1, Math.Min(200, (int)Math.Round(n)));
        }

        /// <summary>Umbral de corte por corriente con margen sobre lo normal.</summary>
        public static int UmbralCorriente(int maximoNormal)
        {
            double u = Math.Max(maximoNormal * 1.5, maximoNormal + 25.0);
            return (int)Math.Round(Math.Max(30.0, u));
        }

        /// <summary>
        /// Propuesta del ajuste fino (solo Pure Pursuit). Devuelve una copia
        /// con los cambios y el motivo en criollo; null si anda bien.
        /// </summary>
        public static SteerCalConfig ProponerAjusteFino(SteerCalConfig c, AnalisisPasadas a, out string motivo)
        {
            motivo = null;
            var n = c.Clone();
            bool oscila = !double.IsNaN(a.PeriodoS) && a.PeriodoS < 15 && a.DesvioM > 0.03;
            bool deambula = !oscila && a.DesvioM > 0.04;
            bool sesgo = Math.Abs(a.SesgoM) > 0.025 && Math.Abs(a.SesgoM) > 0.5 * a.DesvioM;

            if (oscila)
            {
                // Serpentea rápido: que mire más lejos y entre más suave.
                n.HoldLookAhead = Math.Min(70, (int)Math.Round(c.HoldLookAhead * 1.2));
                if (n.HoldLookAhead == c.HoldLookAhead) n.HoldLookAhead = Math.Min(70, c.HoldLookAhead + 2);
                n.AcquireFactor = Math.Max(20, (int)Math.Round(c.AcquireFactor * 0.9));
                // Con oscilación la integral no ayuda: si estaba alta, bajarla.
                if (c.IntegralPp > 20) n.IntegralPp = c.IntegralPp - 10;
                motivo = "serpentea (período " + a.PeriodoS.ToString("F0") + " s): mirar más lejos";
            }
            else if (deambula)
            {
                // Se va y vuelve despacio: que mire más cerca.
                n.HoldLookAhead = Math.Max(10, (int)Math.Round(c.HoldLookAhead * 0.9));
                n.AcquireFactor = Math.Min(300, (int)Math.Round(c.AcquireFactor * 1.1));
                motivo = "se va de la línea despacio: mirar más cerca";
            }

            if (sesgo && !oscila)
            {
                n.IntegralPp = Math.Min(100, c.IntegralPp + 10);
                motivo = (motivo == null ? "" : motivo + "; ")
                       + "queda corrido " + (a.SesgoM * 100).ToString("F0") + " cm: más integral";
            }

            return n.MismoTodo(c) ? null : n;
        }
    }
}
