// ============================================================================
// VxRegistroArchivo.cs — disco del registro VistaX por lote.
//
// Dónde: <lote>/VistaX/Surcos/
//   vistax_surcos_0001.ndjson, _0002…  tramos (≈10 m) con los datos por surco
//   resumen.json                       promedios por surco de todo el lote
//   vistax_surcos.shp/.shx/.dbf/.prj   mapa por surco (un punto por surco y
//                                      tramo), se regenera al cerrar el lote
//
// Por qué en PARTES (como Elevation.txt, ver ElevacionPartes): OrbitXSync
// re-sube un archivo cada vez que cambia su hash y el server archiva una
// copia por cambio. Con partes de 200 tramos (≈ 2 km de pasada, ≈ 250 KB con
// 30 surcos) cada parte completa se sube UNA vez; solo la última crece.
//
// Formato NDJSON (schema agp.vistax.surcos/1), compacto a propósito — un lote
// de 100 ha con una sembradora de 30 surcos junta ~6.000 tramos:
//   1ª línea de cada parte: cabecera con "cols" (orden de las columnas de "s").
//   resto: un tramo por línea
//     {"t":"2026-10-03T14:05:09Z","dur":4.6,"lat":-33.123456,"lon":-61.123456,
//      "rumbo":91.2,"dist":10.1,"vel":7.9,
//      "s":[[tren,surco,off_m,sem_m,sing,dob,fal,cv,n], …]}
//   Posición de cada surco = lat/lon del tramo + off_m metros a la DERECHA del
//   rumbo (0° = norte, sentido horario). sem_m null = sin lectura válida;
//   sing/dob/fal/cv null y n = 0 = sin espaciamiento (nodo < v3.1).
//
// Sin dependencias de PilotX: lo cubre VxRegistroArchivoTests con carpetas
// temporales. Números SIEMPRE con InvariantCulture (punto decimal).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;

namespace AgroParallel.Services.VistaX
{
    public sealed class VxRegistroArchivo
    {
        public const string Schema = "agp.vistax.surcos/1";
        public const string Carpeta = "Surcos";
        public const string Prefijo = "vistax_surcos_";
        public const string NombreResumen = "resumen.json";
        public const string NombreShp = "vistax_surcos.shp";
        public const int TramosPorParteDefault = 200;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly UTF8Encoding Utf8SinBom = new UTF8Encoding(false);

        private readonly string _dir;
        private readonly string _lote;
        private readonly int _porParte;
        private int _parte;
        private int _enParte;

        /// <summary>Carpeta del registro dentro de la carpeta del lote.</summary>
        public static string DirDeLote(string loteDir)
        {
            return Path.Combine(loteDir, "VistaX", Carpeta);
        }

        public static string NombreParte(int indice)
        {
            return Prefijo + indice.ToString("0000", Inv) + ".ndjson";
        }

