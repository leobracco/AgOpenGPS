// ============================================================================
// IsoXmlFieldExporter.cs — el lote abierto a ISO-XML (ISO 11783-10,
// TASKDATA\TASKDATA.XML) para llevarlo en un pendrive a otra pantalla (John
// Deere, CNH, etc.) o a un software de gestión.
//
// Es el SIMÉTRICO de IsoXmlFieldImporter: escribe exactamente lo que ese
// importador sabe leer, y los tests hacen la ida y vuelta (exportar → importar
// con nuestro importador → misma geometría, tolerancia de centímetros).
//
//   PFD  (partfield = el lote; D = área en m², exterior menos huecos)
//    ├─ PLN A=1  lindero exterior      → LSG A=1 (polygon exterior) → PNT A=2
//    ├─ PLN A=6  cada hueco/obstáculo  → LSG A=1                    → PNT A=2
//    ├─ PLN A=10 cabecera del exterior → LSG A=1                    → PNT A=2
//    ├─ v4: GGP → GPN (C=1 AB | C=3 curva) → LSG A=5 → PNT A=6 (A), 9 (medio), 7 (B)
//    └─ v3: LSG A=5 suelta bajo el PFD (2 puntos = AB, más = curva), PNT A=2
//
// Versión por defecto: 4.3. Es la que exportan/importan hoy las pantallas John
// Deere (Gen4 / Operations Center) y CNH (AFS Pro 1200 / IntelliView 12), y la
// única donde las guías existen como guías (GGP/GPN). La 3.3 queda para
// pantallas viejas: lleva el lindero igual, y las guías como líneas sueltas
// (LSG tipo 5), que muchas terminales v3 ignoran.
//
// Decisiones (ver el reporte del commit):
//   · La AB se escribe con sus puntos A y B REALES (no ±1000 m como hacía el
//     exportador viejo de AOG, ISO11783_TaskFile): así vuelve igual al leerla
//     y las terminales la extienden infinito de todas formas. GPN G lleva el
//     rumbo en grados.
//   · Cada GPN con su id propio (GPN1, GPN2…), distinto del GGP: reusar el id
//     del grupo es ISO 11783-10 inválido. La observación viene del exportador
//     de AgOpenWeb (Shared/AgOpenWeb.Services/IsoXml/IsoXmlExporter.cs,
//     "AgOpenWeb Contributors"); el código acá es propio.
//   · El anillo NO repite el primer punto al final (el polígono ISO es
//     implícitamente cerrado, igual que el exportador de AOG; repetirlo le
//     metería un segmento de largo cero al lindero al reimportarlo).
//   · Lindero virtual de "Marcar giro" (isVirtualTurnBoundary): no es un
//     lindero real, no se exporta.
//   · La cobertura NO va: en ISO-XML lo trabajado es un TimeLog binario
//     (TLGxxxxx.BIN + XML de cabecera, con device description del implemento),
//     un formato aparte que hoy no generamos. La cobertura se lleva con el
//     export de la Tarea (shapefile).
//   · Si ya hay una carpeta TASKDATA en el destino NO se pisa: se aparta entera
//     como TASKDATA_anterior_<fecha> (puede traer TLG/BIN de otra pantalla que
//     el XML referencia).
//   · XML a mano (XmlWriter) y no con la librería de Dev4Agriculture: control
//     total de qué atributos salen en cada versión. La librería se usa en los
//     tests para validar que el archivo abre sin errores.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;

namespace AgOpenGPS.Protocols.ISOBUS
{
    public static class IsoXmlFieldExporter
    {
        public enum IsoVersion { V3, V4 }

        public const string Carpeta = "TASKDATA";
        public const string Archivo = "TASKDATA.XML";

        // AB "de un punto" (A≈B): se arma B con el rumbo a esta distancia.
        private const double LargoAbSinB = 100.0;

