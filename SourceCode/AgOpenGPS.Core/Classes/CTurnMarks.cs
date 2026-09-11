// ============================================================================
// CTurnMarks.cs — geometría PURA de "Marcar giro": dos líneas perpendiculares
// a la guía marcadas por el operario definen dónde gira el U-turn sin lindero.
// Ver docs/superpowers/specs/2026-08-10-marcar-giro-design.md.
// Sin estado ni host: solo cuentas, para poder testearlas.
// ============================================================================
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public class TurnMark
    {
        public double easting, northing;
        // Rumbo de la GUÍA al momento de marcar (rad). La línea de la marca es
        // PERPENDICULAR a este rumbo.
        public double heading;
    }

    public static class CTurnMarks
    {
        // Rectángulo cerrado: lados "marca" por A y B (perpendiculares al rumbo
        // de cada una), laterales a ±halfWidthM del eje de la guía. El winding
        // lo normaliza después CalculateFenceArea del CBoundaryList.
        public static List<vec3> BuildVirtualFence(TurnMark a, TurnMark b, double halfWidthM = 500.0)
        {
            var ring = new List<vec3>();
            if (a == null || b == null) return ring;

            // Eje de avance = promedio de headings (misma guía; difieren poco).
            double hdg = Math.Atan2(
                Math.Sin(a.heading) + Math.Sin(b.heading),
                Math.Cos(a.heading) + Math.Cos(b.heading));
            // heading AOG: 0 = norte, crece horario. Avance y perpendicular:
            double fx = Math.Sin(hdg), fy = Math.Cos(hdg);     // adelante (E,N)
            double px = Math.Cos(hdg), py = -Math.Sin(hdg);    // perpendicular

            // Esquinas: (A - perp*w), (A + perp*w), (B + perp*w), (B - perp*w)
            ring.Add(new vec3(a.easting - px * halfWidthM, a.northing - py * halfWidthM, 0));
            ring.Add(new vec3(a.easting + px * halfWidthM, a.northing + py * halfWidthM, 0));
            ring.Add(new vec3(b.easting + px * halfWidthM, b.northing + py * halfWidthM, 0));
            ring.Add(new vec3(b.easting - px * halfWidthM, b.northing - py * halfWidthM, 0));
            ring.Add(ring[0]); // cerrado
            return ring;
        }

        // Sutherland-Hodgman contra UNA recta (la línea de la marca). Se queda
        // con el semiplano donde cae keepSidePoint (el interior del lote): la
        // marca solo ACERCA la cabecera, nunca agranda el polígono.
        public static List<vec3> ClipRingWithHalfPlane(
            List<vec3> ring, vec3 pointOnLine, double lineHeading, vec2 keepSidePoint)
        {
            var outRing = new List<vec3>();
            if (ring == null || ring.Count < 3) return outRing;

            // Normal de la recta (perpendicular a su dirección).
            double dx = Math.Sin(lineHeading), dy = Math.Cos(lineHeading);
            double nx = dy, ny = -dx;
            double Side(double e, double n) =>
                (e - pointOnLine.easting) * nx + (n - pointOnLine.northing) * ny;
            double keep = Math.Sign(Side(keepSidePoint.easting, keepSidePoint.northing));
            if (keep == 0) keep = 1;

            // Trabajar sin el punto de cierre duplicado.
            int count = ring.Count;
            bool cerrado = ring[0].easting == ring[count - 1].easting &&
                           ring[0].northing == ring[count - 1].northing;
            int n2 = cerrado ? count - 1 : count;

            for (int i = 0; i < n2; i++)
            {
                vec3 cur = ring[i];
                vec3 nxt = ring[(i + 1) % n2];
                double sc = Side(cur.easting, cur.northing) * keep;
                double sn = Side(nxt.easting, nxt.northing) * keep;

                if (sc >= 0) outRing.Add(cur);
                if ((sc >= 0) != (sn >= 0))
                {
                    // Intersección segmento-recta.
                    double t = sc / (sc - sn);
                    outRing.Add(new vec3(
                        cur.easting + t * (nxt.easting - cur.easting),
                        cur.northing + t * (nxt.northing - cur.northing), 0));
                }
            }
            if (outRing.Count < 3) { outRing.Clear(); return outRing; }
            outRing.Add(outRing[0]); // re-cerrar
            return outRing;
        }
    }
}
