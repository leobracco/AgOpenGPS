// ============================================================================
// TareaReglas.cs — transiciones de una tarea de trabajo y cuenta de su área.
// Puro: sin disco, sin reloj, sin estado del motor. Todo entra por parámetro
// (ahora, área del lote) para poder testearlo línea por línea.
//
//   activa ──pausar──▶ pausada ──reanudar──▶ activa
//     │                  │
//     └──cerrar──▶ cerrada ◀──cerrar──┘
//
// Una transición inválida devuelve false + motivo y NO toca la tarea.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Tareas
{
    public static class TareaReglas
    {
        // Debajo de esto una baja del contador del lote es ruido de redondeo,
        // no un "Borrar pintado".
        private const double ToleranciaM2 = 0.5;

        public static Tarea Crear(string id, string lote, string cultivo, string tipoTrabajo,
                                  string notas, InsumoDto insumo, DateTime ahora, double areaLoteM2)
        {
            var t = new Tarea
            {
                Id = id ?? "",
                Lote = lote ?? "",
                Cultivo = (cultivo ?? "").Trim(),
                TipoTrabajo = TipoTrabajo.Normalizar(tipoTrabajo),
                Notas = (notas ?? "").Trim(),
                Estado = EstadoTarea.Activa,
                Inicio = ahora,
                AreaAcumuladaM2 = 0,
                AreaBaseM2 = Sano(areaLoteM2),
                UltimaAreaVistaM2 = Sano(areaLoteM2),
            };
            t.Tramos.Add(new TareaTramo { Inicio = ahora });

            if (insumo != null)
            {
                t.InsumoNombre = (insumo.Nombre ?? "").Trim();
                // Sin cultivo tipeado, el del insumo (si es semilla lo trae).
                if (t.Cultivo.Length == 0) t.Cultivo = (insumo.Cultivo ?? "").Trim();

                // Fitosanitario = líquido: la dosis que vale es la de L/ha.
                bool liquido = string.Equals(insumo.Tipo, "fitosanitario", StringComparison.OrdinalIgnoreCase)
                               || (insumo.DosisKgha <= 0 && insumo.DosisLha > 0);
                if (liquido)
                {
                    t.Dosis = insumo.DosisLha;
                    t.DosisUnidad = insumo.DosisLha > 0 ? "l_ha" : "";
                }
                else
                {
                    t.Dosis = insumo.DosisKgha;
                    t.DosisUnidad = insumo.DosisKgha > 0
                        ? (string.IsNullOrEmpty(insumo.DosisUnidad) ? "kg_ha" : insumo.DosisUnidad)
                        : "";
                }
            }
            return t;
        }

        /// <summary>
        /// Registra el área actual del lote con la tarea activa. Si el contador
        /// del lote BAJÓ (Borrar pintado), lo trabajado hasta la última lectura
        /// pasa a lo acumulado y la base arranca de nuevo desde el valor actual.
        /// Devuelve true si hubo ese reinicio (conviene persistir).
        /// </summary>
        public static bool Observar(Tarea t, double areaLoteM2)
        {
            if (t == null || t.Estado != EstadoTarea.Activa) return false;
            double a = Sano(areaLoteM2);
            bool reinicio = false;
            // Contra la ÚLTIMA vista y no contra la base: con la base en 0 (lote
            // vacío al arrancar) un borrado nunca bajaría de la base y se
            // perdería todo lo trabajado hasta ahí.
            if (a < t.UltimaAreaVistaM2 - ToleranciaM2)
            {
                t.AreaAcumuladaM2 += Math.Max(0, t.UltimaAreaVistaM2 - t.AreaBaseM2);
                t.AreaBaseM2 = a;
                reinicio = true;
            }
            t.UltimaAreaVistaM2 = a;
            return reinicio;
        }

        /// <summary>Área trabajada por la tarea, en m², con el área actual del lote.</summary>
        public static double AreaTrabajadaM2(Tarea t, double areaLoteM2)
        {
            if (t == null) return 0;
            if (t.Estado != EstadoTarea.Activa) return t.AreaAcumuladaM2;
            return t.AreaAcumuladaM2 + Math.Max(0, Sano(areaLoteM2) - t.AreaBaseM2);
        }

        public static bool Pausar(Tarea t, DateTime ahora, double areaLoteM2, out string error)
        {
            if (t == null) { error = "No hay tarea."; return false; }
            if (t.Estado != EstadoTarea.Activa)
            {
                error = t.Estado == EstadoTarea.Pausada ? "La tarea ya está en pausa." : "La tarea está cerrada.";
                return false;
            }
            CerrarTramo(t, ahora, areaLoteM2);
            t.Estado = EstadoTarea.Pausada;
            error = null;
            return true;
        }

        public static bool Reanudar(Tarea t, DateTime ahora, double areaLoteM2, out string error)
        {
            if (t == null) { error = "No hay tarea."; return false; }
            if (t.Estado != EstadoTarea.Pausada)
            {
                error = t.Estado == EstadoTarea.Activa ? "La tarea ya está en curso." : "La tarea está cerrada.";
                return false;
            }
            t.AreaBaseM2 = Sano(areaLoteM2);
            t.UltimaAreaVistaM2 = t.AreaBaseM2;
            t.Tramos.Add(new TareaTramo { Inicio = ahora });
            t.Estado = EstadoTarea.Activa;
            error = null;
            return true;
        }

        public static bool Cerrar(Tarea t, DateTime ahora, double areaLoteM2, out string error)
        {
            if (t == null) { error = "No hay tarea."; return false; }
            if (t.Estado == EstadoTarea.Cerrada) { error = "La tarea ya está cerrada."; return false; }
            if (t.Estado == EstadoTarea.Activa) CerrarTramo(t, ahora, areaLoteM2);
            t.Estado = EstadoTarea.Cerrada;
            t.Fin = ahora;
            error = null;
            return true;
        }

        /// <summary>Tiempo con la tarea activa (descuenta las pausas).</summary>
        public static TimeSpan TiempoEfectivo(Tarea t, DateTime ahora)
        {
            if (t?.Tramos == null) return TimeSpan.Zero;
            var total = TimeSpan.Zero;
            foreach (var tr in t.Tramos)
            {
                var fin = tr.Fin ?? ahora;
                if (fin > tr.Inicio) total += fin - tr.Inicio;
            }
            return total;
        }

        /// <summary>
        /// true si <paramref name="instanteUtc"/> cae en un tramo en curso de
        /// la tarea (no en una pausa). Las horas de la tarea son locales (las
        /// del reloj de la pantalla); el instante viene en UTC (registro VistaX).
        /// </summary>
        public static bool EnCurso(Tarea t, DateTime instanteUtc, DateTime ahora)
        {
            if (t?.Tramos == null) return false;
            DateTime x = instanteUtc.Kind == DateTimeKind.Utc ? instanteUtc : instanteUtc.ToUniversalTime();
            foreach (var tr in t.Tramos)
            {
                DateTime ini = tr.Inicio.ToUniversalTime();
                DateTime fin = (tr.Fin ?? ahora).ToUniversalTime();
                if (x >= ini && x <= fin) return true;
            }
            return false;
        }

        /// <summary>La tarea no cerrada del lote (activa o en pausa), o null.
        /// Hay a lo sumo una: Crear la exige cerrada.</summary>
        public static Tarea Abierta(IList<Tarea> tareas)
        {
            if (tareas == null) return null;
            for (int i = tareas.Count - 1; i >= 0; i--)
                if (tareas[i] != null && tareas[i].Estado != EstadoTarea.Cerrada) return tareas[i];
            return null;
        }

        private static void CerrarTramo(Tarea t, DateTime ahora, double areaLoteM2)
        {
            Observar(t, areaLoteM2);
            t.AreaAcumuladaM2 += Math.Max(0, Sano(areaLoteM2) - t.AreaBaseM2);
            t.AreaBaseM2 = Sano(areaLoteM2);
            if (t.Tramos.Count > 0 && t.Tramos[t.Tramos.Count - 1].Fin == null)
                t.Tramos[t.Tramos.Count - 1].Fin = ahora;
        }

        // NaN/negativo/infinito del contador no pueden envenenar la cuenta.
        private static double Sano(double m2) =>
            double.IsNaN(m2) || double.IsInfinity(m2) || m2 < 0 ? 0 : m2;
    }
}
