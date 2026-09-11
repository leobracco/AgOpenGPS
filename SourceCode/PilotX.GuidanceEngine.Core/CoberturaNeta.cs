// CoberturaNeta.cs — area NETA de cobertura (suelo realmente pintado, sin
// contar repintado) para el motor headless.
//
// En el AgOpenGPS original `Fd.actualAreaCovered` salia de contar los pixeles
// pintados en el back-buffer de OpenGL (FormGPS.OpenGL.cs). El motor headless
// no renderiza, y la variable quedo declarada pero nunca asignada: la pantalla
// mostraba Neta 0,00 ha, Repintado = Trabajado (100 %) y el "ha" de la barra
// superior en 0,0 (caso Fran, lote "enriqueta bloquer", 2026-09-06).
//
// Esta clase reemplaza el conteo de pixeles por una grilla de bits en metros:
// cada cuadrilatero que pinta la barra marca sus celdas; una celda ya marcada
// no se vuelve a contar. Neta = celdas marcadas x area de celda.
//
// Memoria: celdas de 0,5 m en bloques de 64x64 bits (512 bytes por bloque de
// 32 m x 32 m). 100 ha ~ 1000 bloques ~ 0,5 MB. Un lote de 1000 ha, 5 MB.
// Costo por fix: un cuadrilatero de 31 m x 1 m son ~250 celdas, nada.
//
// Precision: con celdas de 0,5 m el error de borde es < 1 % para una barra
// de 10 m o mas. El pixel de OpenGL del AOG original era peor que eso.
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public sealed class CoberturaNeta
    {
        private const int Lado = 64;                 // celdas por lado de bloque
        private readonly double _res;                // metros por celda
        private readonly double _areaCelda;
        private readonly Dictionary<long, ulong[]> _bloques = new Dictionary<long, ulong[]>();
        private long _celdas;

        public CoberturaNeta(double resolucionMetros = 0.5)
        {
            _res = resolucionMetros;
            _areaCelda = _res * _res;
        }

        /// <summary>Area neta en m2.</summary>
        public double AreaM2 => _celdas * _areaCelda;

        public int Bloques => _bloques.Count;

        public void Reset()
        {
            _bloques.Clear();
            _celdas = 0;
        }

        /// <summary>Cuadrilatero izq/der anterior + izq/der actual (dos triangulos).</summary>
        public void MarcarQuad(vec3 izqAnt, vec3 derAnt, vec3 izq, vec3 der)
        {
            MarcarTriangulo(izqAnt.easting, izqAnt.northing, derAnt.easting, derAnt.northing, izq.easting, izq.northing);
            MarcarTriangulo(derAnt.easting, derAnt.northing, izq.easting, izq.northing, der.easting, der.northing);
        }

        public void MarcarTriangulo(vec3 a, vec3 b, vec3 c)
            => MarcarTriangulo(a.easting, a.northing, b.easting, b.northing, c.easting, c.northing);

        public void MarcarTriangulo(double ax, double ay, double bx, double by, double cx, double cy)
        {
            double minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx));
            double minY = Math.Min(ay, Math.Min(by, cy)), maxY = Math.Max(ay, Math.Max(by, cy));

            // Un triangulo de mas de 400 m de lado es un salto de GPS (fix
            // perdido, simulador reiniciado), no cobertura. Mismo criterio que
            // TramosSanos() en Cobertura.cs.
            if (maxX - minX > 400 || maxY - minY > 400) return;
            if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsInfinity(maxX) || double.IsInfinity(maxY)) return;

            int gx0 = (int)Math.Floor(minX / _res), gx1 = (int)Math.Floor(maxX / _res);
            int gy0 = (int)Math.Floor(minY / _res), gy1 = (int)Math.Floor(maxY / _res);

            for (int gy = gy0; gy <= gy1; gy++)
            {
                double py = (gy + 0.5) * _res;
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    double px = (gx + 0.5) * _res;
                    double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
                    double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
                    double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
                    bool neg = d1 < 0 || d2 < 0 || d3 < 0;
                    bool pos = d1 > 0 || d2 > 0 || d3 > 0;
                    if (neg && pos) continue;            // fuera del triangulo
                    Marcar(gx, gy);
                }
            }
        }

        private void Marcar(int gx, int gy)
        {
            // Bloque = division entera hacia abajo (tambien con negativos).
            int bx = gx >> 6, by = gy >> 6;              // gx / 64 con floor
            int lx = gx & (Lado - 1), ly = gy & (Lado - 1);
            long clave = ((long)bx << 32) ^ (uint)by;

            if (!_bloques.TryGetValue(clave, out var bits))
            {
                bits = new ulong[Lado];
                _bloques[clave] = bits;
            }
            ulong mascara = 1UL << lx;
            if ((bits[ly] & mascara) != 0) return;       // ya pintada
            bits[ly] |= mascara;
            _celdas++;
        }
    }
}