        /// <summary>
        /// Escribe TASKDATA\TASKDATA.XML. <paramref name="destino"/> puede ser una
        /// carpeta (se crea TASKDATA adentro, salvo que ya se llame TASKDATA) o un
        /// archivo .XML elegido en el explorador (se usa su carpeta). Nunca tira:
        /// los errores vuelven en el resultado.
        /// </summary>
        public static IsoXmlExportResultadoDto Exportar(
            string destino, string designator, IList<CBoundaryList> linderos, IList<CTrk> guias,
            LocalPlane plano, IsoVersion version, string versionSoftware)
        {
            var r = new IsoXmlExportResultadoDto { Version = version == IsoVersion.V4 ? "4" : "3" };
            try
            {
                if (plano == null) { r.Error = "El lote no tiene origen de coordenadas."; return r; }
                string dir = ResolverCarpeta(destino);
                if (dir == null) { r.Error = "Elegí dónde guardar (el pendrive)."; return r; }

                var exteriores = new List<CBoundaryList>();
                if (linderos != null)
                    foreach (var b in linderos)
                        if (b != null && !b.isVirtualTurnBoundary && b.fenceLine != null && b.fenceLine.Count >= 3)
                            exteriores.Add(b);
                int nGuias = 0;
                if (guias != null)
                    foreach (var g in guias) if (Exportable(g)) nGuias++;
                if (exteriores.Count == 0 && nGuias == 0)
                {
                    r.Error = "El lote no tiene lindero ni guías AB/curva para exportar.";
                    return r;
                }

                string xml = ArmarXml(designator, exteriores, guias, plano, version, versionSoftware,
                                      out int nLinderos, out bool cabecera);

                if (File.Exists(Path.Combine(dir, Archivo)))
                {
                    string apartada = dir + "_anterior_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    int n = 2;
                    string baseAp = apartada;
                    while (Directory.Exists(apartada)) apartada = baseAp + "-" + (n++).ToString(CultureInfo.InvariantCulture);
                    Directory.Move(dir, apartada);
                    r.Apartada = apartada;
                }
                Directory.CreateDirectory(dir);
                string ruta = Path.Combine(dir, Archivo);
                // Sin BOM: hay terminales que no lo digieren.
                File.WriteAllText(ruta, xml, new UTF8Encoding(false));

                r.Ok = true;
                r.Archivo = ruta;
                r.Carpeta = dir;
                r.Linderos = nLinderos;
                r.Guias = nGuias;
                r.Cabecera = cabecera;
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = "No se pudo escribir el ISO-XML: " + ex.Message;
                return r;
            }
        }

