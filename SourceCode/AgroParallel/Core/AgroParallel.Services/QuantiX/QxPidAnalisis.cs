// ============================================================================
// QxPidAnalisis.cs — mira una corrida y dice QUE TOCAR en el PID de ese motor.
//
// El numero sale de la MEDICION, no de un modelo generativo. Un modelo de
// lenguaje puede tirar un Kp con total convicción y tenerlo inventado; acá se
// mide cuanto oscila, cuanto error queda y cuanto vive saturado el motor, y de
// ahi salen las ganancias por reglas de sintonia conocidas. Cada recomendacion
// viene con el numero que la justifica para que se pueda auditar en el CSV.
//
// Orden de los descartes — importa, porque recomendar sobre datos que no lo
// soportan es peor que no recomendar nada:
//   1. Corrida corta          -> no alcanza para medir.
//   2. Velocidad despareja    -> el target se movio todo el tiempo; que las rpm
//                                cambien NO es culpa del PID.
//   3. Motor saturado         -> vivio contra el techo de PWM: no hay ganancia
//                                que lo arregle, es mecanico.
// Recien despues se mira si oscila, si le falta empuje o si le falta integral.
//
// Ajustes GRADUALES y de a UN parametro por vez (practica estandar de sintonia):
// un salto grande "arregla" el sintoma y desarma el motor que andaba.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace AgroParallel.QuantiX
{
    /// <summary>Que hacer con este motor.</summary>
    public enum QxVeredicto
    {
        /// <summary>No hay datos suficientes o confiables para opinar.</summary>
        SinDatos,
        /// <summary>Sigue bien el objetivo: no tocar nada.</summary>
        Anda,
        /// <summary>Las rpm oscilan alrededor del objetivo con la velocidad pareja.</summary>
        Oscila,
        /// <summary>Queda un error parejo que no se cierra nunca.</summary>
        ErrorConstante,
        /// <summary>Llega tarde al objetivo, sin pasarse ni oscilar.</summary>
        Lento,
        /// <summary>PWM contra el techo: no es problema de ganancias.</summary>
        Saturado,
        /// <summary>La velocidad del motor vario tanto que no se puede juzgar.</summary>
        VelocidadDespareja
    }

    /// <summary>Resultado del analisis de un motor en una corrida.</summary>
    public sealed class QxPidDiagnostico
    {
        public string Uid { get; set; }
        public int MotorIdx { get; set; }
        public string Nombre { get; set; }

        public QxVeredicto Veredicto { get; set; }

        /// <summary>En criollo, para la cabina.</summary>
        public string Explicacion { get; set; }

        /// <summary>Que se mide y con que numero se justifica.</summary>
        public double RpmDesvio { get; set; }
        public double ErrorMedioPct { get; set; }
        public double TiempoSaturadoPct { get; set; }
        public double VelDesvioPct { get; set; }
        public double OscilacionHz { get; set; }
        public int Muestras { get; set; }

        /// <summary>true si hay una ganancia para cambiar.</summary>
        public bool HayRecomendacion { get; set; }

        /// <summary>Cual: "kp", "ki" o "kd". Vacio si no hay.</summary>
        public string Parametro { get; set; }

        public double ValorActual { get; set; }
        public double ValorSugerido { get; set; }
    }

    public static class QxPidAnalisis
    {
        /// <summary>Menos de esto no alcanza para medir nada (a 5 Hz son 10 s).</summary>
        public const int MuestrasMinimas = 50;

        /// <summary>Si la velocidad del motor vario mas que esto (% sobre su
        /// promedio), el target se movio todo el tiempo y no se puede culpar al
        /// PID de que las rpm lo sigan.</summary>
        public const double VelDesparejaPct = 15.0;

        /// <summary>% de muestras contra el techo de PWM para considerarlo
        /// saturado.</summary>
        public const double SaturadoPct = 20.0;

        /// <summary>Desvio de rpm (% del promedio) a partir del cual se considera
        /// que oscila de verdad y no que es ruido del encoder.</summary>
        public const double OscilaPct = 8.0;

        /// <summary>Error medio que ya se nota en el lote.</summary>
        public const double ErrorNotablePct = 5.0;

        /// <summary>Cuanto se recorta Ki cuando oscila. Gradual a proposito.</summary>
        public const double RecorteKi = 0.70;

        /// <summary>Cuanto se sube Ki cuando queda error parejo.</summary>
        public const double SubeKi = 1.30;

        /// <summary>Cuanto se sube Kp cuando llega tarde.</summary>
        public const double SubeKp = 1.20;

        /// <summary>Analiza las muestras de UN motor.</summary>
        public static QxPidDiagnostico Analizar(IList<QxPidSample> ms, double kp, double ki, double kd)
        {
            var d = new QxPidDiagnostico();
            d.Parametro = "";
            d.Explicacion = "";

            if (ms == null || ms.Count == 0)
            {
                d.Veredicto = QxVeredicto.SinDatos;
                d.Explicacion = "No hay muestras de este motor en la corrida.";
                return d;
            }

            d.Uid = ms[0].Uid;
            d.MotorIdx = ms[0].MotorIdx;
            d.Nombre = ms[0].Nombre;
            d.Muestras = ms.Count;

            var resumen = QxPidResumen.Calcular(ms);
            d.RpmDesvio = resumen.RpmDesvio;
            d.ErrorMedioPct = resumen.ErrorMedioPct;
            d.TiempoSaturadoPct = resumen.TiempoSaturadoPct;

            // ---- 1. ¿Alcanza la corrida? -------------------------------------
            if (ms.Count < MuestrasMinimas)
            {
                d.Veredicto = QxVeredicto.SinDatos;
                d.Explicacion = "La corrida es muy corta para medir. Tirá unos 100 metros parejos.";
                return d;
            }

            // ---- 2. ¿Estuvo pareja la velocidad DEL MOTOR? --------------------
            // La del motor y no la del tractor: en curva la seccion externa va mas
            // rapido y el target CAMBIA con razon.
            d.VelDesvioPct = DesvioRelativoPct(ms, true);
            if (d.VelDesvioPct > VelDesparejaPct)
            {
                d.Veredicto = QxVeredicto.VelocidadDespareja;
                d.Explicacion = "La velocidad del motor varió " + Pct(d.VelDesvioPct) +
                    " en esta corrida, así que las rpm cambiaron por eso y no por el PID. " +
                    "Repetí la prueba a velocidad pareja, en recta.";
                return d;
            }

            // ---- 3. ¿Contra el techo? -----------------------------------------
            if (d.TiempoSaturadoPct >= SaturadoPct)
            {
                d.Veredicto = QxVeredicto.Saturado;
                d.Explicacion = "El motor estuvo " + Pct(d.TiempoSaturadoPct) +
                    " del tiempo al máximo de fuerza. No puede seguir el objetivo y eso NO se " +
                    "arregla con las ganancias: revisá la cadena, el rodamiento o bajá la dosis.";
                return d;
            }

            double rpmProm = resumen.RpmProm;
            if (rpmProm < 1.0)
            {
                d.Veredicto = QxVeredicto.SinDatos;
                d.Explicacion = "El motor no llegó a girar en esta corrida.";
                return d;
            }

            double desvioPct = resumen.RpmDesvio / rpmProm * 100.0;
            d.OscilacionHz = FrecuenciaOscilacion(ms);

            // ---- 4. ¿Oscila? ---------------------------------------------------
            if (desvioPct >= OscilaPct)
            {
                d.Veredicto = QxVeredicto.Oscila;
                d.Explicacion = "Las rpm suben y bajan " + Redondear(resumen.RpmDesvio) +
                    " con la velocidad pareja" +
                    (d.OscilacionHz > 0.05 ? " (" + d.OscilacionHz.ToString("F1", CultureInfo.InvariantCulture) + " veces por segundo)" : "") +
                    ". El motor persigue el objetivo y se pasa: sobra integral.";
                Sugerir(d, "ki", ki, ki * RecorteKi);
                return d;
            }

            // ---- 5. ¿Queda error parejo? ---------------------------------------
            if (d.ErrorMedioPct >= ErrorNotablePct)
            {
                bool porDebajo = PromedioRealMenorQueTarget(ms);
                d.Veredicto = porDebajo ? QxVeredicto.Lento : QxVeredicto.ErrorConstante;
                d.Explicacion = "Queda un " + Pct(d.ErrorMedioPct) + " de diferencia entre las rpm " +
                    "pedidas y las reales, sin oscilar. " +
                    (porDebajo ? "El motor se queda corto: le falta empuje." : "No termina de cerrar el error.");

                // Falta empuje sostenido -> integral. Falta reaccion -> proporcional.
                if (porDebajo && d.ErrorMedioPct >= ErrorNotablePct * 2) Sugerir(d, "kp", kp, kp * SubeKp);
                else Sugerir(d, "ki", ki, ki * SubeKi);
                return d;
            }

            // ---- 6. Anda -------------------------------------------------------
            d.Veredicto = QxVeredicto.Anda;
            d.Explicacion = "Sigue bien el objetivo: " + Pct(d.ErrorMedioPct) + " de error y las rpm " +
                "firmes. No hay nada para tocar.";
            return d;
        }

        private static void Sugerir(QxPidDiagnostico d, string parametro, double actual, double sugerido)
        {
            // Con la ganancia en cero no se sugiere nada: significa que ese termino
            // esta apagado a proposito y multiplicarlo no lo enciende.
            if (actual <= 0) return;
            sugerido = Math.Round(sugerido, 1);
            if (Math.Abs(sugerido - actual) < 0.05) return;
            d.HayRecomendacion = true;
            d.Parametro = parametro;
            d.ValorActual = actual;
            d.ValorSugerido = sugerido;
        }

        /// <summary>Desvio estandar como % del promedio. Es la forma honesta de
        /// preguntar "¿estuvo pareja?" sin depender de la escala.</summary>
        private static double DesvioRelativoPct(IList<QxPidSample> ms, bool velocidad)
        {
            double suma = 0;
            int n = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                double v = velocidad ? ms[i].VelMotorKmh : ms[i].RpmTarget;
                suma += v;
                n++;
            }
            if (n == 0) return 0;
            double prom = suma / n;
            if (prom < 0.2) return 0;   // parado: no tiene sentido el %

            double sq = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                double v = velocidad ? ms[i].VelMotorKmh : ms[i].RpmTarget;
                double dd = v - prom;
                sq += dd * dd;
            }
            return Math.Sqrt(sq / n) / prom * 100.0;
        }

        /// <summary>Cuantas veces por segundo las rpm cruzan el objetivo. Dos
        /// cruces son un ciclo. Sirve para distinguir una oscilacion real de una
        /// deriva lenta.</summary>
        private static double FrecuenciaOscilacion(IList<QxPidSample> ms)
        {
            int cruces = 0;
            int signoPrevio = 0;
            int primerTick = 0, ultimoTick = 0;
            bool hayPrimero = false;

            for (int i = 0; i < ms.Count; i++)
            {
                if (!ms[i].RpmReal.HasValue) continue;
                if (!hayPrimero) { primerTick = ms[i].TickMs; hayPrimero = true; }
                ultimoTick = ms[i].TickMs;

                double err = ms[i].RpmReal.Value - ms[i].RpmTarget;
                int signo = err > 0.5 ? 1 : (err < -0.5 ? -1 : 0);
                if (signo == 0) continue;
                if (signoPrevio != 0 && signo != signoPrevio) cruces++;
                signoPrevio = signo;
            }

            double seg = unchecked(ultimoTick - primerTick) / 1000.0;
            if (seg < 1.0 || cruces < 2) return 0;
            return (cruces / 2.0) / seg;
        }

        private static bool PromedioRealMenorQueTarget(IList<QxPidSample> ms)
        {
            double real = 0, target = 0;
            int n = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                if (!ms[i].RpmReal.HasValue) continue;
                real += ms[i].RpmReal.Value;
                target += ms[i].RpmTarget;
                n++;
            }
            if (n == 0) return false;
            return (real / n) < (target / n);
        }

        private static string Pct(double v)
        {
            return v.ToString("F0", CultureInfo.InvariantCulture) + "%";
        }

        private static string Redondear(double v)
        {
            return v.ToString("F0", CultureInfo.InvariantCulture) + " rpm";
        }
    }
}