        /// <summary>true si el nombre es una parte (vistax_surcos_NNNN.ndjson).</summary>
        public static bool EsParte(string nombreArchivo, out int indice)
        {
            indice = 0;
            if (string.IsNullOrEmpty(nombreArchivo)) return false;
            string n = Path.GetFileName(nombreArchivo);
            if (!n.StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase)) return false;
            if (!n.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)) return false;
            string num = n.Substring(Prefijo.Length, n.Length - Prefijo.Length - ".ndjson".Length);
            return int.TryParse(num, NumberStyles.None, Inv, out indice) && indice > 0;
        }

        /// <summary>Partes existentes en <paramref name="dir"/>, en orden.</summary>
        public static List<string> Partes(string dir)
        {
            var lista = new List<KeyValuePair<int, string>>();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, Prefijo + "*.ndjson"))
                {
                    int i;
                    if (EsParte(f, out i)) lista.Add(new KeyValuePair<int, string>(i, f));
                }
            }
            lista.Sort((a, b) => a.Key.CompareTo(b.Key));
            var r = new List<string>(lista.Count);
            foreach (var kv in lista) r.Add(kv.Value);
            return r;
        }

        /// <summary>
        /// Abre (sin crear nada en disco todavía) el registro de un lote. Si ya
        /// hay partes, sigue en la última y carga el resumen con lo que hay.
        /// </summary>
        public VxRegistroArchivo(string dir, string lote, int tramosPorParte = TramosPorParteDefault)
        {
            _dir = dir;
            _lote = lote ?? "";
            _porParte = tramosPorParte < 1 ? 1 : tramosPorParte;
            Resumen = new VxResumenLote { Lote = _lote };

            var partes = Partes(dir);
            if (partes.Count == 0) { _parte = 1; _enParte = 0; return; }
            foreach (var p in partes)
            {
                int enEsta = 0;
                foreach (var t in LeerTramosDeArchivo(p)) { Resumen.Sumar(t); enEsta++; }
                _enParte = enEsta;
            }
            int ult;
            EsParte(partes[partes.Count - 1], out ult);
            _parte = ult;
        }

        public string Dir { get { return _dir; } }
        public VxResumenLote Resumen { get; private set; }
        public int ParteActual { get { return _parte; } }
        public int TramosEscritos { get; private set; }

        /// <summary>Agrega un tramo (append + flush por tramo: si se corta la
        /// luz se pierde como mucho el tramo en curso).</summary>
        public void Escribir(VxTramo t)
        {
            if (t == null) return;
            Directory.CreateDirectory(_dir);
            if (_enParte >= _porParte) { _parte++; _enParte = 0; }
            string path = Path.Combine(_dir, NombreParte(_parte));
            var sb = new StringBuilder(256 + t.Surcos.Count * 48);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                sb.Append(Cabecera(_lote, _parte)).Append('\n');
            sb.Append(SerializarTramo(t)).Append('\n');
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var w = new StreamWriter(fs, Utf8SinBom))
            {
                w.Write(sb.ToString());
            }
            _enParte++;
            TramosEscritos++;
            Resumen.Sumar(t);
        }

        public void GuardarResumen(DateTime generadoUtc)
        {
            if (Resumen.Tramos == 0) return;
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, NombreResumen);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Resumen.ToJson(generadoUtc), Utf8SinBom);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Regenera el SHP de puntos por surco con todas las partes.
        /// Devuelve la cantidad de puntos (0 = no escribió nada).</summary>
        public int ExportarShp()
        {
            return ExportarShp(_dir, Path.Combine(_dir, NombreShp));
        }

        // ── Formato ─────────────────────────────────────────────────────────

        public static string Cabecera(string lote, int parte)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"schema\":\"").Append(Schema).Append("\",\"lote\":");
            AppendJsonString(sb, lote ?? "");
            sb.Append(",\"parte\":").Append(parte.ToString(Inv));
            sb.Append(",\"cols\":[\"tren\",\"surco\",\"off_m\",\"sem_m\",\"sing\",\"dob\",\"fal\",\"cv\",\"n\"]");
            sb.Append(",\"pos\":\"lat/lon del tramo + off_m a la derecha del rumbo\"}");
            return sb.ToString();
        }

        public static string SerializarTramo(VxTramo t)
        {
            var sb = new StringBuilder(160 + t.Surcos.Count * 48);
            sb.Append("{\"t\":\"").Append(t.FinUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv)).Append('"');
            sb.Append(",\"dur\":").Append(Num((t.FinUtc - t.InicioUtc).TotalSeconds, 1));
            sb.Append(",\"lat\":").Append(Num(t.Lat, 7));
            sb.Append(",\"lon\":").Append(Num(t.Lon, 7));
            sb.Append(",\"rumbo\":").Append(Num(t.RumboDeg, 1));
            sb.Append(",\"dist\":").Append(Num(t.DistM, 1));
            sb.Append(",\"vel\":").Append(Num(t.VelKmh, 1));
            sb.Append(",\"s\":[");
            for (int i = 0; i < t.Surcos.Count; i++)
            {
                var s = t.Surcos[i];
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(s.Tren.ToString(Inv)).Append(',').Append(s.Bajada.ToString(Inv));
                sb.Append(',').Append(Num(s.OffM, 2));
                sb.Append(',').Append(s.SemM >= 0 ? Num(s.SemM, 2) : "null");
                var e = s.Esp;
                bool hay = e != null && e.NEspacios > 0;
                sb.Append(',').Append(hay ? Num(e.SingulacionPct, 1) : "null");
                sb.Append(',').Append(hay ? Num(e.DoblesPct, 1) : "null");
                sb.Append(',').Append(hay ? Num(e.FallasPct, 1) : "null");
                sb.Append(',').Append(hay ? Num(e.CvPct, 1) : "null");
                sb.Append(',').Append(hay ? e.NEspacios.ToString(Inv) : "0");
                sb.Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Lee un tramo de una línea NDJSON. null si es la cabecera o
        /// basura (línea cortada por un apagón).</summary>
        public static VxTramo ParsearTramo(string linea)
        {
            if (string.IsNullOrWhiteSpace(linea)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(linea))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return null;
                    JsonElement js;
                    if (!r.TryGetProperty("s", out js) || js.ValueKind != JsonValueKind.Array) return null;
                    var t = new VxTramo();
                    JsonElement jt;
                    DateTime fin;
                    if (r.TryGetProperty("t", out jt) && jt.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(jt.GetString(), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out fin))
                        t.FinUtc = fin;
                    t.InicioUtc = t.FinUtc.AddSeconds(-Dbl(r, "dur"));
                    t.Lat = Dbl(r, "lat");
                    t.Lon = Dbl(r, "lon");
                    t.RumboDeg = Dbl(r, "rumbo");
                    t.DistM = Dbl(r, "dist");
                    t.VelKmh = Dbl(r, "vel");
                    foreach (var f in js.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() < 9) continue;
                        var s = new VxTramoSurco
                        {
                            Tren = (int)Idx(f, 0, 0),
                            Bajada = (int)Idx(f, 1, 0),
                            OffM = Idx(f, 2, 0),
                            SemM = Idx(f, 3, -1),
                        };
                        int n = (int)Idx(f, 8, 0);
                        if (n > 0)
                        {
                            s.Esp = new VxIndicesEspaciamiento
                            {
                                SingulacionPct = Idx(f, 4, 0),
                                DoblesPct = Idx(f, 5, 0),
                                FallasPct = Idx(f, 6, 0),
                                CvPct = Idx(f, 7, 0),
                                NEspacios = n,
                            };
                        }
                        t.Surcos.Add(s);
                    }
                    return t;
                }
            }
            catch (JsonException) { return null; }
        }

        public static IEnumerable<VxTramo> LeerTramosDeArchivo(string path)
        {
            if (!File.Exists(path)) yield break;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string l;
                while ((l = sr.ReadLine()) != null)
                {
                    var t = ParsearTramo(l);
                    if (t != null) yield return t;
                }
            }
        }

        public static IEnumerable<VxTramo> LeerTramos(string dir)
        {
            foreach (var p in Partes(dir))
                foreach (var t in LeerTramosDeArchivo(p))
                    yield return t;
        }

        /// <summary>Resumen del lote leyendo las partes (null si no hay datos).
        /// Lo usa el export de Tareas cuando no existe resumen.json.</summary>
        public static VxResumenLote ResumenDeCarpeta(string dir, string lote)
        {
            return ResumenDeCarpeta(dir, lote, null);
        }

        /// <summary>Igual, pero solo los tramos cuyo fin (UTC) cumple
        /// <paramref name="incluirFinUtc"/> — el informe de una Tarea suma solo
        /// lo sembrado con la tarea en curso.</summary>
        public static VxResumenLote ResumenDeCarpeta(string dir, string lote, Func<DateTime, bool> incluirFinUtc)
        {
            var r = new VxResumenLote { Lote = lote ?? "" };
            foreach (var t in LeerTramos(dir))
                if (incluirFinUtc == null || incluirFinUtc(t.FinUtc)) r.Sumar(t);
            return r.Tramos > 0 ? r : null;
        }

        // ── SHP ─────────────────────────────────────────────────────────────

        /// <summary>Un punto por surco y tramo, WGS84. Campos DBF ≤ 10
        /// caracteres; -1 = sin dato.</summary>
        public static int ExportarShp(string dir, string shpPath)
        {
            var factory = GeometryFactory.Default;
            var features = new List<IFeature>();
            foreach (var t in LeerTramos(dir))
            {
                if (Math.Abs(t.Lat) < 1e-6 && Math.Abs(t.Lon) < 1e-6) continue;
                foreach (var s in t.Surcos)
                {
                    double lat, lon;
                    PosicionSurco(t.Lat, t.Lon, t.RumboDeg, s.OffM, out lat, out lon);
                    var a = new AttributesTable();
                    a.Add("fecha", t.FinUtc.ToString("yyyy-MM-dd HH:mm:ss", Inv));
                    a.Add("tren", s.Tren);
                    a.Add("surco", s.Bajada);
                    a.Add("sem_m", s.SemM >= 0 ? Math.Round(s.SemM, 2) : -1.0);
                    bool hay = s.Esp != null && s.Esp.NEspacios > 0;
                    a.Add("sing", hay ? Math.Round(s.Esp.SingulacionPct, 1) : -1.0);
                    a.Add("dobles", hay ? Math.Round(s.Esp.DoblesPct, 1) : -1.0);
                    a.Add("fallas", hay ? Math.Round(s.Esp.FallasPct, 1) : -1.0);
                    a.Add("cv", hay ? Math.Round(s.Esp.CvPct, 1) : -1.0);
                    a.Add("n_esp", hay ? s.Esp.NEspacios : 0);
                    a.Add("vel_kmh", Math.Round(t.VelKmh, 1));
                    features.Add(new Feature(factory.CreatePoint(new Coordinate(lon, lat)), a));
                }
            }
            if (features.Count == 0) return 0;
            Shapefile.WriteAllFeatures(features, shpPath);
            File.WriteAllText(Path.ChangeExtension(shpPath, ".prj"),
                "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\","
                + "SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],"
                + "PRIMEM[\"Greenwich\",0.0],"
                + "UNIT[\"Degree\",0.0174532925199433]]");
            return features.Count;
        }

        /// <summary>Lat/lon de un surco: <paramref name="offM"/> metros a la
        /// derecha del rumbo (0° = norte, horario). Aproximación plana, de
        /// sobra para los ~10 m de un implemento.</summary>
        public static void PosicionSurco(double lat, double lon, double rumboDeg, double offM,
            out double latSurco, out double lonSurco)
        {
            double h = rumboDeg * Math.PI / 180.0;
            double dE = offM * Math.Cos(h);
            double dN = -offM * Math.Sin(h);
            latSurco = lat + dN / 111320.0;
            double cosLat = Math.Cos(lat * Math.PI / 180.0);
            lonSurco = lon + (Math.Abs(cosLat) > 1e-9 ? dE / (111320.0 * cosLat) : 0);
        }

        // ── helpers ─────────────────────────────────────────────────────────

        internal static string Num(double v, int dec)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return Math.Round(v, dec).ToString("0.#######", Inv);
        }

        private static double Dbl(JsonElement r, string k)
        {
            JsonElement e;
            double d;
            return r.TryGetProperty(k, out e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out d) ? d : 0;
        }

        private static double Idx(JsonElement arr, int i, double porDefecto)
        {
            var e = arr[i];
            double d;
            return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out d) ? d : porDefecto;
        }

        internal static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