        /// <summary>Arma el TASKDATA.XML en memoria (público para tests/diagnóstico).</summary>
        public static string ArmarXml(string designator, IList<CBoundaryList> linderos, IList<CTrk> guias,
                                      LocalPlane plano, IsoVersion version, string versionSoftware,
                                      out int nLinderos, out bool cabecera)
        {
            bool v4 = version == IsoVersion.V4;
            nLinderos = 0;
            cabecera = false;

            var validos = new List<CBoundaryList>();
            if (linderos != null)
                foreach (var b in linderos)
                    if (b != null && !b.isVirtualTurnBoundary && b.fenceLine != null && b.fenceLine.Count >= 3)
                        validos.Add(b);

            double area = 0;
            for (int i = 0; i < validos.Count; i++)
            {
                double a = Math.Abs(AreaShoelace(validos[i].fenceLine));
                area += i == 0 ? a : -a;
            }
            if (area < 0) area = 0;

            var sb = new StringBuilder(64 * 1024);
            var set = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = true,   // la escribimos a mano con encoding UTF-8
            };
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
            using (var w = XmlWriter.Create(sb, set))
            {
                w.WriteStartElement("ISO11783_TaskData");
                w.WriteAttributeString("VersionMajor", v4 ? "4" : "3");
                w.WriteAttributeString("VersionMinor", "3");
                w.WriteAttributeString("ManagementSoftwareManufacturer", "Agro Parallel");
                w.WriteAttributeString("ManagementSoftwareVersion", string.IsNullOrWhiteSpace(versionSoftware) ? "0" : versionSoftware.Trim());
                w.WriteAttributeString("DataTransferOrigin", "1");   // 1 = FMIS: es un plan para otra pantalla

                w.WriteStartElement("PFD");
                w.WriteAttributeString("A", "PFD1");
                w.WriteAttributeString("C", Nombre(designator, "Lote"));
                w.WriteAttributeString("D", ((ulong)Math.Round(area)).ToString(CultureInfo.InvariantCulture));

                int plnId = 0;
                for (int i = 0; i < validos.Count; i++)
                {
                    // 1 = partfield boundary; 6 = obstacle (lo que el importador
                    // lee como lindero interior).
                    EscribirPoligono(w, i == 0 ? "1" : "6", i == 0 ? Nombre(designator, "Lote") : null,
                                     validos[i].fenceLine, plano, v4, ++plnId);
                    nLinderos++;
                }
                // Cabecera: solo la del exterior (es la que el importador lee).
                if (validos.Count > 0 && validos[0].hdLine != null && validos[0].hdLine.Count >= 3)
                {
                    EscribirPoligono(w, "10", "Cabecera", validos[0].hdLine, plano, v4, ++plnId);
                    cabecera = true;
                }

                if (guias != null)
                {
                    int n = 0;
                    foreach (var g in guias)
                    {
                        if (!Exportable(g)) continue;
                        n++;
                        if (v4) EscribirGuiaV4(w, g, plano, n);
                        else EscribirLineaGuia(w, g, plano, false, Nombre(g.name, "Guía " + n));
                    }
                }

                w.WriteEndElement(); // PFD
                w.WriteEndElement(); // ISO11783_TaskData
            }
            sb.Append("\r\n");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------

        private static bool Exportable(CTrk g)
        {
            if (g == null) return false;
            if (g.mode == TrackMode.AB) return true;
            return g.mode == TrackMode.Curve && g.curvePts != null && g.curvePts.Count >= 3;
        }

