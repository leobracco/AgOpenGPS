// ============================================================================
// PlanimetriaSintetico.cs — lote sintético para probar Planimetria.cs.
//
// PORT EXACTO de tests/lib/planimetria-sintetico.mjs de OrbitX: mismo PRNG
// (mulberry32), mismo orden de consumo de números al azar y mismo formato de
// filas, así que con la misma semilla genera el MISMO Elevation.txt que usa
// Node. Por eso PlanimetriaTests puede comparar la grilla de C# contra la que
// volcó Node (Fixtures/planimetria-ref.json) celda por celda.
//
// ~400×300 m con lomas suaves (±1,5 m), pasadas cada 8 m en serpentina con
// giros en la cabecera, dos vueltas de cabecera, ruido de 2 cm, un sesgo
// distinto por pasada (±3 cm) y un pozo de 50 cm. Mezcla filas viejas de AOG
// (Quality ≠ 4 y una con separador de miles) que el parser tiene que tirar.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgOpenGPS.Core.Tests
{
    internal sealed class LoteSintetico
    {
        public List<string> Partes = new List<string>();
        public ProyeccionLocal Pr;
        public int NPuntos;
        public double Amplitud;
        public bool Pozo;
        public List<double[]> Limite = new List<double[]>();
        public double PozoLat, PozoLon;

        public double VerdadLatLon(double lat, double lon)
        {
            double x = Pr.X(lon), y = Pr.Y(lat);
            return PlanimetriaSintetico.Terreno(x, y, Pozo, Amplitud);
        }

        public void ALatLon(double x, double y, out double lat, out double lon) => Pr.ALatLon(x, y, out lat, out lon);

        public void AXY(double lat, double lon, out double x, out double y)
        {
            x = Pr.X(lon);
            y = Pr.Y(lat);
        }
    }

    internal static class PlanimetriaSintetico
    {
        public const string Cabecera =
            "2026-October-03 10:00:00 AM\r\n$FieldDir\r\nElevation\r\n$Offsets\r\n0,0\r\nConvergence\r\n0\r\nStartFix\r\n-33.1,-61.7\r\n"
            + "Latitude,Longitude,Elevation,Quality,Easting,Northing,Heading,Roll\r\n";

        public const double PozoX = 87.5, PozoY = 260;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Superficie verdadera en metros locales (x este, y norte, origen esquina SO).</summary>
        public static double Terreno(double x, double y, bool pozo = true, double amplitud = 1.5)
        {
            double z = 100 + amplitud * Math.Sin(2 * Math.PI * x / 350) * Math.Cos(2 * Math.PI * y / 260) + 0.002 * x;
            if (pozo) z -= 0.5 * Math.Exp(-((x - PozoX) * (x - PozoX) + (y - PozoY) * (y - PozoY)) / (2 * 15 * 15));
            return z;
        }

        private sealed class Mulberry32
        {
            private int _a;
            public Mulberry32(int seed) { _a = seed; }

            public double Next()
            {
                unchecked
                {
                    _a = _a + 0x6D2B79F5;
                    int t = (_a ^ (int)((uint)_a >> 15)) * (1 | _a);
                    t = (t + (t ^ (int)((uint)t >> 7)) * (61 | t)) ^ t;
                    return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
                }
            }
        }

        private struct Pt
        {
            public double X, Y, B;
            public Pt(double x, double y, double b) { X = x; Y = y; B = b; }
        }

        public static LoteSintetico Generar(int seed = 7, double largo = 400, double ancho = 300, double espaciado = 8,
            double paso = 1, double ruido = 0.02, double sesgo = 0.03, bool cabeceras = true, bool pozo = true,
            int filasPorParte = 5000, double lat0 = -33.1, double lon0 = -61.7, bool filasViejas = true,
            double amplitud = 1.5, double[] hueco = null)
        {
            var rnd = new Mulberry32(seed);
            Func<double> gauss = () =>
            {
                double u = 0;
                while (u == 0) u = rnd.Next();
                return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * rnd.Next());
            };
            var pr = new ProyeccionLocal(lat0, lon0);
            var pts = new List<Pt>();
            Func<double> nuevaPasada = () => (rnd.Next() * 2 - 1) * sesgo;

            int nPas = (int)Math.Floor((ancho - espaciado / 2) / espaciado) + 1;
            for (int k = 0; k < nPas; k++)
            {
                double y = espaciado / 2 + k * espaciado;
                bool ida = k % 2 == 0;
                double b = nuevaPasada();
                if (hueco != null && y >= hueco[0] && y <= hueco[1]) continue;
                for (double s = 0; s <= largo; s += paso) pts.Add(new Pt(ida ? s : largo - s, y, b));
                if (k < nPas - 1)
                {
                    double r = espaciado / 2, cx = ida ? largo : 0, cy = y + r;
                    int nArc = (int)Math.Ceiling(Math.PI * r / paso);
                    for (int t = 1; t < nArc; t++)
                    {
                        double a = -Math.PI / 2 + Math.PI * t / nArc;
                        pts.Add(new Pt(cx + (ida ? 1 : -1) * r * Math.Cos(a), cy + r * Math.Sin(a), b));
                    }
                }
            }
            if (cabeceras)
            {
                for (int v = 0; v < 2; v++)
                {
                    double m = 10 + v * espaciado;
                    double[][] esq =
                    {
                        new[] { -m, -m / 2 }, new[] { largo + m, -m / 2 }, new[] { largo + m, ancho + m / 2 },
                        new[] { -m, ancho + m / 2 }, new[] { -m, -m / 2 },
                    };
                    for (int e = 0; e < 4; e++)
                    {
                        double b = nuevaPasada();
                        double x1 = esq[e][0], y1 = esq[e][1], x2 = esq[e + 1][0], y2 = esq[e + 1][1];
                        double L = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                        for (double s = 0; s < L; s += paso) pts.Add(new Pt(x1 + (x2 - x1) * s / L, y1 + (y2 - y1) * s / L, b));
                    }
                }
            }

            var filas = new List<string>(pts.Count + 3);
            foreach (var p in pts)
            {
                double z = Terreno(p.X, p.Y, pozo, amplitud) + p.B + ruido * gauss();
                double lat, lon;
                pr.ALatLon(p.X, p.Y, out lat, out lon);
                string rumbo = (rnd.Next() * 6.28).ToString("F3", Inv);
                string rolido = (gauss() * 0.5).ToString("F3", Inv);
                filas.Add(lat.ToString("F7", Inv) + "," + lon.ToString("F7", Inv) + "," + z.ToString("F3", Inv) + ",4,"
                    + p.X.ToString("F2", Inv) + "," + p.Y.ToString("F2", Inv) + "," + rumbo + "," + rolido);
            }
            if (filasViejas)
            {
                double la, lo;
                pr.ALatLon(200, 150, out la, out lo);
                string sla = la.ToString("F7", Inv), slo = lo.ToString("F7", Inv);
                filas.Insert(100, sla + "," + slo + ",150.000,1,200.00,150.00,0.000,0");
                filas.Insert(2000, sla + "," + slo + ",90.000,5,200.00,150.00,0.000,0");
                filas.Insert(3000, sla + "," + slo + ",100.000,4,1,234.56,150.00,0.000,0");
            }

            var lote = new LoteSintetico { Pr = pr, NPuntos = pts.Count, Amplitud = amplitud, Pozo = pozo };
            for (int i = 0; i < filas.Count; i += filasPorParte)
            {
                var sb = new StringBuilder(Cabecera);
                int fin = Math.Min(filas.Count, i + filasPorParte);
                for (int j = i; j < fin; j++)
                {
                    if (j > i) sb.Append("\r\n");
                    sb.Append(filas[j]);
                }
                sb.Append("\r\n");
                lote.Partes.Add(sb.ToString());
            }
            foreach (var xy in new[] { new[] { 0.0, 0 }, new[] { largo, 0 }, new[] { largo, ancho }, new[] { 0.0, ancho } })
            {
                double la, lo;
                pr.ALatLon(xy[0], xy[1], out la, out lo);
                lote.Limite.Add(new[] { la, lo });
            }
            pr.ALatLon(PozoX, PozoY, out lote.PozoLat, out lote.PozoLon);
            return lote;
        }
    }
}
