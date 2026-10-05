// ============================================================================
// GuiaCurvaNivel.cs — planimetría fase 3: de una curva de nivel del mapa de
// alturas a los puntos de una guía curva (siembra en contorno).
//
// Entrada: las líneas de UNA cota ya pasadas al plano local del lote
// (easting/northing, sin deriva — el mismo plano del límite y las guías) y la
// posición de referencia (el tractor). Salida: la polilínea que va a ser el
// CTrk modo Curve, o null si no hay una línea usable.
//
// Qué hace:
//   1. Elige, de todas las líneas de esa cota, la más CERCANA a la referencia
//      (en un lote con dos lomas la cota 101,5 m da dos anillos: el operario
//      quiere el que tiene adelante). Descarta las de menos de minLargo m.
//   2. Si la línea es un anillo cerrado (curva alrededor de una loma o de un
//      bajo), lo abre en el vértice más cercano al tractor: la guía arranca
//      donde está la máquina.
//   3. Re-muestrea a paso fijo (1,5 m: por debajo de los 1,6 m de
//      MakePointMinimumSpacing, que así no agrega puntos) y suaviza con media
//      móvil (±2 puntos, extremos fijos; en anillos, circular). La curva de
//      marching squares sobre celdas de 3 m tiene quiebres en cada celda: sin
//      suavizar, el piloto los copiaría como serruchos del volante.
//
// No calcula rumbos ni extiende las puntas: eso lo hace la receta común de
// guías curvas del motor (CalculateHeadings + AddFirstLastPoints).
// Clase PURA. C# 7.3.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public static class GuiaCurvaNivel
    {
        public const double PasoPorDefecto = 1.5;
        public const int VentanaPorDefecto = 2;
        public const double LargoMinimoPorDefecto = 20;

        /// <summary>Distancia mínima de la referencia a la línea (por segmentos).</summary>
        public static double DistanciaALinea(List<vec2> linea, double e0, double n0, out int verticeMasCercano)
        {
            verticeMasCercano = 0;
            double mejor = double.PositiveInfinity, mejorV = double.PositiveInfinity;
            for (int i = 0; i < linea.Count; i++)
            {
                double dv = Dist2(linea[i].easting - e0, linea[i].northing - n0);
                if (dv < mejorV) { mejorV = dv; verticeMasCercano = i; }
                if (i == 0) { mejor = Math.Min(mejor, dv); continue; }
                var a = linea[i - 1];
                var b = linea[i];
                double dx = b.easting - a.easting, dy = b.northing - a.northing, l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? ((e0 - a.easting) * dx + (n0 - a.northing) * dy) / l2 : 0;
                t = Math.Max(0, Math.Min(1, t));
                mejor = Math.Min(mejor, Dist2(a.easting + t * dx - e0, a.northing + t * dy - n0));
            }
            return Math.Sqrt(mejor);
        }

        public static double Largo(List<vec2> linea)
        {
            double L = 0;
            for (int i = 1; i < linea.Count; i++)
                L += Math.Sqrt(Dist2(linea[i].easting - linea[i - 1].easting, linea[i].northing - linea[i - 1].northing));
            return L;
        }

        public static bool EsCerrada(List<vec2> linea)
        {
            if (linea.Count < 4) return false;
            var a = linea[0];
            var b = linea[linea.Count - 1];
            return Dist2(a.easting - b.easting, a.northing - b.northing) < 1e-6;
        }

        /// <summary>Elige la línea más cercana a (e0, n0) con largo ≥ minLargo. −1 si ninguna sirve.</summary>
        public static int ElegirLinea(IList<List<vec2>> lineas, double e0, double n0, double minLargo = LargoMinimoPorDefecto)
        {
            int idx = -1;
            double mejor = double.PositiveInfinity;
            for (int k = 0; k < lineas.Count; k++)
            {
                var l = lineas[k];
                if (l == null || l.Count < 2 || Largo(l) < minLargo) continue;
                int v;
                double d = DistanciaALinea(l, e0, n0, out v);
                if (d < mejor) { mejor = d; idx = k; }
            }
            return idx;
        }

        /// <summary>
        /// Puntos de la guía (heading = 0: los calcula el motor) o null si
        /// ninguna línea de la cota sirve (todas cortas o sin líneas).
        /// </summary>
        public static List<vec3> Preparar(IList<List<vec2>> lineas, double e0, double n0,
            double paso = PasoPorDefecto, int ventana = VentanaPorDefecto, double minLargo = LargoMinimoPorDefecto)
        {
            int k = ElegirLinea(lineas, e0, n0, minLargo);
            if (k < 0) return null;
            var linea = lineas[k];
            bool cerrada = EsCerrada(linea);
            List<vec2> abierta;
            if (cerrada)
            {
                // Anillo: se saca el vértice repetido y se rota para arrancar en el más cercano.
                int v;
                DistanciaALinea(linea, e0, n0, out v);
                int n = linea.Count - 1;
                if (v >= n) v = 0;
                abierta = new List<vec2>(n + 1);
                for (int i = 0; i < n; i++) abierta.Add(linea[(v + i) % n]);
                abierta.Add(abierta[0]);
            }
            else abierta = new List<vec2>(linea);

            var re = Remuestrear(abierta, paso);
            if (cerrada && re.Count > 1)
            {
                // El último punto repite el primero: fuera, para que la media circular no lo cuente doble.
                var u = re[re.Count - 1];
                if (Dist2(u.easting - re[0].easting, u.northing - re[0].northing) < (paso * 0.5) * (paso * 0.5)) re.RemoveAt(re.Count - 1);
            }
            var suave = Suavizar(re, ventana, cerrada);
            if (suave.Count < 4) return null;
            var outp = new List<vec3>(suave.Count);
            foreach (var p in suave) outp.Add(new vec3(p.easting, p.northing, 0));
            return outp;
        }

        /// <summary>Re-muestreo por largo de arco a paso fijo (incluye ambos extremos).</summary>
        public static List<vec2> Remuestrear(List<vec2> linea, double paso)
        {
            var outp = new List<vec2>();
            if (linea.Count == 0) return outp;
            outp.Add(linea[0]);
            double resto = paso;   // cuánto falta para el próximo punto
            for (int i = 1; i < linea.Count; i++)
            {
                var a = linea[i - 1];
                var b = linea[i];
                double dx = b.easting - a.easting, dy = b.northing - a.northing;
                double L = Math.Sqrt(dx * dx + dy * dy);
                double s = 0;
                while (L - s >= resto)
                {
                    s += resto;
                    outp.Add(new vec2(a.easting + dx * s / L, a.northing + dy * s / L));
                    resto = paso;
                }
                resto -= L - s;
            }
            var ultimo = linea[linea.Count - 1];
            var ult = outp[outp.Count - 1];
            if (Dist2(ultimo.easting - ult.easting, ultimo.northing - ult.northing) > 1e-6) outp.Add(ultimo);
            return outp;
        }

        /// <summary>Media móvil de ±ventana puntos. Abierta: extremos fijos y
        /// ventana que se achica cerca de las puntas. Cerrada: circular.</summary>
        public static List<vec2> Suavizar(List<vec2> pts, int ventana, bool cerrada)
        {
            int n = pts.Count;
            if (ventana <= 0 || n < 3) return new List<vec2>(pts);
            var outp = new List<vec2>(n);
            for (int i = 0; i < n; i++)
            {
                if (!cerrada && (i == 0 || i == n - 1)) { outp.Add(pts[i]); continue; }
                int w = cerrada ? ventana : Math.Min(ventana, Math.Min(i, n - 1 - i));
                double se = 0, sn = 0;
                int c = 0;
                for (int d = -w; d <= w; d++)
                {
                    int j = i + d;
                    if (cerrada) j = ((j % n) + n) % n;
                    se += pts[j].easting;
                    sn += pts[j].northing;
                    c++;
                }
                outp.Add(new vec2(se / c, sn / c));
            }
            return outp;
        }

        private static double Dist2(double dx, double dy) => dx * dx + dy * dy;
    }
}
