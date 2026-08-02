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

            // Guarda "sin distancias reales": ImplementoService.SeedFromLegacyServices
            // crea trenes decorativos (DistanciaM=0 en todos) para que la UI tenga
            // algo que mostrar en un implemento recién creado. Sin esta guarda, ese
            // seed pisaba el fallback manual de una máquina que ya andaba en banco
            // (motor.Tren + DistanciaEntreTrenes por nodo) con "todos los trenes a
            // distancia 0" — el peor resultado posible, no el mejor. Si NINGÚN tren
            // declara una distancia real (>0.05 m), tratamos el implemento como
            // "trenes sin configurar" y devolvemos null para que el consumidor use
            // su fallback de siempre.
            bool hayDistanciaReal = false;
            foreach (var t in impl.Trenes)
                if (t != null && t.DistanciaM > 0.05) { hayDistanciaReal = true; break; }
            if (!hayDistanciaReal) return null;

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
