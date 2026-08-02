// ============================================================================
// TrenResolver.cs — deriva a qué tren pertenece un conjunto de surcos y a qué
// distancia corta ese tren, desde el ImplementoDto central.
//
// Reemplaza (fase 1: con fallback) a las TRES copias de "distancia entre
// trenes" que había: sectionX.json por nodo, quantiX_motores.json por nodo y
// el campo `tren` manual por motor. Con dos lugares editables la que se usaba
// para dosificar podía ser la equivocada (spec 2026-08-01).
//
// Contrato: devuelve null cuando NO hay dato derivable (implemento sin trenes,
// un solo tren, surcos desconocidos). El consumidor decide su fallback; acá no
// se inventa un default porque esto decide dónde corta una sección.
// ============================================================================
using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Common
{
    public sealed class TrenResultado
    {
        public int TrenId;
        public double DistanciaM;
        /// <summary>Los surcos pedidos pertenecen a más de un tren: error de
        /// configuración. Se resuelve con el tren del primer surco, no
        /// promediando — y el consumidor debería avisarlo.</summary>
        public bool Conflicto;
    }

    public static class TrenResolver
    {
        public static TrenResultado Resolver(ImplementoDto impl, IEnumerable<int> surcos)
        {
            if (impl == null || impl.Trenes == null || impl.Trenes.Count < 2) return null;
            if (impl.Surcos == null || impl.Surcos.Count == 0 || surcos == null) return null;

            var trenPorSurco = new Dictionary<int, int>();
            foreach (var s in impl.Surcos)
                if (s != null) trenPorSurco[s.Numero] = s.TrenId;

            var distPorTren = new Dictionary<int, double>();
            foreach (var t in impl.Trenes)
                if (t != null) distPorTren[t.Id] = t.DistanciaM;

            TrenResultado r = null;
            foreach (int numero in surcos)
            {
                int trenId;
                if (!trenPorSurco.TryGetValue(numero, out trenId)) continue; // surco desconocido: se ignora
                if (!distPorTren.ContainsKey(trenId)) continue;              // tren huérfano: ídem
                if (r == null)
                    r = new TrenResultado { TrenId = trenId, DistanciaM = distPorTren[trenId] };
                else if (trenId != r.TrenId)
                    r.Conflicto = true;
            }
            return r;
        }
    }
}
