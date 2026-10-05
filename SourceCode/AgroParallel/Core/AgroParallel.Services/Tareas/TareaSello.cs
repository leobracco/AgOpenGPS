// ============================================================================
// TareaSello.cs — sello de integridad de una tarea FINALIZADA.
//
// Una tarea finalizada es un registro de aplicación (pulverización, sobre todo):
// no se edita ni se reabre — TareaReglas ya rechaza cualquier transición desde
// "cerrada" y no hay endpoint que la modifique. El sello es la otra mitad: un
// SHA-256 del contenido calculado AL FINALIZAR. Si después alguien edita
// Tareas.json a mano (o un sync lo pisa con otra cosa), el sello deja de
// coincidir y el informe exportado lo dice en grande.
//
// No es una firma: quien sepa cómo se calcula puede recalcularlo. Sirve para
// detectar alteraciones accidentales o ingenuas, no contra un falsificador
// (para eso haría falta firmar con una clave fuera del equipo; fuera de alcance).
//
// FORMA CANÓNICA (versión 1). Se arma a mano, campo por campo, y NO con el
// serializador: así el sello no depende del orden de propiedades ni del
// formato que elija System.Text.Json en otra versión. Reglas:
//   · una línea "clave=valor\n" por campo, en orden fijo;
//   · textos con \ y saltos de línea escapados;
//   · números con "R" invariante (ida y vuelta exacta);
//   · fechas en ticks: UTC si la fecha trae zona (Local/Utc), ticks crudos si
//     es Unspecified — así un cambio de huso horario de la PC no da una falsa
//     alteración.
// Si algún día se agregan campos al sello, va versión 2 (SelloVersion) y la 1
// se sigue verificando como está.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgroParallel.Services.Tareas
{
    public static class IntegridadTarea
    {
        /// <summary>No está finalizada todavía (no lleva sello).</summary>
        public const string Abierta = "abierta";
        /// <summary>Finalizada antes de que existiera el sello.</summary>
        public const string SinSello = "sin_sello";
        public const string Ok = "ok";
        /// <summary>El contenido no coincide con el sello: alguien lo cambió.</summary>
        public const string Alterada = "alterada";

        public static string Etiqueta(string integridad)
        {
            switch (integridad)
            {
                case Ok: return "Sellada (sin cambios)";
                case Alterada: return "ALTERADA: el contenido no coincide con el sello";
                case SinSello: return "Sin sello (finalizada antes del sellado)";
                default: return "";
            }
        }
    }

    public static class TareaSello
    {
        public const int VersionActual = 1;

        /// <summary>Sella la tarea (debe estar finalizada).</summary>
        public static void Sellar(Tarea t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            t.SelloVersion = VersionActual;
            t.Sello = Calcular(t, VersionActual);
        }

        /// <summary>Estado de integridad: abierta / sin_sello / ok / alterada.</summary>
        public static string Verificar(Tarea t)
        {
            if (t == null) return IntegridadTarea.Abierta;
            if (t.Estado != EstadoTarea.Cerrada)
            {
                // Una tarea "abierta" CON sello es una finalizada que alguien
                // reabrió editando el archivo: eso también es una alteración.
                return string.IsNullOrEmpty(t.Sello) ? IntegridadTarea.Abierta : IntegridadTarea.Alterada;
            }
            if (string.IsNullOrEmpty(t.Sello)) return IntegridadTarea.SinSello;
            int v = t.SelloVersion ?? VersionActual;
            if (v != VersionActual) return IntegridadTarea.Alterada;   // versión desconocida = no confiable
            return string.Equals(Calcular(t, v), t.Sello, StringComparison.OrdinalIgnoreCase)
                ? IntegridadTarea.Ok
                : IntegridadTarea.Alterada;
        }

        public static string Calcular(Tarea t, int version = VersionActual)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            if (version != VersionActual) throw new ArgumentOutOfRangeException(nameof(version));
            byte[] bytes = Encoding.UTF8.GetBytes(Canonica(t));
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(bytes);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Forma canónica v1 (pública para los tests).</summary>
        public static string Canonica(Tarea t)
        {
            var sb = new StringBuilder(1024);
            L(sb, "v", "1");
            L(sb, "id", t.Id);
            L(sb, "lote", t.Lote);
            L(sb, "cultivo", t.Cultivo);
            L(sb, "tipo_trabajo", t.TipoTrabajo);
            L(sb, "notas", t.Notas);
            L(sb, "insumo_nombre", t.InsumoNombre);
            L(sb, "dosis", N(t.Dosis));
            L(sb, "dosis_unidad", t.DosisUnidad);
            L(sb, "estado", t.Estado);
            L(sb, "inicio", F(t.Inicio));
            L(sb, "fin", t.Fin.HasValue ? F(t.Fin.Value) : "");
            var tramos = t.Tramos ?? new List<TareaTramo>();
            L(sb, "tramos", tramos.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < tramos.Count; i++)
            {
                var tr = tramos[i] ?? new TareaTramo();
                L(sb, "tramo." + i.ToString(CultureInfo.InvariantCulture),
                  F(tr.Inicio) + ";" + (tr.Fin.HasValue ? F(tr.Fin.Value) : ""));
            }
            L(sb, "area_acumulada_m2", N(t.AreaAcumuladaM2));
            L(sb, "area_base_m2", N(t.AreaBaseM2));
            L(sb, "ultima_area_vista_m2", N(t.UltimaAreaVistaM2));

            var s = t.Snapshot;
            L(sb, "snapshot", s == null ? "0" : "1");
            if (s != null)
            {
                L(sb, "s.tomado", F(s.Tomado));
                L(sb, "s.vehiculo", s.Vehiculo);
                L(sb, "s.implemento", s.Implemento);
                L(sb, "s.ancho_m", N(s.AnchoM));
                L(sb, "s.secciones", s.Secciones.ToString(CultureInfo.InvariantCulture));
                var anchos = s.AnchosSeccionesM ?? new List<double>();
                var partes = new string[anchos.Count];
                for (int i = 0; i < anchos.Count; i++) partes[i] = N(anchos[i]);
                L(sb, "s.anchos_secciones_m", string.Join(";", partes));
                L(sb, "s.insumo_tipo", s.InsumoTipo);
                var fx = s.FlowX ?? new List<TareaSnapshotFlowX>();
                L(sb, "s.flowx", fx.Count.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < fx.Count; i++)
                {
                    var p = fx[i] ?? new TareaSnapshotFlowX();
                    string k = "s.flowx." + i.ToString(CultureInfo.InvariantCulture);
                    L(sb, k + ".nodo", p.Nodo);
                    L(sb, k + ".producto", p.Producto);
                    L(sb, k + ".dosis_lha", N(p.DosisLha));
                    L(sb, k + ".meter_cal", N(p.MeterCal));
                }
                L(sb, "s.operario", s.Operario);
            }
            return sb.ToString();
        }

        private static void L(StringBuilder sb, string clave, string valor)
        {
            sb.Append(clave).Append('=');
            foreach (char c in valor ?? "")
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('\n');
        }

        private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string F(DateTime d) =>
            d.Kind == DateTimeKind.Unspecified
                ? "u" + d.Ticks.ToString(CultureInfo.InvariantCulture)
                : "z" + d.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
    }
}
