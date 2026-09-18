// ============================================================================
// CoverageIndex.cs — índice espacial del área ya trabajada + consulta por sección.
//
// Fase 2 del anti-overlap geométrico. CoverageGeometry (Fase 1) sabe resolver
// UN triángulo contra UNA sección; acá está el resto del problema, que es de
// escala: una jornada de 8 h deja ~100k vértices, y la decisión de sección se
// toma para ~8-16 secciones, 10 veces por segundo. Recorrer todos los
// triángulos en cada consulta sería del orden de 10 millones de tests por
// segundo — no entra en la CPU de la cabina.
//
// Solución: grilla uniforme. Cada triángulo se registra en TODAS las celdas que
// toca su bounding box; cada consulta mira solo las celdas por las que pasa el
// segmento de la sección. Como la inserción cubre el bbox entero del triángulo
// y la consulta cubre el bbox entero del segmento, si se cruzan de verdad
// entonces comparten al menos una celda: la poda no puede perder un solape.
//
// Decisiones de memoria (importan con 100k vértices):
//   · los triángulos se guardan en un array plano de doubles (6 por triángulo),
//     no como objetos: cero presión de GC al construir el índice.
//   · el dedupe de candidatos usa un array de "generación" en vez de un
//     HashSet, así una consulta no aloca nada.
//
// Sigue siendo geometría pura: no conoce OpenGL, ni cámara, ni zoom. NO está
// enganchado a la decisión de sección todavía (eso es Fase 3, detrás de flag).
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Coverage
{
    /// <summary>
    /// Guarda el área trabajada como triángulos indexados por celda, y responde
    /// cuánto de una sección cae sobre ella.
    /// </summary>
    public sealed class CoverageIndex
    {
        // Triángulos aplanados: [ax,ay,bx,by,cx,cy] por triángulo.
        private double[] _tri = new double[6 * 256];
        private int _count;

        // Bounding box por triángulo, para descartar antes de transformar.
        private double[] _bbox = new double[4 * 256];   // [minE,minN,maxE,maxN]

        private readonly double _celda;
        private readonly Dictionary<long, List<int>> _grilla = new Dictionary<long, List<int>>();

        // Dedupe sin alocar: _visto[i] == _generacion  =>  el triángulo i ya se
        // consideró en ESTA consulta.
        private int[] _visto = new int[256];
        private int _generacion;

        // Buffer de tramos reutilizado entre consultas.
        private readonly List<XInterval> _tramos = new List<XInterval>(64);

        /// <summary>
        /// <paramref name="tamCeldaM"/>: lado de la celda. Conviene del orden del
        /// ancho de la herramienta — muy chica infla el índice (un triángulo cae
        /// en muchas celdas), muy grande devuelve demasiados candidatos.
        /// </summary>
        public CoverageIndex(double tamCeldaM = 4.0)
        {
            if (tamCeldaM <= 0.01) tamCeldaM = 4.0;
            _celda = tamCeldaM;
        }

        /// <summary>Triángulos indexados.</summary>
        public int CantidadTriangulos { get { return _count; } }

        /// <summary>Celdas ocupadas. Sirve para medir si el tamaño de celda es sano.</summary>
        public int CantidadCeldas { get { return _grilla.Count; } }

        /// <summary>Vacía el índice (cambio de lote, reset de cobertura).</summary>
        public void Limpiar()
        {
            _count = 0;
            _grilla.Clear();
            _generacion = 0;
            Array.Clear(_visto, 0, _visto.Length);
        }

        /// <summary>
        /// Agrega una tira de triángulos (GL_TRIANGLE_STRIP): los vértices
        /// v0,v1,v2,v3… forman (v0,v1,v2), (v1,v2,v3), (v2,v3,v4)…
        ///
        /// Los vértices tienen que venir SIN el header de color que PilotX mete en
        /// patchList[k][0] — el adaptador ya lo saltea.
        /// </summary>
        public void AgregarTira(IList<double> eastings, IList<double> northings)
        {
            if (eastings == null || northings == null) return;
            int n = Math.Min(eastings.Count, northings.Count);
            for (int i = 0; i + 2 < n; i++)
            {
                AgregarTriangulo(
                    eastings[i],     northings[i],
                    eastings[i + 1], northings[i + 1],
                    eastings[i + 2], northings[i + 2]);
            }
        }

        /// <summary>Agrega un triángulo suelto.</summary>
        public void AgregarTriangulo(
            double ae, double an, double be, double bn, double ce, double cn)
        {
            // Descartar degenerados: no aportan área y solo ensucian el índice.
            double area2 = Math.Abs((be - ae) * (cn - an) - (ce - ae) * (bn - an));
            if (area2 < 1e-9) return;

            if (_count * 6 + 6 > _tri.Length) Crecer();

            int t = _count;
            int o = t * 6;
            _tri[o] = ae; _tri[o + 1] = an;
            _tri[o + 2] = be; _tri[o + 3] = bn;
            _tri[o + 4] = ce; _tri[o + 5] = cn;

            double minE = Math.Min(ae, Math.Min(be, ce));
            double maxE = Math.Max(ae, Math.Max(be, ce));
            double minN = Math.Min(an, Math.Min(bn, cn));
            double maxN = Math.Max(an, Math.Max(bn, cn));

            // GUARDIA ANTI-VENENO: un triángulo de siembra real mide metros.
            // Un bbox de cientos de metros es un vértice basura (header de
            // color, fix sin origen, salto de posición) y registrarlo en la
            // grilla crea MILLONES de celdas (una List<int> por celda): en el
            // equipo real esto infló el proceso a >4 GB con 24,5 millones de
            // listas y dejó el hilo del fix clavado en este for. Se descarta.
            if (maxE - minE > 100.0 || maxN - minN > 100.0) return;

            int b = t * 4;
            _bbox[b] = minE; _bbox[b + 1] = minN; _bbox[b + 2] = maxE; _bbox[b + 3] = maxN;

            _count++;

            // Registrar en todas las celdas que toca el bbox.
            int cx0 = Celda(minE), cx1 = Celda(maxE);
            int cy0 = Celda(minN), cy1 = Celda(maxN);
            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    long k = Clave(cx, cy);
                    List<int> lista;
                    if (!_grilla.TryGetValue(k, out lista))
                    {
                        lista = new List<int>(8);
                        _grilla[k] = lista;
                    }
                    lista.Add(t);
                }
            }
        }

        /// <summary>
        /// Cuánto de la sección cae sobre área ya trabajada.
        ///
        /// <paramref name="heading"/> en radianes, convención de PilotX (0 = Norte,
        /// horario). <paramref name="umbralY"/> es el look-ahead: 0 = la sección
        /// donde está ahora, positivo = metros adelante. Consultar varias
        /// distancias es barato: cambia solo este parámetro.
        /// </summary>
        public CoverageResult Consultar(
            double centroE, double centroN, double heading,
            double medioAncho, double umbralY = 0)
        {
            _tramos.Clear();
            if (_count == 0 || medioAncho <= 0)
                return CoverageGeometry.Resultado(_tramos, medioAncho);

            double cos = Math.Cos(heading), sin = Math.Sin(heading);

            // Extremos del segmento consultado, en coordenadas del mundo.
            // Inversa del transform: dE = x·cos + y·sin ; dN = -x·sin + y·cos
            double e1 = centroE + (-medioAncho) * cos + umbralY * sin;
            double n1 = centroN - (-medioAncho) * sin + umbralY * cos;
            double e2 = centroE + (medioAncho) * cos + umbralY * sin;
            double n2 = centroN - (medioAncho) * sin + umbralY * cos;

            double qMinE = Math.Min(e1, e2), qMaxE = Math.Max(e1, e2);
            double qMinN = Math.Min(n1, n2), qMaxN = Math.Max(n1, n2);

            // Nueva generación para el dedupe (sin alocar).
            _generacion++;
            if (_visto.Length < _count)
            {
                _visto = new int[Math.Max(_count, _visto.Length * 2)];
                _generacion = 1;
            }

            int cx0 = Celda(qMinE), cx1 = Celda(qMaxE);
            int cy0 = Celda(qMinN), cy1 = Celda(qMaxN);

            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    List<int> lista;
                    if (!_grilla.TryGetValue(Clave(cx, cy), out lista)) continue;

                    for (int i = 0; i < lista.Count; i++)
                    {
                        int t = lista[i];
                        if (_visto[t] == _generacion) continue;   // ya evaluado
                        _visto[t] = _generacion;

                        // Rechazo por bbox antes de gastar el transform.
                        int b = t * 4;
                        if (_bbox[b] > qMaxE || _bbox[b + 2] < qMinE) continue;
                        if (_bbox[b + 1] > qMaxN || _bbox[b + 3] < qMinN) continue;

                        int o = t * 6;
                        double ax, ay, bx, by, ccx, ccy;
                        CoverageGeometry.ATransformar(_tri[o], _tri[o + 1], centroE, centroN, cos, sin, out ax, out ay);
                        CoverageGeometry.ATransformar(_tri[o + 2], _tri[o + 3], centroE, centroN, cos, sin, out bx, out by);
                        CoverageGeometry.ATransformar(_tri[o + 4], _tri[o + 5], centroE, centroN, cos, sin, out ccx, out ccy);

                        XInterval iv;
                        if (CoverageGeometry.TrianguloEnIntervalo(
                                ax, ay, bx, by, ccx, ccy, umbralY, medioAncho, out iv))
                            _tramos.Add(iv);
                    }
                }
            }

            return CoverageGeometry.Resultado(_tramos, medioAncho);
        }

        // ---- internos ------------------------------------------------------

        private int Celda(double v)
        {
            return (int)Math.Floor(v / _celda);
        }

        private static long Clave(int cx, int cy)
        {
            return ((long)cx << 32) ^ (uint)cy;
        }

        private void Crecer()
        {
            int nuevo = Math.Max(_tri.Length * 2, 6 * 256);
            var t = new double[nuevo];
            Array.Copy(_tri, t, _count * 6);
            _tri = t;

            var b = new double[nuevo / 6 * 4];
            Array.Copy(_bbox, b, _count * 4);
            _bbox = b;
        }
    }
}
