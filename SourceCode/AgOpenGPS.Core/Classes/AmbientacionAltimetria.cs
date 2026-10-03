// ============================================================================
// AmbientacionAltimetria.cs — planimetría fase 3: ambientes por altura
// (loma / media loma / bajo) a partir de la grilla de Planimetria, y la
// prescripción GeoJSON que los representa con una dosis por zona.
//
// Dos criterios (editables por el operario):
//   · Percentil: bajo = el p_bajo % más bajo de la superficie relevada, loma =
//     el (100 − p_loma) % más alto. Por defecto 25 / 75: un cuarto de bajo, un
//     cuarto de loma, la mitad media loma. Sirve en cualquier lote.
//   · Desnivel: respecto de la altura MEDIANA del lote, bajo = más de d_bajo m
//     por debajo, loma = más de d_loma m por encima (default 0,30 / 0,30). Es
//     el criterio del agrónomo que dice "medio metro abajo ya es bajo".
//
// Antes de armar polígonos se pasa un filtro de mayoría 3×3 (una celda suelta
// rodeada de otra zona toma la de sus vecinas): sin él la prescripción tendría
// miles de cuadraditos y el dosificador cambiaría de dosis a cada metro.
//
// La prescripción NO inventa maquinaria: es un GeoJSON (FeatureCollection de
// Polygon en [lon, lat] WGS84, propiedades "zona" y "dosis") que se escribe en
// la carpeta de prescripciones y se activa con PrescripcionService, igual que
// una que baja de OrbitX o se copia de un pendrive. Las celdas se juntan en
// rectángulos (corridas por fila que se estiran hacia abajo mientras la fila
// siguiente repite la corrida) para que queden pocos polígonos y sin solapes.
//
// Clase PURA: sin disco ni lote. C# 7.3.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgOpenGPS
{
    public enum ModoAmbientacion
    {
        Percentil = 0,
        Desnivel = 1,
    }

    public sealed class ConfigAmbientacion
    {
        public ModoAmbientacion Modo = ModoAmbientacion.Percentil;
        /// <summary>Percentil (0–100) debajo del cual es bajo.</summary>
        public double PBajo = 25;
        /// <summary>Percentil (0–100) encima del cual es loma.</summary>
        public double PLoma = 75;
        /// <summary>Metros debajo de la mediana desde los que es bajo.</summary>
        public double DBajoM = 0.30;
        /// <summary>Metros encima de la mediana desde los que es loma.</summary>
        public double DLomaM = 0.30;
        /// <summary>Filtro de mayoría 3×3 antes de armar zonas.</summary>
        public bool Suavizar = true;
    }

    public sealed class ResultadoAmbientacion
    {
        public const byte SinDato = 0, Bajo = 1, Media = 2, Loma = 3;

        /// <summary>Zona por celda (mismo orden que GrillaAlturas.Z).</summary>
        public byte[] Zona;
        /// <summary>Cota (m) por debajo de la cual es bajo / por encima de la cual es loma.</summary>
        public double CotaBajo, CotaLoma;
        /// <summary>Superficie por zona (ha), índice = código de zona.</summary>
        public double[] AreaHa = new double[4];
    }

    /// <summary>Rectángulo de celdas de una misma zona (índices inclusivos).</summary>
    public struct RectanguloZona
    {
        public byte Zona;
        public int C0, C1, R0, R1;
    }

    public static class AmbientacionAltimetria
    {
        public static readonly string[] Nombres = { "", "Bajo", "Media loma", "Loma" };

        public static ResultadoAmbientacion Clasificar(GrillaAlturas g, ConfigAmbientacion cfg)
        {
            if (cfg == null) cfg = new ConfigAmbientacion();
            int N = g.Nx * g.Ny;
            var validos = new List<double>(N);
            for (int i = 0; i < N; i++) if (!float.IsNaN(g.Z[i])) validos.Add(g.Z[i]);
            var res = new ResultadoAmbientacion { Zona = new byte[N] };
            if (validos.Count == 0) return res;
            validos.Sort();

            if (cfg.Modo == ModoAmbientacion.Desnivel)
            {
                double med = Planimetria.Mediana(validos);
                res.CotaBajo = med - Math.Abs(cfg.DBajoM);
                res.CotaLoma = med + Math.Abs(cfg.DLomaM);
            }
            else
            {
                double pb = Math.Max(0, Math.Min(100, cfg.PBajo));
                double pl = Math.Max(pb, Math.Min(100, cfg.PLoma));
                res.CotaBajo = Percentil(validos, pb);
                res.CotaLoma = Percentil(validos, pl);
            }

            for (int i = 0; i < N; i++)
            {
                float z = g.Z[i];
                if (float.IsNaN(z)) continue;
                res.Zona[i] = z < res.CotaBajo ? ResultadoAmbientacion.Bajo
                    : z > res.CotaLoma ? ResultadoAmbientacion.Loma
                    : ResultadoAmbientacion.Media;
            }
            if (cfg.Suavizar) res.Zona = Mayoria(res.Zona, g.Nx, g.Ny);

            double haCelda = g.Res * g.Res / 1e4;
            for (int i = 0; i < N; i++) res.AreaHa[res.Zona[i]] += haCelda;
            res.AreaHa[0] = 0;
            return res;
        }

        /// <summary>Percentil sobre valores ORDENADOS (mismo criterio que las
        /// estadísticas de Planimetria: s[min(n−1, ⌊p·n⌋)]).</summary>
        public static double Percentil(IList<double> ordenados, double p)
        {
            int n = ordenados.Count;
            if (n == 0) return double.NaN;
            int k = (int)Math.Floor(p / 100.0 * n);
            return ordenados[Math.Max(0, Math.Min(n - 1, k))];
        }

        /// <summary>Filtro de mayoría 3×3: una celda toma la zona que tenga 5 o
        /// más de las 9 (ella incluida). Las celdas sin dato no votan ni cambian.</summary>
        public static byte[] Mayoria(byte[] zona, int nx, int ny)
        {
            var outp = (byte[])zona.Clone();
            var votos = new int[4];
            for (int r = 0; r < ny; r++)
            {
                for (int c = 0; c < nx; c++)
                {
                    int i = r * nx + c;
                    if (zona[i] == 0) continue;
                    votos[1] = votos[2] = votos[3] = 0;
                    for (int dr = -1; dr <= 1; dr++)
                    {
                        int rr = r + dr;
                        if (rr < 0 || rr >= ny) continue;
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            int cc = c + dc;
                            if (cc < 0 || cc >= nx) continue;
                            votos[zona[rr * nx + cc]]++;
                        }
                    }
                    for (byte z = 1; z <= 3; z++) if (votos[z] >= 5) { outp[i] = z; break; }
                }
            }
            return outp;
        }

        /// <summary>Junta las celdas en rectángulos disjuntos de una misma zona.
        /// Cubren EXACTAMENTE las celdas con zona ≠ 0.</summary>
        public static List<RectanguloZona> Rectangulos(byte[] zona, int nx, int ny)
        {
            var cerrados = new List<RectanguloZona>();
            // Abiertos: clave (c0, c1, zona) → rectángulo que llega hasta la fila anterior.
            var abiertos = new Dictionary<long, RectanguloZona>();
            var siguientes = new Dictionary<long, RectanguloZona>();
            for (int r = 0; r < ny; r++)
            {
                siguientes.Clear();
                int c = 0;
                while (c < nx)
                {
                    byte z = zona[r * nx + c];
                    int c0 = c;
                    while (c + 1 < nx && zona[r * nx + c + 1] == z) c++;
                    int c1 = c;
                    c++;
                    if (z == 0) continue;
                    long clave = ((long)c0 << 32) | ((long)c1 << 8) | z;
                    RectanguloZona rect;
                    if (abiertos.TryGetValue(clave, out rect))
                    {
                        rect.R1 = r;
                        abiertos.Remove(clave);
                    }
                    else rect = new RectanguloZona { Zona = z, C0 = c0, C1 = c1, R0 = r, R1 = r };
                    siguientes[clave] = rect;
                }
                foreach (var kv in abiertos) cerrados.Add(kv.Value);
                var tmp = abiertos;
                abiertos = siguientes;
                siguientes = tmp;
            }
            foreach (var kv in abiertos) cerrados.Add(kv.Value);
            return cerrados;
        }

        /// <summary>
        /// GeoJSON de la prescripción: un Polygon por rectángulo, propiedades
        /// "zona" (nombre) y "dosis" (número). <paramref name="dosisPorZona"/>
        /// se indexa por código de zona (1 bajo, 2 media, 3 loma).
        /// </summary>
        public static string PrescripcionGeoJson(ResultadoPlanimetria R, ResultadoAmbientacion A, double[] dosisPorZona)
        {
            var G = R.Grilla;
            var rects = Rectangulos(A.Zona, G.Nx, G.Ny);
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256 + rects.Count * 200);
            sb.Append("{\"type\":\"FeatureCollection\",\"features\":[");
            bool primero = true;
            // Orden estable: por zona y después por fila/columna (el primer
            // feature define las propiedades candidatas en PrescripcionService).
            rects.Sort((a, b) =>
            {
                int k = a.Zona.CompareTo(b.Zona);
                if (k != 0) return k;
                k = a.R0.CompareTo(b.R0);
                return k != 0 ? k : a.C0.CompareTo(b.C0);
            });
            foreach (var q in rects)
            {
                double xW = G.X0 + (q.C0 - 0.5) * G.Res, xE = G.X0 + (q.C1 + 0.5) * G.Res;
                double yN = G.YTop - (q.R0 - 0.5) * G.Res, yS = G.YTop - (q.R1 + 0.5) * G.Res;
                double latS, lonW, latN, lonE;
                R.Proyeccion.ALatLon(xW, yS, out latS, out lonW);
                R.Proyeccion.ALatLon(xE, yN, out latN, out lonE);
                double dosis = dosisPorZona != null && q.Zona < dosisPorZona.Length ? dosisPorZona[q.Zona] : 0;
                if (double.IsNaN(dosis) || double.IsInfinity(dosis)) dosis = 0;
                if (!primero) sb.Append(',');
                primero = false;
                sb.Append("{\"type\":\"Feature\",\"properties\":{\"zona\":\"").Append(Nombres[q.Zona])
                  .Append("\",\"dosis\":").Append(dosis.ToString("0.###", inv))
                  .Append("},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[");
                AppendLonLat(sb, lonW, latS, inv); sb.Append(',');
                AppendLonLat(sb, lonE, latS, inv); sb.Append(',');
                AppendLonLat(sb, lonE, latN, inv); sb.Append(',');
                AppendLonLat(sb, lonW, latN, inv); sb.Append(',');
                AppendLonLat(sb, lonW, latS, inv);
                sb.Append("]]}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendLonLat(StringBuilder sb, double lon, double lat, CultureInfo inv)
        {
            sb.Append('[').Append(lon.ToString("0.#########", inv)).Append(',').Append(lat.ToString("0.#########", inv)).Append(']');
        }
    }
}
