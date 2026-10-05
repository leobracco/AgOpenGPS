// ============================================================================
// QxAnchoMotor.cs — ancho de trabajo REAL de un motor QuantiX y cuántos surcos
// alimenta. Función pura, con tests.
//
// Por qué existe (fix 2026-08-08): el bridge usaba SIEMPRE el ancho total del
// implemento para la rama kg/ha, y la CANTIDAD DE SECCIONES como si fueran
// surcos para la rama sem/m. Con motores parciales eso rompe la dosis:
//
//   · 14 surcos, 2 motores de semilla en kg/ha con 7 surcos cada uno
//     → cada motor calculaba con 14 surcos de ancho = DOBLE de producto.
//   · 96 surcos, 2 tolvas, 1 motor por tren (48 surcos en 4 secciones)
//     → sem/m contaba 4 "surcos" en vez de 48 = 12 VECES de menos.
//
// Regla: el mejor dato gana.
//   1. Implemento central cargado → surcos reales del motor × espaciamiento.
//   2. Sin implemento → proporcional a sus secciones sobre el ancho total.
//   3. Sin nada (o motor sin cortes = eje que alimenta todo) → ancho total,
//      que es el comportamiento histórico.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.QuantiX
{
    public static class QxAnchoMotor
    {
        /// <summary>Ancho de trabajo del motor (m) para la rama kg/ha.</summary>
        /// <param name="cortes">Secciones PilotX (1-based) asignadas al motor.
        /// null/vacío = el motor alimenta el implemento entero.</param>
        /// <param name="surcosMotor">Surcos del motor derivados del implemento
        /// central (SurcosDeSecciones). null si no hay implemento.</param>
        /// <param name="impl">Implemento central (espaciamiento real). Puede ser null.</param>
        /// <param name="totalSecciones">Secciones PilotX totales del snapshot.</param>
        /// <param name="anchoTotalM">Ancho total de la herramienta (m).</param>
        public static double Resolver(IList<int> cortes, ICollection<int> surcosMotor,
                                      ImplementoDto impl, int totalSecciones, double anchoTotalM)
        {
            bool tieneCortes = cortes != null && cortes.Count > 0;
            if (!tieneCortes) return anchoTotalM;   // alimenta todo: ancho total es correcto

            // 1) Implemento central: surcos reales × espaciamiento real.
            if (surcosMotor != null && surcosMotor.Count > 0 && impl != null)
            {
                double esp = impl.DistanciaEntreSurcosM;
                if (esp <= 0 && impl.NumeroSurcos > 0 && anchoTotalM > 0)
                    esp = anchoTotalM / impl.NumeroSurcos;
                if (esp > 0) return surcosMotor.Count * esp;
            }

            // 2) Sin implemento: proporcional a las secciones asignadas.
            //    (rigs viejos: sección ≈ surco, la proporción da lo mismo)
            if (totalSecciones > 0 && anchoTotalM > 0)
            {
                int n = cortes.Count > totalSecciones ? totalSecciones : cortes.Count;
                return anchoTotalM * n / totalSecciones;
            }

            // 3) Último recurso: comportamiento histórico.
            return anchoTotalM;
        }

        /// <summary>Surcos que alimenta el motor, para la rama sem/m.
        /// Con implemento central son los surcos REALES de sus secciones;
        /// sin implemento se mantiene el histórico Cortes.Count (rigs viejos
        /// donde cada sección era un surco).</summary>
        public static int Surcos(IList<int> cortes, ICollection<int> surcosMotor)
        {
            if (surcosMotor != null && surcosMotor.Count > 0) return surcosMotor.Count;
            return (cortes != null && cortes.Count > 0) ? cortes.Count : 1;
        }
    }
}
