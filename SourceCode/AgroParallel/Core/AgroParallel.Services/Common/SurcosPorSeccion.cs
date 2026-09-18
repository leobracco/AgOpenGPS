// ============================================================================
// SurcosPorSeccion.cs — mapa sección PilotX -> surcos que la componen, sacado
// del implemento central. Extraído porque SectionXCutAdapter y
// QuantiXMotorBridge tenían el MISMO bloque duplicado letra por letra (Task 4
// y Task 5 lo escribieron cada uno por su lado). Mover, no reescribir: mismo
// comportamiento, una sola fuente.
// ============================================================================
using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Common
{
    public static class SurcosPorSeccion
    {
        /// <summary>Sección PilotX (1-based, SurcoDto.SeccionPilotX) → números de
        /// surco (SurcoDto.Numero) que la componen. null si no hay implemento o
        /// no tiene surcos cargados — el caller decide su fallback (secciones
        /// PilotX directo, sin pasar por TrenResolver).</summary>
        public static Dictionary<int, List<int>> Construir(ImplementoDto impl)
        {
            if (impl == null || impl.Surcos == null) return null;

            var mapa = new Dictionary<int, List<int>>();
            foreach (var s in impl.Surcos)
            {
                if (s == null || s.SeccionPilotX < 1) continue;
                List<int> lista;
                if (!mapa.TryGetValue(s.SeccionPilotX, out lista))
                    mapa[s.SeccionPilotX] = lista = new List<int>();
                lista.Add(s.Numero);
            }
            return mapa;
        }
    }
}
