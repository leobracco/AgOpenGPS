// ============================================================================
// TareaInforme.cs — informe legible de una tarea, en HTML imprimible.
//
// HTML y no PDF a propósito: no hay generador de PDF en el producto (el
// HTML→PDF con Chrome headless es herramienta de oficina, no de cabina) y no
// se suma una dependencia pesada para esto. El HTML abre en cualquier
// navegador y se imprime / guarda como PDF desde ahí; el @media print lo deja
// prolijo en A4.
//
// Autocontenido: estilos inline, sin fuentes ni scripts externos — se abre
// desde el pendrive en una PC sin internet. Todo texto del operario (lote,
// notas, insumo) se escapa.
// ============================================================================

using System;
using System.Text;

namespace AgroParallel.Services.Tareas
{
    public static class TareaInforme
    {
        /// <param name="archivoShp">Nombre del .shp exportado junto al informe
        /// (null si no hubo cobertura que exportar).</param>
        public static string ArmarHtml(Tarea t, DateTime generado, string archivoShp)
        {
            return ArmarHtml(t, generado, archivoShp, null, null);
        }

        /// <param name="vistax">Calidad de siembra por surco que registró
        /// VistaX durante la tarea (null = no hubo registro: no sale la sección).</param>
        /// <param name="archivoCsvVistax">Nombre del CSV exportado al lado (o null).</param>
        public static string ArmarHtml(Tarea t, DateTime generado, string archivoShp,
            AgroParallel.Services.VistaX.VxResumenLote vistax, string archivoCsvVistax)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));

            double areaM2 = TareaReglas.AreaTrabajadaM2(t, 0);
            DateTime finRef = t.Fin ?? generado;
            string tipo = TipoTrabajo.Etiqueta(t.TipoTrabajo);
            string dosis = TareaFormato.Dosis(t.Dosis, t.DosisUnidad);
            string producto = TareaFormato.ProductoEstimado(t.Dosis, t.DosisUnidad, areaM2);

            var sb = new StringBuilder(4096);
            sb.Append("<!DOCTYPE html>\n<html lang=\"es\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>").Append(E(tipo + " · " + t.Lote)).Append("</title>\n");
            sb.Append("<style>\n")
              .Append(":root{--verde:#4ABA3E;--borde:#E2E7E2;--fondo:#F5F7F4;--linea:#C5CFC5;--texto:#101612;--dim:#535E54}\n")
              .Append("*{box-sizing:border-box}\n")
              .Append("body{margin:0;background:var(--fondo);color:var(--texto);font-family:'Segoe UI',Roboto,Arial,sans-serif;font-size:15px}\n")
              .Append(".hoja{max-width:820px;margin:24px auto;background:#fff;border:1px solid var(--borde);border-radius:14px;padding:28px 32px}\n")
              .Append("header{display:flex;justify-content:space-between;align-items:flex-start;border-bottom:3px solid var(--verde);padding-bottom:12px;margin-bottom:18px}\n")
              .Append("h1{font-size:24px;margin:0}\n")
              .Append(".sub{color:var(--dim);font-size:13px;margin-top:4px}\n")
              .Append(".marca{color:var(--dim);font-size:12px;text-align:right}\n")
              .Append(".kpis{display:flex;gap:12px;margin:0 0 18px}\n")
              .Append(".kpi{flex:1;border:1px solid var(--borde);border-radius:10px;padding:12px 14px;background:var(--fondo)}\n")
              .Append(".kpi .v{font-size:26px;font-weight:700}\n")
              .Append(".kpi .l{color:var(--dim);font-size:12px;text-transform:uppercase;letter-spacing:.04em}\n")
              .Append("table{width:100%;border-collapse:collapse}\n")
              .Append("th{text-align:left;color:var(--dim);font-weight:600;width:36%;font-size:13px}\n")
              .Append("th,td{padding:8px 6px;border-bottom:1px solid var(--borde);vertical-align:top}\n")
              .Append(".notas{white-space:pre-wrap}\n")
              .Append(".pie{margin-top:18px;color:var(--dim);font-size:12px}\n")
              .Append("h2{font-size:18px;margin:24px 0 6px}\n")
              .Append("table.surcos th,table.surcos td{width:auto;text-align:right;padding:6px}\n")
              .Append("table.surcos th:first-child,table.surcos td:first-child{text-align:left}\n")
              .Append("@media print{body{background:#fff}.hoja{margin:0;border:0;border-radius:0;padding:0;max-width:none}@page{size:A4;margin:16mm}}\n")
              .Append("</style>\n</head>\n<body>\n<div class=\"hoja\">\n");

            sb.Append("<header><div><h1>").Append(E(tipo)).Append(" — ").Append(E(t.Lote)).Append("</h1>");
            sb.Append("<div class=\"sub\">").Append(E(TareaFormato.Fecha(t.Inicio)));
            if (t.Fin.HasValue) sb.Append(" a ").Append(E(TareaFormato.Fecha(t.Fin)));
            sb.Append(" · ").Append(E(EstadoTarea.Etiqueta(t.Estado))).Append("</div></div>");
            sb.Append("<div class=\"marca\"><strong>PilotX</strong><br>Agro Parallel</div></header>\n");

            sb.Append("<div class=\"kpis\">");
            Kpi(sb, TareaFormato.Hectareas(areaM2), "Área trabajada");
            Kpi(sb, TareaFormato.Duracion(TareaReglas.TiempoEfectivo(t, finRef)), "Tiempo de trabajo");
            if (producto.Length > 0) Kpi(sb, producto, "Producto estimado");
            sb.Append("</div>\n");

            sb.Append("<table>\n");
            Fila(sb, "Lote", t.Lote);
            Fila(sb, "Tipo de trabajo", tipo);
            Fila(sb, "Cultivo", Vacio(t.Cultivo));
            Fila(sb, "Insumo", Vacio(t.InsumoNombre));
            Fila(sb, "Dosis", Vacio(dosis));
            Fila(sb, "Inicio", TareaFormato.Fecha(t.Inicio));
            Fila(sb, "Fin", t.Fin.HasValue ? TareaFormato.Fecha(t.Fin) : "—");
            Fila(sb, "Pausas", Math.Max(0, (t.Tramos?.Count ?? 1) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (producto.Length > 0)
                Fila(sb, "Producto estimado", producto + " (dosis objetivo × área trabajada)");
            Fila(sb, "Cobertura (mapa)", string.IsNullOrEmpty(archivoShp) ? "Sin cobertura registrada" : archivoShp + " (shapefile WGS84)");
            sb.Append("<tr><th>Notas</th><td class=\"notas\">").Append(E(Vacio(t.Notas))).Append("</td></tr>\n");
            sb.Append("</table>\n");

            if (vistax != null && vistax.Tramos > 0) SeccionVistaX(sb, vistax, archivoCsvVistax);

            sb.Append("<div class=\"pie\">Generado por PilotX el ").Append(E(TareaFormato.Fecha(generado)))
              .Append(". El área es la superficie pintada mientras la tarea estuvo en curso; las pausas no suman.</div>\n");
            sb.Append("</div>\n</body>\n</html>\n");
            return sb.ToString();
        }

        // Calidad de siembra por surco (VistaX): promedios + tabla. Solo surcos
        // de semilla; dobles/fallas/CV solo con nodos v3.1+ (si no, "—").
        private static void SeccionVistaX(StringBuilder sb, AgroParallel.Services.VistaX.VxResumenLote vx, string csv)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append("<h2>Calidad de siembra por surco (VistaX)</h2>\n");
            sb.Append("<div class=\"sub\">").Append(E(TareaFormato.Numero(vx.DistM, "#,##0") + " m sembrados con registro mientras la tarea estuvo en curso ("
                + vx.Tramos.ToString(inv) + " tramos de ~10 m)."));
            if (!string.IsNullOrEmpty(csv)) sb.Append(" Tabla para planilla: ").Append(E(csv)).Append('.');
            sb.Append("</div>\n");

            sb.Append("<div class=\"kpis\" style=\"margin-top:12px\">");
            Kpi(sb, Vx(vx.PromedioSemM(), "0.00"), "sem/m promedio");
            if (vx.HayEspaciamiento)
            {
                Kpi(sb, Vx(vx.PromedioSingulacion(), "0.0") + " %", "Singulación");
                Kpi(sb, Vx(vx.PromedioDobles(), "0.0") + " %", "Dobles");
                Kpi(sb, Vx(vx.PromedioFallas(), "0.0") + " %", "Fallas");
                Kpi(sb, Vx(vx.PromedioCv(), "0.0") + " %", "CV");
            }
            sb.Append("</div>\n");

            sb.Append("<table class=\"surcos\">\n<tr><th>Surco</th><th>sem/m</th><th>Singulación %</th><th>Dobles %</th><th>Fallas %</th><th>CV %</th></tr>\n");
            foreach (var s in vx.Surcos)
            {
                bool hay = s.NEspacios > 0;
                string surco = s.Bajada.ToString(inv);
                if (s.Tren > 1) surco += " (tren " + s.Tren.ToString(inv) + ")";
                sb.Append("<tr><td>").Append(E(surco)).Append("</td><td>")
                  .Append(E(s.SemM >= 0 ? TareaFormato.Numero(s.SemM, "0.00") : "—")).Append("</td><td>")
                  .Append(E(hay ? TareaFormato.Numero(s.SingulacionPct, "0.0") : "—")).Append("</td><td>")
                  .Append(E(hay ? TareaFormato.Numero(s.DoblesPct, "0.0") : "—")).Append("</td><td>")
                  .Append(E(hay ? TareaFormato.Numero(s.FallasPct, "0.0") : "—")).Append("</td><td>")
                  .Append(E(hay ? TareaFormato.Numero(s.CvPct, "0.0") : "—")).Append("</td></tr>\n");
            }
            sb.Append("</table>\n");
            if (!vx.HayEspaciamiento)
                sb.Append("<div class=\"pie\">Dobles, fallas y CV requieren nodos VistaX v3.1 o más nuevos con un sensor por surco.</div>\n");
        }

        private static string Vx(double v, string formato)
        {
            return double.IsNaN(v) ? "—" : TareaFormato.Numero(v, formato);
        }

        private static void Kpi(StringBuilder sb, string valor, string etiqueta)
        {
            sb.Append("<div class=\"kpi\"><div class=\"v\">").Append(E(valor))
              .Append("</div><div class=\"l\">").Append(E(etiqueta)).Append("</div></div>");
        }

        private static void Fila(StringBuilder sb, string etiqueta, string valor)
        {
            sb.Append("<tr><th>").Append(E(etiqueta)).Append("</th><td>").Append(E(valor)).Append("</td></tr>\n");
        }

        private static string Vacio(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

        // A mano y no WebUtility.HtmlEncode: ese pasa los acentos (160..255) a
        // entidades numéricas, y el informe tiene que poder leerse/buscarse
        // como texto ("Maíz", "Pulverización") — el archivo ya es UTF-8.
        private static string E(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
