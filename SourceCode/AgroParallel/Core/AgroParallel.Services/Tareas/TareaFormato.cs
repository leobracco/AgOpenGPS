// ============================================================================
// TareaFormato.cs — números y textos de la tarea como los lee el operario:
// coma decimal y punto de miles (es-AR), hectáreas con 2 decimales, dosis en
// la unidad del insumo (kg/ha, L/ha, sem/ha, sem/m). NetStandard + Android: no
// se depende de que el sistema tenga la cultura "es-AR" instalada (ICU puede
// faltar), así que el formato se arma con un NumberFormatInfo propio.
// ============================================================================

using System;
using System.Globalization;

namespace AgroParallel.Services.Tareas
{
    public static class TareaFormato
    {
        private static readonly NumberFormatInfo Nfi = new NumberFormatInfo
        {
            NumberDecimalSeparator = ",",
            NumberGroupSeparator = ".",
            NumberGroupSizes = new[] { 3 },
        };

        public const string FormatoFecha = "dd/MM/yyyy HH:mm";

        public static string Numero(double v, string formato) => v.ToString(formato, Nfi);

        public static string Hectareas(double m2) => Numero(m2 / 10000.0, "#,##0.00") + " ha";

        public static string Duracion(TimeSpan d)
        {
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            int horas = (int)d.TotalHours;
            int min = d.Minutes;
            return horas > 0
                ? horas.ToString(CultureInfo.InvariantCulture) + " h " + min.ToString("00", CultureInfo.InvariantCulture) + " min"
                : min.ToString(CultureInfo.InvariantCulture) + " min";
        }

        public static string Fecha(DateTime? f) =>
            f.HasValue ? f.Value.ToString(FormatoFecha, CultureInfo.InvariantCulture) : "";

        /// <summary>Unidad visible. Nunca pps ni nada interno: lo que dice la bolsa.</summary>
        public static string Unidad(string unidad)
        {
            switch ((unidad ?? "").Trim().ToLowerInvariant())
            {
                case "kg_ha": return "kg/ha";
                case "l_ha": return "L/ha";
                case "sem_ha": return "sem/ha";
                case "sem_m": return "sem/m";
                default: return "";
            }
        }

        /// <summary>"80 kg/ha", "2,5 L/ha"… "" si no hay dosis cargada.</summary>
        public static string Dosis(double valor, string unidad)
        {
            if (!(valor > 0)) return "";
            string u = Unidad(unidad);
            return Numero(valor, "#,##0.##") + (u.Length > 0 ? " " + u : "");
        }

        /// <summary>
        /// Producto total estimado = dosis × hectáreas, solo para dosis por
        /// hectárea en masa o volumen ("1.235 kg", "250 L"). Es la dosis
        /// OBJETIVO, no lo que midió el nodo: el informe lo dice "estimado".
        /// "" si no aplica (sem/m, sin dosis, sin área).
        /// </summary>
        public static string ProductoEstimado(double dosis, string unidad, double areaM2)
        {
            if (!(dosis > 0) || !(areaM2 > 0)) return "";
            double ha = areaM2 / 10000.0;
            switch ((unidad ?? "").Trim().ToLowerInvariant())
            {
                case "kg_ha": return Numero(dosis * ha, "#,##0") + " kg";
                case "l_ha": return Numero(dosis * ha, "#,##0") + " L";
                case "sem_ha": return Numero(dosis * ha, "#,##0") + " semillas";
                default: return "";
            }
        }
    }
}
