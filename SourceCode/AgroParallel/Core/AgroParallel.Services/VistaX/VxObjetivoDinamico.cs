// ============================================================================
// VxObjetivoDinamico.cs — objetivo de sem/m por surco cuando dosifica QuantiX.
//
// Con dosis variable (prescripción shape, manual, o simplemente velocidad
// cambiando), el objetivo FIJO del insumo no sirve para alarmar: el motor
// puede estar mandado a 2,3 sem/m por prescripción y VistaX esperando 16 —
// alarma perpetua contra un número que nadie pidió. El objetivo correcto es
// LO QUE EL MOTOR TIENE MANDADO en este instante: el PpsTarget que el bridge
// ya calculó (incluye prescripción, velocidad y surcos del motor).
//
//   sem/m del motor = pps_target × (semillas_vuelta / ppr) / (vel_ms × surcos)
//
// que es la MISMA cuenta (invertida) con la que el bridge convirtió sem/m a
// pps — ver QxPulseCalculator. Función pura para poder testearla sin MQTT.
// ============================================================================

using System;

namespace AgroParallel.Services.VistaX
{
    public static class VxObjetivoDinamico
    {
        /// <summary>
        /// Objetivo de sem/m derivado del target vivo del motor QuantiX que
        /// alimenta al surco. Devuelve 0 si no hay dato derivable (motor sin
        /// consigna, parado, sin calibrar, velocidad nula) — el llamador usa
        /// entonces el objetivo fijo del insumo, como siempre.
        /// </summary>
        /// <param name="ppsTarget">Consigna viva del motor (pulsos/seg).</param>
        /// <param name="semillasVuelta">Semillas por vuelta del dosificador.</param>
        /// <param name="ppr">Pulsos por vuelta del encoder.</param>
        /// <param name="velocidadKmh">Velocidad actual del equipo.</param>
        /// <param name="surcosDelMotor">Cuántos surcos alimenta el motor.</param>
        public static double SemMetro(double ppsTarget, double semillasVuelta,
            double ppr, double velocidadKmh, int surcosDelMotor)
        {
            if (ppsTarget <= 0.5) return 0;          // motor sin consigna
            if (semillasVuelta <= 0 || ppr <= 0) return 0;   // sin calibrar
            if (surcosDelMotor < 1) surcosDelMotor = 1;
            double vMs = velocidadKmh / 3.6;
            if (vMs <= 0.1) return 0;                // parado: el fijo manda

            double semPorSeg = ppsTarget * (semillasVuelta / ppr);
            return semPorSeg / (vMs * surcosDelMotor);
        }
    }
}
