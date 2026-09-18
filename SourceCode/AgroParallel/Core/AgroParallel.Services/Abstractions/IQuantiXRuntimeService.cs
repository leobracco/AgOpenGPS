// IQuantiXRuntimeService — runtime + techo operativo por motor QuantiX.
// Combina "qué pps está pidiendo ahora el bridge" con "qué dosis máxima
// puede dar cada motor a la velocidad y ancho actual". El cálculo es la
// misma fórmula del bridge:
//   pps_max         = MaxHz
//   rpm_max         = MaxHz × 60 / DientesEngranaje
//   salida_max_g_s  = MaxHz × MeterCal
//   dosis_max_kg_ha = MaxHz × MeterCal × 36 / (ancho × vel_kmh)
// La impl FormGpsQuantiXRuntimeService lee MotoresConfig + snapshot PilotX.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IQuantiXRuntimeService
    {
        QuantiXRuntimeSnapshot GetSnapshot();
    }
}
