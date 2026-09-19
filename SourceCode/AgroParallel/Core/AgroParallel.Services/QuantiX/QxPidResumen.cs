// ============================================================================
// QxPidResumen.cs — estadistica por motor de una sesion de registro.
//
// Existe para no tener que abrir 14 CSV buscando cual se porta distinto. Un
// motor con el triple de desvio y load_pct promedio de 86% con un tercio del
// tiempo saturado NO se arregla con Kp: es fuerza mecanica de mas.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.QuantiX
{
    public sealed class QxPidResumen
    {
        public string Uid { get; set; }
        public int MotorIdx { get; set; }
        public string Nombre { get; set; }

        /// <summary>RPM reales promedio (ignora las muestras sin dato).</summary>
        public double RpmProm { get; set; }

        /// <summary>Desvio estandar de las RPM reales. Es el numero que delata
        /// al PID mal sintonizado cuando la velocidad estuvo estable.</summary>
        public double RpmDesvio { get; set; }

        /// <summary>|real - target| / target promedio, en %.</summary>
        public double ErrorMedioPct { get; set; }

        public double LoadPromPct { get; set; }

        /// <summary>% de muestras con load_pct >= 99. Si es alto, el motor vive
        /// contra el techo y el PID no tiene margen para corregir.</summary>
        public double TiempoSaturadoPct { get; set; }

        public static QxPidResumen Calcular(IList<QxPidSample> ms)
        {
            var r = new QxPidResumen();
            if (ms == null || ms.Count == 0) return r;

            r.Uid = ms[0].Uid;
            r.MotorIdx = ms[0].MotorIdx;
            r.Nombre = ms[0].Nombre;

            double sumaRpm = 0, sumaLoad = 0, sumaErr = 0;
            int nRpm = 0, nErr = 0, nSat = 0;

            for (int i = 0; i < ms.Count; i++)
            {
                sumaLoad += ms[i].LoadPct;
                if (ms[i].LoadPct >= 99) nSat++;
                if (!ms[i].RpmReal.HasValue) continue;   // sin dato no promedia como cero
                sumaRpm += ms[i].RpmReal.Value;
                nRpm++;
                if (ms[i].RpmTarget > 0)
                {
                    sumaErr += Math.Abs(ms[i].RpmReal.Value - ms[i].RpmTarget) / ms[i].RpmTarget * 100.0;
                    nErr++;
                }
            }

            r.LoadPromPct = sumaLoad / ms.Count;
            r.TiempoSaturadoPct = nSat * 100.0 / ms.Count;
            if (nErr > 0) r.ErrorMedioPct = sumaErr / nErr;
            if (nRpm == 0) return r;

            r.RpmProm = sumaRpm / nRpm;

            double sumaSq = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                if (!ms[i].RpmReal.HasValue) continue;
                double d = ms[i].RpmReal.Value - r.RpmProm;
                sumaSq += d * d;
            }
            r.RpmDesvio = Math.Sqrt(sumaSq / nRpm);
            return r;
        }
    }
}
