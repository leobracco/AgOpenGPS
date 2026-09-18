// ============================================================================
// QxPulseCalculator.cs — dosis → pulsos por segundo (pps) del motor QuantiX.
//
// Es el último eslabón de la cadena de siembra: qué tan rápido tiene que girar
// el motor para aplicar la dosis pedida a la velocidad actual. Estaba embebido
// en el tick de QuantiXMotorBridge, mezclado con MQTT, historial de posición y
// logging — imposible de testear. Acá queda como función PURA para poder fijar
// con tests los casos que rompen en campo (tractor parado, sección cerrada,
// calibración sin cargar) y para que el bridge tenga una sola fuente de verdad.
//
// Unidades: la dosis viaja en las del operario (kg/ha o sem/m). NUNCA en pps —
// el pps es interno del firmware y no se le muestra a nadie.
// ============================================================================

using System;

namespace AgroParallel.QuantiX
{
    /// <summary>Datos que necesita el cálculo, sin depender de la config entera.</summary>
    public struct QxPulseInput
    {
        /// <summary>Dosis ya resuelta (manual &gt; mapa &gt; fija). kg/ha o sem/m.</summary>
        public double Dosis;
        /// <summary>Velocidad real del motor (km/h). Puede diferir de la del
        /// tractor: en curva las secciones externas van más rápido.</summary>
        public double VelocidadKmh;
        /// <summary>True si al menos una sección del motor está aplicando.</summary>
        public bool SeccionOn;
        /// <summary>True si la dosis está en sem/m; false = kg/ha.</summary>
        public bool EsSemillas;

        // --- kg/ha ---
        /// <summary>Ancho de trabajo del motor (m).</summary>
        public double AnchoM;
        /// <summary>Calibración: gramos de producto por pulso.</summary>
        public double MeterCal;

        // --- sem/m ---
        /// <summary>Surcos que alimenta el motor (eje solidario).</summary>
        public int Surcos;
        /// <summary>Semillas por vuelta del plato.</summary>
        public double SemillasVuelta;
        /// <summary>Pulsos por vuelta del encoder (dientes).</summary>
        public int DientesEngranaje;
    }

    public static class QxPulseCalculator
    {
        /// <summary>Por debajo de esto se considera detenido: el motor NO gira.
        /// Sin este piso, a velocidad casi cero la dosis por hectárea tiende a
        /// infinito y el motor se embala con el tractor parado.</summary>
        public const double VelocidadMinimaKmh = 0.5;

        /// <summary>Dientes asumidos si el motor no los trae configurados.</summary>
        public const int DientesPorDefecto = 24;

        /// <summary>
        /// Pulsos por segundo que hay que pedirle al motor. Devuelve 0 —motor
        /// quieto— si el tractor está detenido, si la sección está cerrada, si
        /// no hay dosis, o si falta la calibración. Cero es siempre la respuesta
        /// segura: es preferible no sembrar a sembrar cualquier cosa.
        /// </summary>
        public static double Pps(QxPulseInput e)
        {
            if (!e.SeccionOn) return 0;
            if (e.Dosis <= 0) return 0;
            if (e.VelocidadKmh <= VelocidadMinimaKmh) return 0;

            double velocidadMs = e.VelocidadKmh / 3.6;

            if (e.EsSemillas)
            {
                int surcos = e.Surcos > 0 ? e.Surcos : 1;
                int ppv = e.DientesEngranaje > 0 ? e.DientesEngranaje : DientesPorDefecto;
                // Sin semillas/vuelta cargadas no hay forma de saber cuánto
                // entrega el plato: no se inventa un número, se para el motor.
                if (e.SemillasVuelta <= 0) return 0;

                double semPorPulso = e.SemillasVuelta / ppv;
                if (semPorPulso <= 0) return 0;

                double semillasPorSeg = e.Dosis * velocidadMs * surcos;
                return semillasPorSeg / semPorPulso;
            }

            // kg/ha → gramos/seg → pulsos/seg
            if (e.MeterCal <= 0) return 0;      // sin calibrar: motor quieto
            if (e.AnchoM <= 0) return 0;
            double gramosPorSeg = (e.Dosis * 1000.0 * e.AnchoM * velocidadMs) / 10000.0;
            return gramosPorSeg / e.MeterCal;
        }

        /// <summary>
        /// RPM del motor para un pps dado. Es lo que se le muestra al operario:
        /// el pps es interno del firmware.
        /// </summary>
        public static double Rpm(double pps, int dientesEngranaje)
        {
            int ppr = dientesEngranaje > 0 ? dientesEngranaje : DientesPorDefecto;
            return ppr > 0 ? pps * 60.0 / ppr : 0;
        }
    }
}
