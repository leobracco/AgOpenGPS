// ============================================================================
// QxRuntimeBuilder.cs — arma lo que el operario VE de QuantiX: dosis objetivo,
// rpm y el techo de dosis alcanzable a la velocidad actual.
//
// Existía tres veces copiado y pegado (host WinForms, head Android, y en el
// motor headless directamente no existía) y las copias se habían desincronizado
// del bridge que realmente comanda el motor:
//
//   · la dosis fija le ganaba al mapa — al revés que el bridge ("mapa manda").
//     El widget mostraba un objetivo y el motor aplicaba otro.
//   · para sembradoras (sem/m) usaba la fórmula de kg/ha: el objetivo y el
//     techo que se mostraban no tenían nada que ver con lo que giraba el motor.
//
// Acá queda UNA sola vez, como función pura, apoyada en las mismas piezas que
// usa el bridge — QxDoseResolver para la dosis y QxPulseCalculator para los
// pulsos. Si el widget y el motor difieren, es un bug; ya no puede ser una
// copia vieja.
//
// Regla de siempre: al operario se le muestran rpm y dosis en sus unidades.
// El pps es interno del firmware.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.QuantiX
{
    /// <summary>Lo que el motor necesita saber del tractor en este instante.</summary>
    public struct QxRuntimeContexto
    {
        /// <summary>Velocidad del tractor (km/h).</summary>
        public double VelocidadKmh;
        /// <summary>Ancho de trabajo del implemento (m).</summary>
        public double AnchoTotalM;
        /// <summary>Dosis del mapa de prescripción en la posición actual
        /// (0 = fuera del lote o sin mapa cargado).</summary>
        public double DosisMapaGlobal;
        /// <summary>Dosis de un campo puntual del shapefile por nombre. Puede
        /// ser null: en ese caso los motores con CampoDosis caen al mapa global.</summary>
        public Func<string, double> CampoLookup;
    }

    public static class QxRuntimeBuilder
    {
        /// <summary>Velocidades a las que se muestra el techo de dosis, para que
        /// el operario vea de un vistazo hasta dónde puede acelerar.</summary>
        private static readonly double[] VelocidadesCurva = { 5, 7, 10, 12, 15 };

        public static QuantiXRuntimeSnapshot Build(MotoresConfig cfg, QxRuntimeContexto ctx)
        {
            var snap = new QuantiXRuntimeSnapshot
            {
                Motores = new List<QuantiXMotorRuntime>(),
                CurrentSpeedKmh = ctx.VelocidadKmh,
                CurrentToolWidthM = ctx.AnchoTotalM > 0 ? ctx.AnchoTotalM : 0,
            };
            if (cfg == null || cfg.Nodos == null) return snap;

            foreach (var nodo in cfg.Nodos)
            {
                if (nodo == null || !nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;
                if (nodo.Motores == null) continue;

                for (int mi = 0; mi < nodo.Motores.Length; mi++)
                {
                    var motor = nodo.Motores[mi];
                    if (motor == null) continue;
                    snap.Motores.Add(Motor(nodo.Uid, mi, motor, ctx));
                }
            }
            return snap;
        }

        private static QuantiXMotorRuntime Motor(string uid, int mi, QxMotorConfig motor, QxRuntimeContexto ctx)
        {
            bool tieneCortes = motor.Cortes != null && motor.Cortes.Count > 0;
            int surcos = tieneCortes ? motor.Cortes.Count : 1;
            bool esSemillas = string.Equals(motor.UnidadDosis, "sem_m", StringComparison.OrdinalIgnoreCase);

            // MISMA cascada que el bridge (Manual > Mapa > Fija). Sin esto el
            // widget muestra un objetivo y el motor aplica otro.
            double dosis = QxDoseResolver.Resolve(
                motor.ManualMode,
                motor.ManualDosis,
                motor.DosisFija,
                motor.CampoDosis,
                ctx.DosisMapaGlobal,
                campo => ctx.CampoLookup != null ? ctx.CampoLookup(campo) : 0);

            int ppr = motor.DientesEngranaje > 0 ? motor.DientesEngranaje : QxPulseCalculator.DientesPorDefecto;
            double maxHz = motor.MaxHz > 0 ? motor.MaxHz : 0;

            // El widget muestra el objetivo "si estuviera aplicando": el corte
            // por sección lo decide el bridge tick a tick y acá no se conoce.
            double pps = QxPulseCalculator.Pps(new QxPulseInput
            {
                Dosis = dosis,
                VelocidadKmh = ctx.VelocidadKmh,
                SeccionOn = true,
                EsSemillas = esSemillas,
                AnchoM = ctx.AnchoTotalM,
                MeterCal = motor.MeterCal,
                Surcos = surcos,
                SemillasVuelta = motor.SemillasVuelta,
                DientesEngranaje = motor.DientesEngranaje,
            });

            // Entrega por pulso en las unidades del operario: gramos para
            // kg/ha, semillas para sem/m.
            double porPulso = esSemillas
                ? (motor.SemillasVuelta > 0 ? motor.SemillasVuelta / ppr : 0)
                : (motor.MeterCal > 0 ? motor.MeterCal : 0);

            return new QuantiXMotorRuntime
            {
                NodoUid = uid,
                MotorIndex = mi,
                Nombre = motor.Nombre,
                Habilitado = tieneCortes || motor.DosisFija > 0 || !string.IsNullOrEmpty(motor.CampoDosis),
                DosisObjetivo = dosis,
                UnidadDosis = esSemillas ? "sem_m" : "kg_ha",
                TargetPps = pps,
                TargetRpm = QxPulseCalculator.Rpm(pps, motor.DientesEngranaje),
                MaxHz = maxHz,
                MaxRpm = QxPulseCalculator.Rpm(maxHz, motor.DientesEngranaje),
                MaxOutputPerSec = maxHz * porPulso,
                MaxDoseAtCurrentSpeed = DosisMaxima(maxHz, porPulso, esSemillas,
                                                    ctx.AnchoTotalM, surcos, ctx.VelocidadKmh),
                MaxDoseCurve = Curva(maxHz, porPulso, esSemillas, ctx.AnchoTotalM, surcos),
            };
        }

        /// <summary>
        /// Dosis máxima aplicable a una velocidad dada. Devuelve -1 cuando no se
        /// puede saber (motor sin calibrar, tractor detenido): -1 es "no sé", y
        /// la UI lo muestra como guión, no como cero — un cero sería mentira.
        /// </summary>
        private static double DosisMaxima(double maxHz, double porPulso, bool esSemillas,
                                          double anchoM, int surcos, double velKmh)
        {
            if (maxHz <= 0 || porPulso <= 0) return -1;
            if (velKmh <= QxPulseCalculator.VelocidadMinimaKmh) return -1;

            double velMs = velKmh / 3.6;
            double porSeg = maxHz * porPulso;

            if (esSemillas)
            {
                if (surcos <= 0) return -1;
                // sem/m de surco: repartir la entrega entre los surcos y el avance.
                return porSeg / (velMs * surcos);
            }

            if (anchoM <= 0) return -1;
            // gramos/s → kg/ha
            return (porSeg * 10000.0) / (anchoM * velMs * 1000.0);
        }

        private static List<QuantiXMaxDosePoint> Curva(double maxHz, double porPulso, bool esSemillas,
                                                       double anchoM, int surcos)
        {
            var list = new List<QuantiXMaxDosePoint>();
            if (maxHz <= 0 || porPulso <= 0) return list;
            foreach (double v in VelocidadesCurva)
            {
                double max = DosisMaxima(maxHz, porPulso, esSemillas, anchoM, surcos, v);
                if (max < 0) continue;
                list.Add(new QuantiXMaxDosePoint { SpeedKmh = v, MaxDose = max });
            }
            return list;
        }
    }
}
