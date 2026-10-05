// ============================================================================
// PlanimetriaCabina.cs — puente entre el resultado de Planimetria (que vive en
// su propia proyección, igual que en OrbitX) y el mapa de PilotX (plano local
// del lote: easting/northing SIN deriva, el mismo del límite y las guías).
//
//   · ArmarCapa: la capa que dibuja el mapa — grilla compacta (cm sobre una
//     base, 65535 = sin dato), zona de ambiente por celda, esquinas de la
//     grilla y curvas de nivel ya simplificadas, todo en el plano local.
//   · LineasLocales: las líneas de una cota en el plano local (para la guía).
//   · CotaEn: altura del mapa en un lat/lon (bilineal), p. ej. bajo el tractor.
//
// Las dos proyecciones (la equirectangular de Planimetria y la LocalPlane del
// lote) son lineales en lat/lon y alineadas a norte: un rectángulo de una es
// un rectángulo de la otra. Por eso alcanza con convertir dos esquinas para
// ubicar la textura, y la conversión de vértices de curva es exacta.
//
// Clase PURA: la conversión al plano local la pasa quien llama (el motor usa
// AppModelField.LocalPlane.ConvertWgs84ToGeoCoord). C# 7.3.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>lat/lon → plano local del lote (easting, northing).</summary>
    public delegate void ConvertirALocal(double lat, double lon, out double easting, out double northing);

    public sealed class CurvaCapa
    {
        public double Elev;
        /// <summary>Cada 5 intervalos: se dibuja más gruesa.</summary>
        public bool Maestra;
        /// <summary>Cada línea como e0,n0,e1,n1,… en el plano local.</summary>
        public List<float[]> Lineas = new List<float[]>();
    }

    public sealed class CapaPlanimetria
    {
        public const ushort Nulo = 0xFFFF;
        public int Nx, Ny;
        /// <summary>Bordes de la grilla (bordes de celda) en el plano local.</summary>
        public double EOeste, EEste, NSur, NNorte;
        /// <summary>z = ZBase + ZCm/100; fila 0 = norte.</summary>
        public double ZBase;
        public ushort[] ZCm;
        /// <summary>Zona de ambiente por celda (0 sin dato, 1 bajo, 2 media, 3 loma). Null si no se pidió.</summary>
        public byte[] Zona;
        public double ZMin, ZMax, Intervalo;
        public List<CurvaCapa> Curvas = new List<CurvaCapa>();
    }

    public static class PlanimetriaCabina
    {
        /// <summary>Tolerancia de Douglas-Peucker (en celdas): la misma que usa OrbitX al serializar.</summary>
        public const double TolSimplificar = 0.12;

        public static CapaPlanimetria ArmarCapa(ResultadoPlanimetria R, ResultadoAmbientacion A, ConvertirALocal conv)
        {
            var G = R.Grilla;
            int N = G.Nx * G.Ny;
            var capa = new CapaPlanimetria { Nx = G.Nx, Ny = G.Ny, Intervalo = R.Curvas.Intervalo, ZCm = new ushort[N] };
            double zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
            for (int i = 0; i < N; i++)
            {
                double v = G.Z[i];
                if (double.IsNaN(v)) continue;
                if (v < zmin) zmin = v;
                if (v > zmax) zmax = v;
            }
            if (double.IsInfinity(zmin)) { zmin = 0; zmax = 0; }
            capa.ZMin = zmin;
            capa.ZMax = zmax;
            capa.ZBase = Math.Floor(zmin * 100) / 100;
            for (int i = 0; i < N; i++)
            {
                double v = G.Z[i];
                capa.ZCm[i] = double.IsNaN(v) ? CapaPlanimetria.Nulo : (ushort)Math.Min(65534, Math.Floor((v - capa.ZBase) * 100 + 0.5));
            }
            if (A != null) capa.Zona = A.Zona;

            // Esquinas (bordes de celda) → plano local.
            double xW = G.X0 - G.Res / 2, xE = G.X0 + (G.Nx - 1) * G.Res + G.Res / 2;
            double yN = G.YTop + G.Res / 2, yS = G.YTop - (G.Ny - 1) * G.Res - G.Res / 2;
            double la, lo, e1, n1, e2, n2;
            R.Proyeccion.ALatLon(xW, yS, out la, out lo);
            conv(la, lo, out e1, out n1);
            R.Proyeccion.ALatLon(xE, yN, out la, out lo);
            conv(la, lo, out e2, out n2);
            capa.EOeste = Math.Min(e1, e2);
            capa.EEste = Math.Max(e1, e2);
            capa.NSur = Math.Min(n1, n2);
            capa.NNorte = Math.Max(n1, n2);

            double inter = R.Curvas.Intervalo > 0 ? R.Curvas.Intervalo : 0.1;
            foreach (var nv in R.Curvas.Niveles)
            {
                var c = new CurvaCapa { Elev = nv.Elev };
                long k = (long)Math.Floor(nv.Elev / inter + 0.5);
                c.Maestra = k % 5 == 0;
                foreach (var l in nv.Lineas)
                {
                    var s = Planimetria.Simplificar(l, TolSimplificar);
                    if (s.Count < 2) continue;
                    var arr = new float[s.Count * 2];
                    for (int i = 0; i < s.Count; i++)
                    {
                        double e, n;
                        R.CeldaALatLon(s[i].C, s[i].F, out la, out lo);
                        conv(la, lo, out e, out n);
                        arr[2 * i] = (float)e;
                        arr[2 * i + 1] = (float)n;
                    }
                    c.Lineas.Add(arr);
                }
                if (c.Lineas.Count > 0) capa.Curvas.Add(c);
            }
            return capa;
        }

        /// <summary>Líneas de una cota en el plano local, SIN simplificar (la guía re-muestrea sola).</summary>
        public static List<List<vec2>> LineasLocales(ResultadoPlanimetria R, NivelCurva nivel, ConvertirALocal conv)
        {
            var outp = new List<List<vec2>>();
            foreach (var l in nivel.Lineas)
            {
                var pts = new List<vec2>(l.Count);
                foreach (var p in l)
                {
                    double la, lo, e, n;
                    R.CeldaALatLon(p.C, p.F, out la, out lo);
                    conv(la, lo, out e, out n);
                    pts.Add(new vec2(e, n));
                }
                outp.Add(pts);
            }
            return outp;
        }

        /// <summary>Altura del mapa en (lat, lon): bilineal entre las 4 celdas
        /// vecinas; si alguna no tiene dato, la celda más cercana; NaN fuera.</summary>
        public static double CotaEn(ResultadoPlanimetria R, double lat, double lon)
        {
            if (R == null || !R.Ok) return double.NaN;
            var G = R.Grilla;
            double fc = (R.Proyeccion.X(lon) - G.X0) / G.Res;
            double fr = (G.YTop - R.Proyeccion.Y(lat)) / G.Res;
            if (fc < -0.5 || fr < -0.5 || fc > G.Nx - 0.5 || fr > G.Ny - 0.5) return double.NaN;
            int c0 = (int)Math.Floor(fc), r0 = (int)Math.Floor(fr);
            double tc = fc - c0, tr = fr - r0;
            double a = Celda(G, r0, c0), b = Celda(G, r0, c0 + 1), d = Celda(G, r0 + 1, c0), e = Celda(G, r0 + 1, c0 + 1);
            if (!double.IsNaN(a) && !double.IsNaN(b) && !double.IsNaN(d) && !double.IsNaN(e))
                return (a * (1 - tc) + b * tc) * (1 - tr) + (d * (1 - tc) + e * tc) * tr;
            return Celda(G, (int)Math.Floor(fr + 0.5), (int)Math.Floor(fc + 0.5));
        }

        private static double Celda(GrillaAlturas G, int r, int c)
            => (r < 0 || c < 0 || r >= G.Ny || c >= G.Nx) ? double.NaN : G.Z[r * G.Nx + c];
    }
}