        private static void EscribirPoligono(XmlWriter w, string tipo, string nombre, IList<vec3> pts,
                                             LocalPlane plano, bool v4, int id)
        {
            w.WriteStartElement("PLN");
            w.WriteAttributeString("A", tipo);
            if (!string.IsNullOrEmpty(nombre)) w.WriteAttributeString("B", nombre);
            w.WriteAttributeString("C", ((ulong)Math.Round(Math.Abs(AreaShoelace(pts)))).ToString(CultureInfo.InvariantCulture));
            if (v4) w.WriteAttributeString("E", "PLN" + id.ToString(CultureInfo.InvariantCulture));
            w.WriteStartElement("LSG");
            w.WriteAttributeString("A", "1");   // polygon exterior
            foreach (var p in pts) Punto(w, "2", p.easting, p.northing, plano);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void EscribirGuiaV4(XmlWriter w, CTrk g, LocalPlane plano, int n)
        {
            string nombre = Nombre(g.name, "Guía " + n);
            string idx = n.ToString(CultureInfo.InvariantCulture);
            w.WriteStartElement("GGP");
            w.WriteAttributeString("A", "GGP" + idx);
            w.WriteAttributeString("B", nombre);
            w.WriteStartElement("GPN");
            w.WriteAttributeString("A", "GPN" + idx);
            w.WriteAttributeString("B", nombre);
            w.WriteAttributeString("C", g.mode == TrackMode.AB ? "1" : "3");   // 1 = AB, 3 = curva
            w.WriteAttributeString("E", "1");   // propagación: a ambos lados
            w.WriteAttributeString("F", "1");   // extensión: desde el primero y el último punto
            if (g.mode == TrackMode.AB)
            {
                AbPuntos(g, out var a, out var b);
                double rumbo = Math.Atan2(b.easting - a.easting, b.northing - a.northing) * 180.0 / Math.PI;
                if (rumbo < 0) rumbo += 360.0;
                if (rumbo >= 360.0) rumbo -= 360.0;
                w.WriteAttributeString("G", rumbo.ToString("0.######", CultureInfo.InvariantCulture));
            }
            w.WriteAttributeString("I", "16");  // método GNSS: 16 = generado en escritorio (no es una medición)
            EscribirLineaGuia(w, g, plano, true, null);
            w.WriteEndElement(); // GPN
            w.WriteEndElement(); // GGP
        }

        /// <summary>LSG tipo 5. v4: PNT 6 (A), 9 (intermedios), 7 (B). v3: todos 2.</summary>
        private static void EscribirLineaGuia(XmlWriter w, CTrk g, LocalPlane plano, bool v4, string nombreV3)
        {
            w.WriteStartElement("LSG");
            w.WriteAttributeString("A", "5");
            if (!string.IsNullOrEmpty(nombreV3)) w.WriteAttributeString("B", nombreV3);
            if (g.mode == TrackMode.AB)
            {
                AbPuntos(g, out var a, out var b);
                Punto(w, v4 ? "6" : "2", a.easting, a.northing, plano);
                Punto(w, v4 ? "7" : "2", b.easting, b.northing, plano);
            }
            else
            {
                int last = g.curvePts.Count - 1;
                for (int i = 0; i <= last; i++)
                {
                    string tipo = !v4 ? "2" : i == 0 ? "6" : i == last ? "7" : "9";
                    Punto(w, tipo, g.curvePts[i].easting, g.curvePts[i].northing, plano);
                }
            }
            w.WriteEndElement();
        }

        private static void AbPuntos(CTrk g, out vec2 a, out vec2 b)
        {
            a = g.ptA;
            b = g.ptB;
            double dx = b.easting - a.easting, dy = b.northing - a.northing;
            if (dx * dx + dy * dy < 0.25)
            {
                // B no definida (o pegada a A): se arma con el rumbo de la guía.
                b = new vec2(a.easting + Math.Sin(g.heading) * LargoAbSinB,
                             a.northing + Math.Cos(g.heading) * LargoAbSinB);
            }
        }

        private static void Punto(XmlWriter w, string tipo, double easting, double northing, LocalPlane plano)
        {
            Wgs84 ll = plano.ConvertGeoCoordToWgs84(new GeoCoord(northing, easting));
            w.WriteStartElement("PNT");
            w.WriteAttributeString("A", tipo);
            // 9 decimales ≈ 0,1 mm: el redondeo no mueve nada.
            w.WriteAttributeString("C", ll.Latitude.ToString("0.#########", CultureInfo.InvariantCulture));
            w.WriteAttributeString("D", ll.Longitude.ToString("0.#########", CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        private static string Nombre(string s, string porDefecto)
        {
            string t = (s ?? "").Trim();
            if (t.Length == 0) t = porDefecto;
            // ISO 11783-10: designadores de hasta 32 caracteres.
            return t.Length > 32 ? t.Substring(0, 32) : t;
        }

        private static double AreaShoelace(IList<vec3> pts)
        {
            if (pts == null || pts.Count < 3) return 0;
            double s = 0;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                s += (pts[j].easting * pts[i].northing) - (pts[i].easting * pts[j].northing);
            return s / 2.0;
        }

        private static string ResolverCarpeta(string destino)
        {
            string d = (destino ?? "").Trim();
            if (d.Length == 0) return null;
            if (d.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) d = Path.GetDirectoryName(Path.GetFullPath(d));
            d = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // "E:\" → TrimEnd deja "E:"; Path.Combine("E:", x) sería relativo.
            if (d.EndsWith(":", StringComparison.Ordinal)) d += Path.DirectorySeparatorChar;
            return string.Equals(Path.GetFileName(d), Carpeta, StringComparison.OrdinalIgnoreCase)
                ? d
                : Path.Combine(d, Carpeta);
        }
    }
}
