// ============================================================================
// Planimetria.cs — mapa de alturas de un lote a partir de los puntos que
// registra PilotX con RTK fijo (planimetría fase 3, en la cabina).
//
// Es un PORT FIEL de lib/planimetria.js de OrbitX (fase 2): mismos pasos,
// mismos parámetros por defecto y mismo orden de cálculo, para que la grilla
// que ve el operario en la cabina sea la misma que ve el dueño en el panel
// web (los tests de PlanimetriaTests comparan contra la salida de Node con
// el mismo lote sintético).
//
// Por qué se calcula acá y no se baja de OrbitX: el endpoint
// GET /api/aog/lotes/:nombre/planimetria es SOLO JWT (panel web); el
// middleware de /api/aog en server.js deja pasar sin JWT únicamente
// POST /sync y GET /pendientes-descarga, y slugPlanimetria() saca la org del
// usuario del token. Con X-Device-ID/X-Auth-Token responde 401. Además en el
// campo no siempre hay internet. Así que la cabina calcula sola con el
// Elevation.txt local del lote.
//
// Entrada: el texto de Elevation.txt (cabecera de AOG + filas
//   Latitude,Longitude,Elevation,Quality,Easting,Northing,Heading,Roll).
// Salida: grilla regular (DEM), curvas de nivel, pendiente por celda, bajos
// (depresiones donde se junta agua) y estadísticas.
//
// Clase PURA: sin disco, sin lote, sin settings, sin UI. C# 7.3 (net48 y
// netstandard2.0). Todo en arrays planos.
//
// Pasos:
//   1. parsear + filtrar Quality == 4 (solo RTK fijo) y filas con 8 campos
//   2. proyectar lat/lon a un plano local (metros) alrededor de la mediana
//   3. cortar el recorrido en "pasadas" (huecos y cambios de rumbo)
//   4. nivelar: un offset por pasada para que coincida con sus vecinas
//      (mínimos cuadrados amortiguados, gradiente conjugado)
//   5. grillar con un plano local ponderado; celdas lejos de todo dato = NaN
//   6. curvas de nivel (marching squares), pendiente y bajos (priority-flood)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace AgOpenGPS
{
    /// <summary>Conteo del parseo de Elevation.txt (mismo significado que en Node).</summary>
    public sealed class CuentaElevacion
    {
        public int Partes;
        public int Filas;
        public int Q4;
        public int NoRtk;
        public int Invalidas;
    }

    /// <summary>Puntos RTK fijo leídos de uno o más Elevation.txt.</summary>
    public sealed class PuntosElevacion
    {
        public double[] Lat = new double[0];
        public double[] Lon = new double[0];
        public double[] Z = new double[0];
        public int N;
        public CuentaElevacion Cuenta = new CuentaElevacion();
    }

    /// <summary>Junta puntos parte por parte sin retener el texto.</summary>
    public sealed class AcumuladorElevacion
    {
        private double[] _lat = new double[8192], _lon = new double[8192], _z = new double[8192];
        private int _n;
        private readonly CuentaElevacion _cuenta = new CuentaElevacion();

        public void Agregar(string texto)
        {
            _cuenta.Partes++;
            Planimetria.ParsearTexto(texto, this, _cuenta);
        }

        /// <summary>Lee un Elevation.txt línea por línea, sin cargar el texto
        /// entero en memoria (un lote grande tiene cientos de miles de filas).</summary>
        public void AgregarLineas(System.IO.TextReader lector)
        {
            _cuenta.Partes++;
            string linea;
            while ((linea = lector.ReadLine()) != null) Planimetria.ParsearLinea(linea, this, _cuenta);
        }

        internal void Push(double lat, double lon, double z)
        {
            if (_n == _z.Length)
            {
                Array.Resize(ref _lat, _n * 2);
                Array.Resize(ref _lon, _n * 2);
                Array.Resize(ref _z, _n * 2);
            }
            _lat[_n] = lat; _lon[_n] = lon; _z[_n] = z; _n++;
        }

        public PuntosElevacion Resultado()
        {
            var p = new PuntosElevacion { N = _n, Cuenta = _cuenta };
            p.Lat = new double[_n]; p.Lon = new double[_n]; p.Z = new double[_n];
            Array.Copy(_lat, p.Lat, _n);
            Array.Copy(_lon, p.Lon, _n);
            Array.Copy(_z, p.Z, _n);
            return p;
        }
    }

    /// <summary>
    /// Proyección local WGS84 equirectangular con radios locales (igual que
    /// crearProyeccion de Node): x = (lon−lon0)·N·cos(lat0), y = (lat−lat0)·M.
    /// Lineal en lat/lon: la grilla es un rectángulo lat/lon exacto.
    /// </summary>
    public sealed class ProyeccionLocal
    {
        public double Lat0 { get; }
        public double Lon0 { get; }
        private readonly double _kx, _ky;

        public ProyeccionLocal(double lat0, double lon0)
        {
            const double a = 6378137, e2 = 0.00669437999014, rad = Math.PI / 180;
            double s = Math.Sin(lat0 * rad), w = 1 - e2 * s * s;
            double m = a * (1 - e2) / Math.Pow(w, 1.5);
            double nn = a / Math.Sqrt(w);
            Lat0 = lat0; Lon0 = lon0;
            _kx = nn * Math.Cos(lat0 * rad) * rad;
            _ky = m * rad;
        }

        public double X(double lon) => (lon - Lon0) * _kx;
        public double Y(double lat) => (lat - Lat0) * _ky;

        public void ALatLon(double x, double y, out double lat, out double lon)
        {
            lat = Lat0 + y / _ky;
            lon = Lon0 + x / _kx;
        }
    }

    /// <summary>Grilla de alturas. Fila 0 = NORTE, row-major; NaN = sin dato.
    /// Centro de la celda (c, r) = (X0 + c·Res, YTop − r·Res) en la proyección.</summary>
    public sealed class GrillaAlturas
    {
        public float[] Z;
        public int Nx, Ny;
        public double Res, X0, YTop;
        public double RadioM, DCercaM;
        public int Cubiertas;
    }

    public sealed class ZonaBajo
    {
        public int Celdas;
        public double AreaM2, ProfMaxM, VolumenM3, Fila, Col, ZMin;
    }

    public sealed class BajosResultado
    {
        /// <summary>Profundidad del bajo por celda (m); 0 = no es bajo.</summary>
        public float[] Prof;
        public List<ZonaBajo> Zonas = new List<ZonaBajo>();
    }

    /// <summary>Punto de una curva en coordenadas de grilla (columna, fila) fraccionarias.</summary>
    public struct PuntoGrilla
    {
        public double C, F;
        public PuntoGrilla(double c, double f) { C = c; F = f; }
    }

    public sealed class NivelCurva
    {
        public double Elev;
        public List<List<PuntoGrilla>> Lineas = new List<List<PuntoGrilla>>();
    }

    public sealed class CurvasResultado
    {
        public List<NivelCurva> Niveles = new List<NivelCurva>();
        public double Intervalo;
    }

    public sealed class NivelacionResultado
    {
        public double[] Offsets;
        public bool Aplicada;
        public string Motivo;
        public double RadioM;
        public int Observaciones, Iteraciones, PasadasNiveladas;
        public double SesgoRmsAntesM = double.NaN, SesgoRmsDespuesM = double.NaN;
    }

    public sealed class OpcionesPlanimetria
    {
        /// <summary>Resolución pedida de la grilla (m).</summary>
        public double Res = 3;
        /// <summary>true: el intervalo más fino que deje ≤ 15 curvas.</summary>
        public bool IntervaloAuto;
        /// <summary>Intervalo entre curvas (m) si no es automático.</summary>
        public double Intervalo = 0.1;
        /// <summary>Profundidad mínima de un bajo (m).</summary>
        public double UmbralBajo = 0.05;
        /// <summary>Superficie mínima de un bajo (m²). NaN = max(50, 4·res²).</summary>
        public double AreaMinBajoM2 = double.NaN;
        public bool Nivelar = true;
        public int MaxPuntos = Planimetria.MaxPuntos;
        public int MaxCeldas = Planimetria.MaxCeldas;
        /// <summary>Radio del plano ponderado de la grilla (m). NaN = automático.</summary>
        public double RadioGrilla = double.NaN;
        /// <summary>Límite del lote opcional, [lat, lon] por vértice (para el % cubierto).</summary>
        public IList<double[]> Limite;
    }

    public sealed class EstadisticasPlanimetria
    {
        public int PuntosFilas, PuntosRtk, PuntosDescartadosNoRtk, PuntosInvalidos, PuntosUsados;
        public int Raleo, Partes, Pasadas;
        public double? EspaciadoPasadasM;
        public double? ZMinM, ZMaxM, DesnivelM, ZMediaM;
        public double? AreaCubiertaHa, LimiteHa, CoberturaPct;
        public double? PendienteMediaPct, PendienteP90Pct, PendienteMaxPct;
        public int BajosCantidad;
        public double? BajosAreaHa, BajosVolumenM3;
        public bool NivelacionAplicada;
        public string NivelacionMotivo;
        public int PasadasNiveladas;
        public double? SesgoRmsAntesCm, SesgoRmsDespuesCm, OffsetMaxCm;
        public long Ms;
    }

    public sealed class ResultadoPlanimetria
    {
        public bool Ok;
        public string Motivo;
        public double Res, ResPedida, Intervalo, UmbralBajo;
        public ProyeccionLocal Proyeccion;
        public GrillaAlturas Grilla;
        public float[] Pendiente;
        public BajosResultado Bajos;
        public CurvasResultado Curvas;
        public EstadisticasPlanimetria Stats = new EstadisticasPlanimetria();
        // Interno (tests): pasada de cada punto, offsets y z corregida.
        public int[] Seg;
        public double[] Offsets, X, Y, ZOriginal, ZCorregida;

        /// <summary>Centro de una posición de grilla (col, fila fraccionarias) → lat/lon.</summary>
        public void CeldaALatLon(double col, double fila, out double lat, out double lon)
        {
            Proyeccion.ALatLon(Grilla.X0 + col * Grilla.Res, Grilla.YTop - fila * Grilla.Res, out lat, out lon);
        }
    }

    public static class Planimetria
    {
        public const int Version = 1;
        public const int MaxPuntos = 600000;
        public const int MaxCeldas = 1500000;
        private static readonly double[] IntervalosAuto = { 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 5 };

        // ════════════════════════════════════════════════════════════════
        //  1. Parseo
        // ════════════════════════════════════════════════════════════════

        /// <summary>Junta los textos de Elevation.txt (en orden) y devuelve los puntos RTK fijo.</summary>
        public static PuntosElevacion JuntarPuntos(IEnumerable<string> textos)
        {
            var acc = new AcumuladorElevacion();
            foreach (string t in textos) acc.Agregar(t);
            return acc.Resultado();
        }

        /// <summary>
        /// Agrega las filas válidas de un Elevation.txt. Una fila vale si tiene
        /// EXACTAMENTE 8 campos (AOG viejo escribía con separador de miles y la
        /// fila se parte en más), Quality == 4 y lat/lon/altura razonables.
        /// </summary>
        internal static void ParsearTexto(string texto, AcumuladorElevacion acc, CuentaElevacion cuenta)
        {
            if (string.IsNullOrEmpty(texto)) return;
            int ini = 0, L = texto.Length;
            while (ini < L)
            {
                int fin = texto.IndexOf('\n', ini);
                if (fin < 0) fin = L;
                int largo = fin - ini;
                int inicioLinea = ini;
                ini = fin + 1;
                if (largo <= 0) continue;
                char c0 = texto[inicioLinea];
                if (!(c0 == '-' || (c0 >= '0' && c0 <= '9'))) continue;
                ParsearLinea(texto.Substring(inicioLinea, largo), acc, cuenta);
            }
        }

        /// <summary>Una línea de Elevation.txt (sin el salto de línea). Las que no empiezan
        /// con dígito o signo (cabecera) se ignoran sin contar.</summary>
        internal static void ParsearLinea(string linea, AcumuladorElevacion acc, CuentaElevacion cuenta)
        {
            if (string.IsNullOrEmpty(linea)) return;
            char c0 = linea[0];
            if (!(c0 == '-' || (c0 >= '0' && c0 <= '9'))) return;
            string[] f = linea.Split(',');
            if (f.Length < 8) return;          // cabecera ("StartFix" lat,lon) u otra cosa
            cuenta.Filas++;
            if (f.Length != 8) { cuenta.Invalidas++; return; }
            double q = Numero(f[3]);
            if (q != 4) { cuenta.NoRtk++; return; }
            double lat = Numero(f[0]), lon = Numero(f[1]), z = Numero(f[2]);
            if (double.IsNaN(lat) || double.IsInfinity(lat) || double.IsNaN(lon) || double.IsInfinity(lon)
                || double.IsNaN(z) || double.IsInfinity(z)
                || Math.Abs(lat) > 90 || Math.Abs(lon) > 180
                || (Math.Abs(lat) < 0.01 && Math.Abs(lon) < 0.01) || z < -500 || z > 9000)
            {
                cuenta.Invalidas++;
                return;
            }
            acc.Push(lat, lon, z);
            cuenta.Q4++;
        }

        // Number() de JS para lo que nos importa: vacío = 0, basura = NaN.
        private static double Numero(string s)
        {
            string t = s.Trim();
            if (t.Length == 0) return 0;
            double v;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        // ════════════════════════════════════════════════════════════════
        //  2. Proyección
        // ════════════════════════════════════════════════════════════════

        internal static double Mediana(IList<double> arr)
        {
            if (arr.Count == 0) return double.NaN;
            var s = new double[arr.Count];
            arr.CopyTo(s, 0);
            Array.Sort(s);
            int m = s.Length >> 1;
            return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
        }

        public sealed class Proyectados
        {
            public ProyeccionLocal Pr;
            public double[] X, Y, Z;
            public int N, Raleo, Lejos, Repetidos;
        }

        /// <summary>Proyecta, descarta puntos a &gt; 20 km de la mediana y ralea si hay demasiados.</summary>
        public static Proyectados Proyectar(PuntosElevacion p, int maxPuntos)
        {
            int n0 = p.N;
            int paso0 = Math.Max(1, n0 / 4000);
            var latS = new List<double>();
            var lonS = new List<double>();
            for (int i = 0; i < n0; i += paso0) { latS.Add(p.Lat[i]); lonS.Add(p.Lon[i]); }
            var pr = new ProyeccionLocal(Mediana(latS), Mediana(lonS));
            int k = Math.Max(1, (int)Math.Ceiling(n0 / (double)maxPuntos));
            int cap = (int)Math.Ceiling(n0 / (double)k);
            var x = new double[cap];
            var y = new double[cap];
            var z = new double[cap];
            int n = 0, lejos = 0, repetidos = 0;
            for (int i = 0; i < n0; i += k)
            {
                double px = pr.X(p.Lon[i]), py = pr.Y(p.Lat[i]);
                if (px * px + py * py > 4e8) { lejos++; continue; }     // > 20 km
                // Tractor parado: puntos repetidos pesarían de más en la nivelación.
                if (n > 0 && Math.Abs(px - x[n - 1]) < 0.15 && Math.Abs(py - y[n - 1]) < 0.15) { repetidos++; continue; }
                x[n] = px; y[n] = py; z[n] = p.Z[i]; n++;
            }
            Array.Resize(ref x, n);
            Array.Resize(ref y, n);
            Array.Resize(ref z, n);
            return new Proyectados { Pr = pr, X = x, Y = y, Z = z, N = n, Raleo = k, Lejos = lejos, Repetidos = repetidos };
        }

        // ════════════════════════════════════════════════════════════════
        //  Hash espacial (cubetas fijas, orden por conteo)
        // ════════════════════════════════════════════════════════════════

        internal interface IVisitante
        {
            void Visitar(int j, double d2, double dx, double dy);
        }

        internal sealed class HashEspacial
        {
            public double Celda, MinX, MinY;
            public int Nx, Ny;
            public int[] Start, Items;
            private readonly double[] _x, _y;

            public HashEspacial(double[] x, double[] y, int n, double celda)
            {
                _x = x; _y = y; Celda = celda;
                double minx = double.PositiveInfinity, miny = double.PositiveInfinity;
                double maxx = double.NegativeInfinity, maxy = double.NegativeInfinity;
                for (int i = 0; i < n; i++)
                {
                    if (x[i] < minx) minx = x[i];
                    if (x[i] > maxx) maxx = x[i];
                    if (y[i] < miny) miny = y[i];
                    if (y[i] > maxy) maxy = y[i];
                }
                if (n == 0) { minx = miny = 0; maxx = maxy = 0; }
                MinX = minx; MinY = miny;
                Nx = Math.Max(1, (int)Math.Floor((maxx - minx) / celda) + 1);
                Ny = Math.Max(1, (int)Math.Floor((maxy - miny) / celda) + 1);
                Start = new int[Nx * Ny + 1];
                var cel = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int c = (int)Math.Floor((x[i] - minx) / celda) + (int)Math.Floor((y[i] - miny) / celda) * Nx;
                    cel[i] = c;
                    Start[c + 1]++;
                }
                for (int c = 0; c < Nx * Ny; c++) Start[c + 1] += Start[c];
                Items = new int[n];
                var pos = new int[Nx * Ny];
                Array.Copy(Start, pos, Nx * Ny);
                for (int i = 0; i < n; i++) Items[pos[cel[i]]++] = i;
            }

            /// <summary>Visita cada punto a distancia ≤ r de (px, py), en el mismo orden que Node.</summary>
            public void Vecinos<T>(double px, double py, double r, ref T v) where T : struct, IVisitante
            {
                double r2 = r * r;
                int c0 = Math.Max(0, (int)Math.Floor((px - r - MinX) / Celda));
                int c1 = Math.Min(Nx - 1, (int)Math.Floor((px + r - MinX) / Celda));
                int f0 = Math.Max(0, (int)Math.Floor((py - r - MinY) / Celda));
                int f1 = Math.Min(Ny - 1, (int)Math.Floor((py + r - MinY) / Celda));
                for (int f = f0; f <= f1; f++)
                {
                    for (int c = c0; c <= c1; c++)
                    {
                        int k = c + f * Nx;
                        for (int t = Start[k], e = Start[k + 1]; t < e; t++)
                        {
                            int j = Items[t];
                            double dx = _x[j] - px, dy = _y[j] - py, d2 = dx * dx + dy * dy;
                            if (d2 <= r2) v.Visitar(j, d2, dx, dy);
                        }
                    }
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  3. Pasadas
        // ════════════════════════════════════════════════════════════════

        private static double DifAng(double a, double b)
        {
            double d = a - b;
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d < -Math.PI) d += 2 * Math.PI;
            return d;
        }

        private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

        /// <summary>
        /// Corta el recorrido en pasadas: hueco &gt; 15 m, giro &gt; 50° respecto
        /// del rumbo de la pasada, o pasada más larga que 500 m. Los pedazos
        /// cortos (&lt; 30 m, giros de cabecera) se pegan a la anterior.
        /// </summary>
        public static int[] SegmentarPasadas(double[] x, double[] y, int n, out int nSeg)
        {
            const double gap = 15, paso = 3, minLargo = 30, maxLargo = 500;
            double giro = 50 * Math.PI / 180;
            var crudo = new int[n];
            var largos = new List<double> { 0 };
            var trasHueco = new List<bool> { true };
            int id = 0;
            double ax = n > 0 ? x[0] : 0, ay = n > 0 ? y[0] : 0, refR = double.NaN;
            for (int i = 1; i < n; i++)
            {
                double d = Hypot(x[i] - x[i - 1], y[i] - y[i - 1]);
                bool corta = false, porHueco = false;
                if (d > gap) { corta = true; porHueco = true; }
                else
                {
                    double da = Hypot(x[i] - ax, y[i] - ay);
                    if (da >= paso)
                    {
                        double b = Math.Atan2(y[i] - ay, x[i] - ax);
                        if (double.IsNaN(refR)) refR = b;
                        else
                        {
                            double df = DifAng(b, refR);
                            if (Math.Abs(df) > giro) corta = true;
                            else refR += 0.3 * df;
                        }
                        ax = x[i]; ay = y[i];
                    }
                    if (!corta && largos[id] + d > maxLargo) corta = true;
                }
                if (corta)
                {
                    id++;
                    largos.Add(0);
                    trasHueco.Add(porHueco);
                    refR = double.NaN;
                    ax = x[i]; ay = y[i];
                }
                else largos[id] += d;
                crudo[i] = id;
            }
            int nCrudo = id + 1;
            var destino = new int[nCrudo];
            for (int s = 0; s < nCrudo; s++)
            {
                destino[s] = s;
                if (s > 0 && largos[s] < minLargo && !trasHueco[s]) destino[s] = destino[s - 1];
            }
            for (int s = nCrudo - 2; s >= 0; s--)
            {
                if (destino[s] == s && largos[s] < minLargo && trasHueco[s] && s + 1 < nCrudo && !trasHueco[s + 1])
                    destino[s] = destino[s + 1];
            }
            var mapa = new Dictionary<int, int>();
            var seg = new int[n];
            for (int i = 0; i < n; i++)
            {
                int d = destino[crudo[i]];
                int v;
                if (!mapa.TryGetValue(d, out v)) { v = mapa.Count; mapa[d] = v; }
                seg[i] = v;
            }
            nSeg = mapa.Count;
            return seg;
        }

        private struct VisMinOtraPasada : IVisitante
        {
            public int[] Seg;
            public int S;
            public double Mejor;
            public void Visitar(int j, double d2, double dx, double dy)
            {
                if (Seg[j] != S && d2 < Mejor) Mejor = d2;
            }
        }

        /// <summary>Separación típica entre pasadas (mediana de la distancia a otra pasada). NaN si no hay.</summary>
        internal static double EstimarEspaciado(double[] x, double[] y, int n, int[] seg, HashEspacial hash)
        {
            int paso = Math.Max(1, n / 3000);
            var ds = new List<double>();
            const double R = 40;
            for (int i = 0; i < n; i += paso)
            {
                var v = new VisMinOtraPasada { Seg = seg, S = seg[i], Mejor = double.PositiveInfinity };
                hash.Vecinos(x[i], y[i], R, ref v);
                if (v.Mejor < double.PositiveInfinity) ds.Add(Math.Sqrt(v.Mejor));
            }
            if (ds.Count < 10) return double.NaN;
            double m = Mediana(ds);
            return m > 0.5 ? m : double.NaN;
        }

        // ════════════════════════════════════════════════════════════════
        //  4. Nivelación entre pasadas
        // ════════════════════════════════════════════════════════════════

        private struct VisNivel : IVisitante
        {
            public int[] Seg;
            public int P;
            public double R2;
            public List<int> Vj;
            public List<double> Vw, Vdx, Vdy;
            public HashSet<int> Segs;
            public double S0, Sx, Sy, Sxx, Sxy, Syy;
            public void Visitar(int j, double d2, double dx, double dy)
            {
                if (Seg[j] == P) return;
                double u = 1 - d2 / R2, w = u * u;
                if (w <= 0) return;
                Vj.Add(j); Vw.Add(w); Vdx.Add(dx); Vdy.Add(dy);
                S0 += w; Sx += w * dx; Sy += w * dy; Sxx += w * dx * dx; Sxy += w * dx * dy; Syy += w * dy * dy;
                Segs.Add(Seg[j]);
            }
        }

        internal static NivelacionResultado NivelarPasadas(double[] x, double[] y, double[] z, int n, int[] seg, int nSeg,
            HashEspacial hash, double espaciado)
        {
            if (double.IsNaN(espaciado) || nSeg < 3)
                return SinCambio(nSeg, double.IsNaN(espaciado) ? "una sola pasada" : "menos de 3 pasadas");

            double R = Math.Min(30, Math.Max(8, 2.1 * espaciado));
            const int maxEval = 40000;
            int k = Math.Max(1, (int)Math.Ceiling(n / (double)maxEval));
            double minEig = Math.Pow(0.2 * espaciado, 2);

            var evP = new List<int>();
            var evR = new List<double>();
            var coefIni = new List<int> { 0 };
            var coefSeg = new List<int>();
            var coefVal = new List<double>();
            var vis = new VisNivel
            {
                Seg = seg,
                R2 = R * R,
                Vj = new List<int>(),
                Vw = new List<double>(),
                Vdx = new List<double>(),
                Vdy = new List<double>(),
                Segs = new HashSet<int>(),
            };
            var porSegOrden = new List<int>();
            var porSegVal = new Dictionary<int, double>();
            for (int i = 0; i < n; i += k)
            {
                vis.P = seg[i];
                vis.Vj.Clear(); vis.Vw.Clear(); vis.Vdx.Clear(); vis.Vdy.Clear(); vis.Segs.Clear();
                vis.S0 = vis.Sx = vis.Sy = vis.Sxx = vis.Sxy = vis.Syy = 0;
                hash.Vecinos(x[i], y[i], R, ref vis);
                if (vis.Segs.Count < 2 || vis.Vj.Count < 6) continue;
                double S0 = vis.S0, Sx = vis.Sx, Sy = vis.Sy, Sxx = vis.Sxx, Sxy = vis.Sxy, Syy = vis.Syy;
                double mx = Sx / S0, my = Sy / S0;
                double cxx = Sxx / S0 - mx * mx, cxy = Sxy / S0 - mx * my, cyy = Syy / S0 - my * my;
                double tr = cxx + cyy, det2 = cxx * cyy - cxy * cxy;
                double eigMin = tr / 2 - Math.Sqrt(Math.Max(0, tr * tr / 4 - det2));
                if (eigMin < minEig) continue;
                if (Hypot(mx, my) > 0.75 * R) continue;       // extrapolaría demasiado
                double c00 = Sxx * Syy - Sxy * Sxy;
                double c01 = -(Sx * Syy - Sxy * Sy);
                double c02 = Sx * Sxy - Sxx * Sy;
                double det = S0 * c00 + Sx * c01 + Sy * c02;
                if (!(Math.Abs(det) > 1e-9)) continue;
                double bas = 0;
                porSegOrden.Clear();
                porSegVal.Clear();
                for (int t = 0; t < vis.Vj.Count; t++)
                {
                    double a = vis.Vw[t] * (c00 + c01 * vis.Vdx[t] + c02 * vis.Vdy[t]) / det;
                    bas += a * z[vis.Vj[t]];
                    int q = seg[vis.Vj[t]];
                    double prev;
                    if (porSegVal.TryGetValue(q, out prev)) porSegVal[q] = prev + a;
                    else { porSegVal[q] = a; porSegOrden.Add(q); }
                }
                evP.Add(vis.P);
                evR.Add(z[i] - bas);
                foreach (int q in porSegOrden) { coefSeg.Add(q); coefVal.Add(porSegVal[q]); }
                coefIni.Add(coefSeg.Count);
            }
            int nEv = evP.Count;
            if (nEv < 20) return SinCambio(nSeg, "pocas superposiciones entre pasadas");

            // Robustez: fuera los residuos > 4·MAD (mínimo 8 cm) de la mediana de su pasada.
            var porPasada = new List<double>[nSeg];
            for (int s = 0; s < nSeg; s++) porPasada[s] = new List<double>();
            for (int e = 0; e < nEv; e++) porPasada[evP[e]].Add(evR[e]);
            var med = new double[nSeg];
            var tol = new double[nSeg];
            for (int s = 0; s < nSeg; s++)
            {
                var a = porPasada[s];
                if (a.Count == 0) continue;
                med[s] = Mediana(a);
                var abs = new double[a.Count];
                for (int t = 0; t < a.Count; t++) abs[t] = Math.Abs(a[t] - med[s]);
                double mad = Mediana(abs) * 1.4826;
                tol[s] = Math.Max(0.08, 4 * mad);
            }
            var usa = new bool[nEv];
            var cnt = new double[nSeg];
            for (int e = 0; e < nEv; e++)
            {
                if (Math.Abs(evR[e] - med[evP[e]]) <= tol[evP[e]]) { usa[e] = true; cnt[evP[e]]++; }
            }
            const int minObs = 8;
            const double lambda = 4;
            int[] cIni = coefIni.ToArray();
            int[] cSeg = coefSeg.ToArray();
            double[] cVal = coefVal.ToArray();
            int[] eP = evP.ToArray();
            double[] eR = evR.ToArray();

            Func<double[], double> sesgoRms = off =>
            {
                var acc = new double[nSeg];
                for (int e = 0; e < nEv; e++)
                {
                    if (!usa[e]) continue;
                    double v = eR[e] - off[eP[e]];
                    for (int t = cIni[e]; t < cIni[e + 1]; t++) v += cVal[t] * off[cSeg[t]];
                    acc[eP[e]] += v;
                }
                double s2 = 0;
                int m = 0;
                for (int s = 0; s < nSeg; s++)
                {
                    if (cnt[s] >= minObs) { double v = acc[s] / cnt[s]; s2 += v * v; m++; }
                }
                return m > 0 ? Math.Sqrt(s2 / m) : double.NaN;
            };

            // Mínimos cuadrados amortiguados por gradiente conjugado (CGLS):
            //   min Σ_e (r_e − o_p + Σ_q C_eq·o_q)² + λ·Σ o²
            var activa = new bool[nSeg];
            for (int s = 0; s < nSeg; s++) activa[s] = cnt[s] >= minObs;
            Action<double[], double[]> Ax = (v, outv) =>
            {
                for (int e = 0; e < nEv; e++)
                {
                    if (!usa[e]) { outv[e] = 0; continue; }
                    double a = activa[eP[e]] ? v[eP[e]] : 0;
                    for (int t = cIni[e]; t < cIni[e + 1]; t++) if (activa[cSeg[t]]) a -= cVal[t] * v[cSeg[t]];
                    outv[e] = a;
                }
            };
            Action<double[], double[]> ATx = (u, outv) =>
            {
                Array.Clear(outv, 0, outv.Length);
                for (int e = 0; e < nEv; e++)
                {
                    if (!usa[e]) continue;
                    double ue = u[e];
                    if (activa[eP[e]]) outv[eP[e]] += ue;
                    for (int t = cIni[e]; t < cIni[e + 1]; t++) if (activa[cSeg[t]]) outv[cSeg[t]] -= cVal[t] * ue;
                }
            };

            var off0 = new double[nSeg];
            double antes = sesgoRms(off0);
            const int iterMax = 200;
            var res = new double[nEv];
            for (int e = 0; e < nEv; e++) res[e] = usa[e] ? eR[e] : 0;
            var sv = new double[nSeg];
            var qv = new double[nEv];
            ATx(res, sv);
            var pv = (double[])sv.Clone();
            double gamma = Dot(sv, sv);
            double gamma0 = gamma;
            int iters = 0;
            for (; iters < iterMax && gamma > 1e-14 * Math.Max(1, gamma0); iters++)
            {
                Ax(pv, qv);
                double alfa = gamma / (Dot(qv, qv) + lambda * Dot(pv, pv));
                for (int s = 0; s < nSeg; s++) off0[s] += alfa * pv[s];
                for (int e = 0; e < nEv; e++) res[e] -= alfa * qv[e];
                ATx(res, sv);
                for (int s = 0; s < nSeg; s++) sv[s] -= lambda * off0[s];
                double gNuevo = Dot(sv, sv);
                double beta = gNuevo / gamma;
                gamma = gNuevo;
                for (int s = 0; s < nSeg; s++) pv[s] = sv[s] + beta * pv[s];
            }
            // El datum no cambia: se quita la media ponderada de los offsets.
            double sw = 0, sm = 0;
            for (int s = 0; s < nSeg; s++) if (activa[s]) { sw += cnt[s]; sm += cnt[s] * off0[s]; }
            if (sw != 0) for (int s = 0; s < nSeg; s++) if (activa[s]) off0[s] -= sm / sw;
            // Un offset de más de 50 cm no es sesgo de RTK: es un dato roto.
            for (int s = 0; s < nSeg; s++) if (Math.Abs(off0[s]) > 0.5) off0[s] = 0;
            double despues = sesgoRms(off0);
            int niveladas = 0;
            for (int s = 0; s < nSeg; s++) if (cnt[s] >= minObs) niveladas++;
            return new NivelacionResultado
            {
                Offsets = off0,
                Aplicada = true,
                Motivo = null,
                RadioM = R,
                Observaciones = nEv,
                Iteraciones = iters,
                PasadasNiveladas = niveladas,
                SesgoRmsAntesM = antes,
                SesgoRmsDespuesM = despues,
            };
        }

        private static NivelacionResultado SinCambio(int nSeg, string motivo)
            => new NivelacionResultado { Offsets = new double[nSeg], Aplicada = false, Motivo = motivo };

        private static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        // ════════════════════════════════════════════════════════════════
        //  5. Grilla
        // ════════════════════════════════════════════════════════════════

        private struct VisPlano : IVisitante
        {
            public double[] Z;
            public double R2;
            public double Dmin, S0, Sx, Sy, Sxx, Sxy, Syy, Sz, Sxz, Syz;
            public void Visitar(int j, double d2, double dx, double dy)
            {
                if (d2 < Dmin) Dmin = d2;
                double u = 1 - d2 / R2, w = u * u;
                double zz = Z[j];
                S0 += w; Sx += w * dx; Sy += w * dy; Sxx += w * dx * dx; Sxy += w * dx * dy; Syy += w * dy * dy;
                Sz += w * zz; Sxz += w * dx * zz; Syz += w * dy * zz;
            }
        }

        /// <summary>
        /// Cada celda: plano ponderado (peso (1−d²/R²)²) de los puntos a ≤ R,
        /// evaluado en el centro. Sin extrapolar: si el punto más cercano está a
        /// más de dCerca la celda queda NaN.
        /// </summary>
        internal static GrillaAlturas Grillar(double[] x, double[] y, double[] z, int n, double res, double espaciado, double radio)
        {
            double esp = double.IsNaN(espaciado) ? 0 : espaciado;
            double R = !double.IsNaN(radio) ? radio : Math.Min(30, Math.Max(Math.Max(2.5 * res, 1.25 * esp), 4));
            double dCerca = Math.Max(Math.Max(0.75 * res, 0.6 * esp), 2);
            double minx = double.PositiveInfinity, miny = double.PositiveInfinity;
            double maxx = double.NegativeInfinity, maxy = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                if (x[i] < minx) minx = x[i];
                if (x[i] > maxx) maxx = x[i];
                if (y[i] < miny) miny = y[i];
                if (y[i] > maxy) maxy = y[i];
            }
            double x0 = Math.Floor((minx - dCerca) / res) * res;
            double y0 = Math.Floor((miny - dCerca) / res) * res;
            int nx = (int)Math.Floor((maxx + dCerca - x0) / res) + 1;
            int ny = (int)Math.Floor((maxy + dCerca - y0) / res) + 1;
            double yTop = y0 + (ny - 1) * res;
            var hash = new HashEspacial(x, y, n, R);
            var g = new float[nx * ny];
            for (int i = 0; i < g.Length; i++) g[i] = float.NaN;
            double dC2 = dCerca * dCerca, R2 = R * R;
            double ridge = 1e-3 * R2;
            int cubiertas = 0;
            var v = new VisPlano { Z = z, R2 = R2 };
            for (int r = 0; r < ny; r++)
            {
                double cy = yTop - r * res;
                for (int c = 0; c < nx; c++)
                {
                    double cx = x0 + c * res;
                    v.Dmin = double.PositiveInfinity;
                    v.S0 = v.Sx = v.Sy = v.Sxx = v.Sxy = v.Syy = v.Sz = v.Sxz = v.Syz = 0;
                    hash.Vecinos(cx, cy, R, ref v);
                    if (v.Dmin > dC2 || v.S0 <= 0) continue;
                    double S0 = v.S0, Sx = v.Sx, Sy = v.Sy, Sxy = v.Sxy, Sz = v.Sz, Sxz = v.Sxz, Syz = v.Syz;
                    double a11 = v.Sxx + ridge * S0, a22 = v.Syy + ridge * S0;
                    double det = S0 * (a11 * a22 - Sxy * Sxy) - Sx * (Sx * a22 - Sxy * Sy) + Sy * (Sx * Sxy - a11 * Sy);
                    double media = Sz / S0;
                    double val = media;
                    if (Math.Abs(det) > 1e-12)
                    {
                        double da = Sz * (a11 * a22 - Sxy * Sxy) - Sx * (Sxz * a22 - Sxy * Syz) + Sy * (Sxz * Sxy - a11 * Syz);
                        double a = da / det;
                        if (!double.IsNaN(a) && !double.IsInfinity(a) && Math.Abs(a - media) < 0.5) val = a;
                    }
                    g[r * nx + c] = (float)val;
                    cubiertas++;
                }
            }
            return new GrillaAlturas { Z = g, Nx = nx, Ny = ny, Res = res, X0 = x0, YTop = yTop, RadioM = R, DCercaM = dCerca, Cubiertas = cubiertas };
        }

        // ════════════════════════════════════════════════════════════════
        //  6a. Pendiente (%)
        // ════════════════════════════════════════════════════════════════

        public static float[] Pendientes(GrillaAlturas G)
        {
            float[] z = G.Z;
            int nx = G.Nx, ny = G.Ny;
            double res = G.Res;
            var p = new float[nx * ny];
            for (int i = 0; i < p.Length; i++) p[i] = float.NaN;
            for (int r = 0; r < ny; r++)
            {
                for (int c = 0; c < nx; c++)
                {
                    double v = z[r * nx + c];
                    if (double.IsNaN(v)) continue;
                    double e = Val(z, nx, ny, r, c + 1), w = Val(z, nx, ny, r, c - 1);
                    double nN = Val(z, nx, ny, r - 1, c), s = Val(z, nx, ny, r + 1, c);
                    double dzdx, dzdy;
                    if (!double.IsNaN(e) && !double.IsNaN(w)) dzdx = (e - w) / (2 * res);
                    else if (!double.IsNaN(e)) dzdx = (e - v) / res;
                    else if (!double.IsNaN(w)) dzdx = (v - w) / res;
                    else continue;
                    // Fila 0 = norte: "arriba" es r-1.
                    if (!double.IsNaN(nN) && !double.IsNaN(s)) dzdy = (nN - s) / (2 * res);
                    else if (!double.IsNaN(nN)) dzdy = (nN - v) / res;
                    else if (!double.IsNaN(s)) dzdy = (v - s) / res;
                    else continue;
                    p[r * nx + c] = (float)(100 * Hypot(dzdx, dzdy));
                }
            }
            return p;
        }

        private static double Val(float[] z, int nx, int ny, int r, int c)
            => (r < 0 || c < 0 || r >= ny || c >= nx) ? double.NaN : z[r * nx + c];

        // ════════════════════════════════════════════════════════════════
        //  6b. Bajos: llenado de depresiones (priority-flood, Barnes 2014)
        // ════════════════════════════════════════════════════════════════

        private sealed class Heap
        {
            private double[] _k;
            private int[] _v;
            public int N;
            public Heap(int cap) { _k = new double[cap]; _v = new int[cap]; }

            public void Push(double key, int val)
            {
                int i = N++;
                if (i >= _k.Length)
                {
                    Array.Resize(ref _k, _k.Length * 2);
                    Array.Resize(ref _v, _v.Length * 2);
                }
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_k[p] <= key) break;
                    _k[i] = _k[p]; _v[i] = _v[p]; i = p;
                }
                _k[i] = key; _v[i] = val;
            }

            public int Pop()
            {
                int top = _v[0];
                int n = --N;
                if (n > 0)
                {
                    double key = _k[n];
                    int val = _v[n];
                    int i = 0;
                    for (; ; )
                    {
                        int c = 2 * i + 1;
                        if (c >= n) break;
                        if (c + 1 < n && _k[c + 1] < _k[c]) c++;
                        if (_k[c] >= key) break;
                        _k[i] = _k[c]; _v[i] = _v[c]; i = c;
                    }
                    _k[i] = key; _v[i] = val;
                }
                return top;
            }
        }

        private static readonly int[] DR = { -1, -1, -1, 0, 0, 1, 1, 1 };
        private static readonly int[] DC = { -1, 0, 1, -1, 1, -1, 0, 1 };

        /// <summary>
        /// El agua sale por el borde de la zona relevada (borde de la grilla o
        /// celda vecina a una sin dato). Lo que queda por debajo del nivel de
        /// desborde es un bajo: profundidad = lleno − z.
        /// </summary>
        public static BajosResultado Bajos(GrillaAlturas G, double umbral = 0.05, double areaMinM2 = 50)
        {
            float[] z = G.Z;
            int nx = G.Nx, ny = G.Ny;
            double res = G.Res;
            int N = nx * ny;
            var lleno = new float[N];
            for (int i = 0; i < N; i++) lleno[i] = float.NaN;
            var visto = new bool[N];
            var heap = new Heap(Math.Max(1024, 2 * (nx + ny)));
            for (int r = 0; r < ny; r++)
            {
                for (int c = 0; c < nx; c++)
                {
                    int i = r * nx + c;
                    if (float.IsNaN(z[i])) continue;
                    bool borde = r == 0 || c == 0 || r == ny - 1 || c == nx - 1;
                    if (!borde) borde = float.IsNaN(z[i - 1]) || float.IsNaN(z[i + 1]) || float.IsNaN(z[i - nx]) || float.IsNaN(z[i + nx]);
                    if (borde) { visto[i] = true; lleno[i] = z[i]; heap.Push(z[i], i); }
                }
            }
            while (heap.N > 0)
            {
                int i = heap.Pop();
                float nivel = lleno[i];
                int r = i / nx, c = i - r * nx;
                for (int t = 0; t < 8; t++)
                {
                    int rr = r + DR[t], cc = c + DC[t];
                    if (rr < 0 || cc < 0 || rr >= ny || cc >= nx) continue;
                    int j = rr * nx + cc;
                    if (visto[j] || float.IsNaN(z[j])) continue;
                    visto[j] = true;
                    lleno[j] = Math.Max(z[j], nivel);
                    heap.Push(lleno[j], j);
                }
            }
            var prof = new float[N];
            for (int i = 0; i < N; i++)
            {
                if (!float.IsNaN(z[i]) && !float.IsNaN(lleno[i]))
                {
                    float d = lleno[i] - z[i];
                    prof[i] = d >= umbral ? d : 0;
                }
            }
            // Componentes conexas (8) → zonas; las chicas se descartan.
            var etiqueta = new int[N];
            for (int i = 0; i < N; i++) etiqueta[i] = -1;
            var zonas = new List<ZonaBajo>();
            var pila = new int[N];
            double areaCelda = res * res;
            int nZonas = 0;
            var celdas = new List<int>();
            for (int i0 = 0; i0 < N; i0++)
            {
                if (prof[i0] <= 0 || etiqueta[i0] >= 0) continue;
                int id = nZonas++;
                int sp = 0;
                pila[sp++] = i0;
                etiqueta[i0] = id;
                celdas.Clear();
                double profMax = 0, vol = 0, sr = 0, sc = 0, zMin = double.PositiveInfinity;
                while (sp > 0)
                {
                    int i = pila[--sp];
                    celdas.Add(i);
                    double d = prof[i];
                    if (d > profMax) profMax = d;
                    vol += d * areaCelda;
                    if (z[i] < zMin) zMin = z[i];
                    int r = i / nx, c = i - r * nx;
                    sr += r; sc += c;
                    for (int t = 0; t < 8; t++)
                    {
                        int rr = r + DR[t], cc = c + DC[t];
                        if (rr < 0 || cc < 0 || rr >= ny || cc >= nx) continue;
                        int j = rr * nx + cc;
                        if (prof[j] > 0 && etiqueta[j] < 0) { etiqueta[j] = id; pila[sp++] = j; }
                    }
                }
                double area = celdas.Count * areaCelda;
                if (area < areaMinM2)
                {
                    foreach (int i in celdas) prof[i] = 0;
                    continue;
                }
                zonas.Add(new ZonaBajo
                {
                    Celdas = celdas.Count,
                    AreaM2 = area,
                    ProfMaxM = profMax,
                    VolumenM3 = vol,
                    Fila = sr / celdas.Count,
                    Col = sc / celdas.Count,
                    ZMin = zMin,
                });
            }
            return new BajosResultado { Prof = prof, Zonas = zonas };
        }

        // ════════════════════════════════════════════════════════════════
        //  6c. Curvas de nivel (marching squares sobre centros de celda)
        // ════════════════════════════════════════════════════════════════

        private static double RedondearJs(double v) => Math.Floor(v + 0.5);

        /// <summary>Curvas de nivel cada <paramref name="intervalo"/> m (se agranda si darían más de 250 niveles).</summary>
        public static CurvasResultado CurvasNivel(GrillaAlturas G, double intervalo)
        {
            float[] z = G.Z;
            int nx = G.Nx, ny = G.Ny;
            double zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
            for (int i = 0; i < z.Length; i++)
            {
                double v = z[i];
                if (v < zmin) zmin = v;
                if (v > zmax) zmax = v;
            }
            var outp = new CurvasResultado { Intervalo = intervalo };
            if (!(zmax > zmin)) return outp;
            double inter = intervalo;
            while ((zmax - zmin) / inter > 250) inter *= 2;
            outp.Intervalo = inter;
            double nivel0 = Math.Ceiling(zmin / inter) * inter;
            for (double L = nivel0; L <= zmax; L += inter)
            {
                double lv = RedondearJs(L * 1e4) / 1e4;
                outp.Niveles.Add(UnNivel(z, nx, ny, lv));
            }
            return outp;
        }

        /// <summary>La curva de una cota puntual (para "guía por cota").</summary>
        public static NivelCurva CurvaDeCota(GrillaAlturas G, double cota) => UnNivel(G.Z, G.Nx, G.Ny, cota);

        private static NivelCurva UnNivel(float[] z, int nx, int ny, double lv)
        {
            var punto = new Dictionary<int, PuntoGrilla>();
            var ady = new Dictionary<int, List<int>>();
            var orden = new List<int>();   // orden de inserción de ady (Map de JS)
            Action<int, int> unir = (a, b) =>
            {
                List<int> la, lb;
                if (!ady.TryGetValue(a, out la)) { la = new List<int>(); ady[a] = la; orden.Add(a); }
                if (!ady.TryGetValue(b, out lb)) { lb = new List<int>(); ady[b] = lb; orden.Add(b); }
                la.Add(b); lb.Add(a);
            };
            Func<int, int, int, int, int, int> cruce = (k, r1, c1, r2, c2) =>
            {
                if (!punto.ContainsKey(k))
                {
                    double v1 = z[r1 * nx + c1], v2 = z[r2 * nx + c2];
                    double t = (lv - v1) / (v2 - v1);
                    punto[k] = new PuntoGrilla(c1 + t * (c2 - c1), r1 + t * (r2 - r1));
                }
                return k;
            };
            for (int r = 0; r < ny - 1; r++)
            {
                for (int c = 0; c < nx - 1; c++)
                {
                    double a = z[r * nx + c], b = z[r * nx + c + 1], d = z[(r + 1) * nx + c], e = z[(r + 1) * nx + c + 1];
                    if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(d) || double.IsNaN(e)) continue;
                    int caso = (a >= lv ? 8 : 0) | (b >= lv ? 4 : 0) | (e >= lv ? 2 : 0) | (d >= lv ? 1 : 0);
                    if (caso == 0 || caso == 15) continue;
                    int rr = r, cc = c;
                    Func<int> top = () => cruce(2 * (rr * nx + cc), rr, cc, rr, cc + 1);
                    Func<int> bot = () => cruce(2 * ((rr + 1) * nx + cc), rr + 1, cc, rr + 1, cc + 1);
                    Func<int> izq = () => cruce(2 * (rr * nx + cc) + 1, rr, cc, rr + 1, cc);
                    Func<int> der = () => cruce(2 * (rr * nx + cc + 1) + 1, rr, cc + 1, rr + 1, cc + 1);
                    switch (caso)
                    {
                        case 1: case 14: unir(izq(), bot()); break;
                        case 2: case 13: unir(bot(), der()); break;
                        case 3: case 12: unir(izq(), der()); break;
                        case 4: case 11: unir(top(), der()); break;
                        case 6: case 9: unir(top(), bot()); break;
                        case 7: case 8: unir(izq(), top()); break;
                        case 5:
                        case 10:
                            {
                                // Silla: decide el promedio del centro.
                                bool centro = (a + b + d + e) / 4 >= lv;
                                if ((caso == 5) == centro) { unir(izq(), top()); unir(bot(), der()); }
                                else { unir(izq(), bot()); unir(top(), der()); }
                                break;
                            }
                    }
                }
            }
            // Encadenar: primero desde extremos (grado 1), después los anillos cerrados.
            var usado = new HashSet<int>();
            var lineas = new List<List<int>>();
            Func<int, List<int>> recorrer = inicio =>
            {
                var l = new List<int> { inicio };
                usado.Add(inicio);
                int actual = inicio;
                for (; ; )
                {
                    int sig = -1;
                    bool hay = false;
                    foreach (int kk in ady[actual]) { if (!usado.Contains(kk)) { sig = kk; hay = true; break; } }
                    if (!hay)
                    {
                        if (l.Count > 2 && ady[actual].Contains(inicio)) l.Add(inicio);
                        break;
                    }
                    usado.Add(sig);
                    l.Add(sig);
                    actual = sig;
                }
                return l;
            };
            foreach (int k in orden) if (ady[k].Count == 1 && !usado.Contains(k)) lineas.Add(recorrer(k));
            foreach (int k in orden) if (!usado.Contains(k)) lineas.Add(recorrer(k));
            var nivel = new NivelCurva { Elev = lv };
            foreach (var l in lineas)
            {
                if (l.Count < 2) continue;
                var pts = new List<PuntoGrilla>(l.Count);
                foreach (int k in l) pts.Add(punto[k]);
                nivel.Lineas.Add(pts);
            }
            return nivel;
        }

        /// <summary>Douglas-Peucker en coordenadas de grilla.</summary>
        public static List<PuntoGrilla> Simplificar(List<PuntoGrilla> pts, double tol)
        {
            if (pts.Count < 3) return pts;
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            var pila = new Stack<KeyValuePair<int, int>>();
            pila.Push(new KeyValuePair<int, int>(0, pts.Count - 1));
            double t2 = tol * tol;
            while (pila.Count > 0)
            {
                var par = pila.Pop();
                int i0 = par.Key, i1 = par.Value;
                double ax = pts[i0].C, ay = pts[i0].F, bx = pts[i1].C, by = pts[i1].F;
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                double maxd = -1;
                int idx = -1;
                for (int i = i0 + 1; i < i1; i++)
                {
                    double px = pts[i].C, py = pts[i].F;
                    double t = l2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0;
                    t = Math.Max(0, Math.Min(1, t));
                    double ex = ax + t * dx - px, ey = ay + t * dy - py, d = ex * ex + ey * ey;
                    if (d > maxd) { maxd = d; idx = i; }
                }
                if (maxd > t2)
                {
                    keep[idx] = true;
                    pila.Push(new KeyValuePair<int, int>(i0, idx));
                    pila.Push(new KeyValuePair<int, int>(idx, i1));
                }
            }
            var outp = new List<PuntoGrilla>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]);
            return outp;
        }

        private static bool DentroPoligono(double px, double py, IList<double[]> poly)
        {
            bool dentro = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i][0], yi = poly[i][1], xj = poly[j][0], yj = poly[j][1];
                if (((yi > py) != (yj > py)) && (px < (xj - xi) * (py - yi) / (yj - yi) + xi)) dentro = !dentro;
            }
            return dentro;
        }

        // ════════════════════════════════════════════════════════════════
        //  Orquestador
        // ════════════════════════════════════════════════════════════════

        public static ResultadoPlanimetria Calcular(IEnumerable<string> textosElevation, OpcionesPlanimetria o = null)
            => CalcularDesdePuntos(JuntarPuntos(textosElevation), o);

        public static ResultadoPlanimetria CalcularDesdePuntos(PuntosElevacion P, OpcionesPlanimetria o = null)
        {
            if (o == null) o = new OpcionesPlanimetria();
            var reloj = Stopwatch.StartNew();
            double res = o.Res > 0 ? o.Res : 3;
            double intervalo = o.IntervaloAuto ? 0.1 : (o.Intervalo > 0 ? o.Intervalo : 0.1);
            double umbralBajo = o.UmbralBajo;
            var cuenta = P.Cuenta ?? new CuentaElevacion();
            var R = new ResultadoPlanimetria { Res = res, ResPedida = res, Intervalo = intervalo, UmbralBajo = umbralBajo };
            if (P.N < 50)
            {
                R.Ok = false;
                R.Motivo = P.N > 0 ? "pocos puntos con RTK fijo" : "sin puntos con RTK fijo";
                R.Stats.PuntosFilas = cuenta.Filas;
                R.Stats.PuntosRtk = P.N;
                return R;
            }

            var Q = Proyectar(P, o.MaxPuntos > 0 ? o.MaxPuntos : MaxPuntos);
            double[] x = Q.X, y = Q.Y, z = Q.Z;
            int n = Q.N;
            int nSeg;
            int[] seg = SegmentarPasadas(x, y, n, out nSeg);
            var hashN = new HashEspacial(x, y, n, 10);
            double espaciado = EstimarEspaciado(x, y, n, seg, hashN);

            var niv = new NivelacionResultado { Offsets = new double[nSeg], Aplicada = false, Motivo = "desactivada" };
            if (o.Nivelar)
            {
                double Rn = Math.Min(30, Math.Max(8, 2.1 * (double.IsNaN(espaciado) ? 8 : espaciado)));
                var hashL = new HashEspacial(x, y, n, Rn);
                niv = NivelarPasadas(x, y, z, n, seg, nSeg, hashL, espaciado);
            }
            var zc = new double[n];
            for (int i = 0; i < n; i++) zc[i] = z[i] - niv.Offsets[seg[i]];

            // Tope de celdas: se agranda la resolución si el lote es enorme.
            double minx = double.PositiveInfinity, miny = double.PositiveInfinity;
            double maxx = double.NegativeInfinity, maxy = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                if (x[i] < minx) minx = x[i];
                if (x[i] > maxx) maxx = x[i];
                if (y[i] < miny) miny = y[i];
                if (y[i] > maxy) maxy = y[i];
            }
            int maxCeldas = o.MaxCeldas > 0 ? o.MaxCeldas : MaxCeldas;
            while (((maxx - minx) / res + 3) * ((maxy - miny) / res + 3) > maxCeldas) res *= 1.5;
            res = RedondearJs(res * 100) / 100;

            var G = Grillar(x, y, zc, n, res, espaciado, o.RadioGrilla);
            var pend = Pendientes(G);
            double areaMin = !double.IsNaN(o.AreaMinBajoM2) ? o.AreaMinBajoM2 : Math.Max(50, 4 * res * res);
            var B = Bajos(G, umbralBajo, areaMin);
            if (o.IntervaloAuto)
            {
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                for (int i = 0; i < G.Z.Length; i++) { double v = G.Z[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
                double des = hi > lo ? hi - lo : 0;
                intervalo = IntervalosAuto[IntervalosAuto.Length - 1];
                foreach (double c in IntervalosAuto) if (des / c <= 15) { intervalo = c; break; }
            }
            var C = CurvasNivel(G, intervalo);

            // Estadísticas.
            double zmin = double.PositiveInfinity, zmax = double.NegativeInfinity, zs = 0;
            int nz = 0;
            var pv = new List<double>();
            for (int i = 0; i < G.Z.Length; i++)
            {
                double v = G.Z[i];
                if (double.IsNaN(v)) continue;
                if (v < zmin) zmin = v;
                if (v > zmax) zmax = v;
                zs += v; nz++;
                if (!float.IsNaN(pend[i])) pv.Add(pend[i]);
            }
            pv.Sort();
            double? coberturaPct = null, limiteHa = null;
            if (o.Limite != null && o.Limite.Count >= 3)
            {
                var poly = new List<double[]>();
                foreach (var ll in o.Limite) poly.Add(new[] { Q.Pr.X(ll[1]), Q.Pr.Y(ll[0]) });
                int conDato = 0;
                for (int r = 0; r < G.Ny; r++)
                {
                    double cy = G.YTop - r * G.Res;
                    for (int c = 0; c < G.Nx; c++)
                    {
                        if (DentroPoligono(G.X0 + c * G.Res, cy, poly) && !float.IsNaN(G.Z[r * G.Nx + c])) conDato++;
                    }
                }
                double a2 = 0;
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) a2 += poly[j][0] * poly[i][1] - poly[i][0] * poly[j][1];
                double areaLim = Math.Abs(a2) / 2;
                limiteHa = areaLim / 1e4;
                if (areaLim > 0) coberturaPct = Math.Min(100, 100 * conDato * G.Res * G.Res / areaLim);
            }

            B.Zonas.Sort((a, b) => b.VolumenM3.CompareTo(a.VolumenM3));
            double bajosArea = 0, bajosVol = 0;
            foreach (var zn in B.Zonas) { bajosArea += zn.AreaM2; bajosVol += zn.VolumenM3; }
            double offMax = 0;
            foreach (double off in niv.Offsets) offMax = Math.Max(offMax, Math.Abs(off));
            double pendSum = 0;
            foreach (double v in pv) pendSum += v;

            R.Ok = true;
            R.Res = G.Res;
            R.Intervalo = C.Intervalo;
            R.Proyeccion = Q.Pr;
            R.Grilla = G;
            R.Pendiente = pend;
            R.Bajos = B;
            R.Curvas = C;
            var st = R.Stats;
            st.PuntosFilas = cuenta.Filas;
            st.PuntosRtk = P.N;
            st.PuntosDescartadosNoRtk = cuenta.NoRtk;
            st.PuntosInvalidos = cuenta.Invalidas;
            st.PuntosUsados = n;
            st.Raleo = Q.Raleo;
            st.Partes = cuenta.Partes;
            st.Pasadas = nSeg;
            st.EspaciadoPasadasM = R2(espaciado, 1);
            st.ZMinM = R2(zmin, 3);
            st.ZMaxM = R2(zmax, 3);
            st.DesnivelM = R2(zmax - zmin, 3);
            st.ZMediaM = R2(zs / nz, 3);
            st.AreaCubiertaHa = R2(nz * G.Res * G.Res / 1e4, 2);
            st.LimiteHa = limiteHa.HasValue ? R2(limiteHa.Value, 2) : null;
            st.CoberturaPct = coberturaPct.HasValue ? R2(coberturaPct.Value, 1) : null;
            st.PendienteMediaPct = pv.Count > 0 ? R2(pendSum / pv.Count, 2) : null;
            st.PendienteP90Pct = pv.Count > 0 ? R2(pv[Math.Min(pv.Count - 1, (int)Math.Floor(0.9 * pv.Count))], 2) : null;
            st.PendienteMaxPct = pv.Count > 0 ? R2(pv[pv.Count - 1], 2) : null;
            st.BajosCantidad = B.Zonas.Count;
            st.BajosAreaHa = R2(bajosArea / 1e4, 2);
            st.BajosVolumenM3 = R2(bajosVol, 0);
            st.NivelacionAplicada = niv.Aplicada;
            st.NivelacionMotivo = niv.Motivo;
            st.PasadasNiveladas = niv.PasadasNiveladas;
            st.SesgoRmsAntesCm = R2(niv.SesgoRmsAntesM * 100, 2);
            st.SesgoRmsDespuesCm = R2(niv.SesgoRmsDespuesM * 100, 2);
            st.OffsetMaxCm = R2(offMax * 100, 2);
            st.Ms = reloj.ElapsedMilliseconds;
            R.Seg = seg;
            R.Offsets = niv.Offsets;
            R.X = x;
            R.Y = y;
            R.ZOriginal = z;
            R.ZCorregida = zc;
            return R;
        }

        private static double? R2(double v, int d)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
            double f = Math.Pow(10, d);
            return RedondearJs(v * f) / f;
        }
    }
}
