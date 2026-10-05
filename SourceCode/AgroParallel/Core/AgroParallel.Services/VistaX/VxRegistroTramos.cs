// ============================================================================
// VxRegistroTramos.cs — arma los TRAMOS del registro VistaX por lote.
//
// Mientras se siembra con lote abierto, VistaXRegistroLote le pasa una
// muestra por segundo (posición, velocidad, sem/m de cada surco). Esta clase
// junta las muestras hasta completar un tramo —10 m recorridos, o 30 s si la
// máquina va muy despacio— y lo devuelve cerrado con, por surco:
//   · sem/m promedio PONDERADO POR DISTANCIA (semillas / metros, no el
//     promedio de lecturas: una muestra a 2 km/h no pesa lo mismo que una a 8),
//   · dobles / fallas / singulación / CV del tramo (los entrega
//     VistaXLiveService.TomarTramoEspaciamiento, ISO 7256-1),
//   · el corrimiento lateral del surco respecto del centro de la máquina.
//
// El tramo se ubica en el CENTROIDE de las posiciones muestreadas y lleva el
// rumbo; la posición de cada surco = centroide + off_m a la DERECHA del rumbo.
//
// Clase pura (sin reloj propio, sin disco, sin MQTT): la cubre
// VxRegistroTramosTests. NO es thread-safe (la usa un solo timer).
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Services.VistaX
{
    /// <summary>Índices de espaciamiento de un surco (tren + bajada).</summary>
    public sealed class VxIndicesSurco
    {
        public int Tren;
        public int Bajada;
        public VxIndicesEspaciamiento Indices;
    }

    /// <summary>Quien entrega el tramo de espaciamiento por surco y lo vacía
    /// (VistaXLiveService). Interfaz aparte para no tocar IVistaXLiveService.</summary>
    public interface IVistaXTramosEspaciamiento
    {
        List<VxIndicesSurco> TomarTramoEspaciamiento();
    }

    /// <summary>Lectura de un surco en una muestra.</summary>
    public sealed class VxMuestraSurco
    {
        public int Tren;
        public int Bajada;
        public double SemM;
        /// <summary>false = la lectura no cuenta para el sem/m (sección
        /// cortada, sin dato, silenciado).</summary>
        public bool Valida;
    }

    /// <summary>Una muestra (≈ 1 por segundo) del estado de la siembra.</summary>
    public sealed class VxMuestra
    {
        public DateTime Utc;
        public bool Sembrando;
        public double VelKmh;
        public double Lat, Lon;
        /// <summary>Posición local (m) para medir el recorrido; 0/0 = sin dato
        /// (se integra la velocidad).</summary>
        public double E, N;
        public double RumboRad;
        public double DistEntreSurcosM;
        public List<VxMuestraSurco> Surcos = new List<VxMuestraSurco>();
    }

    public sealed class VxTramoSurco
    {
        public int Tren;
        public int Bajada;
        /// <summary>Metros a la derecha del centro de la máquina (− = izquierda).</summary>
        public double OffM;
        /// <summary>Semillas por metro del tramo. -1 = sin lectura válida.</summary>
        public double SemM = -1;
        /// <summary>Índices ISO del tramo; null = sin datos `dt` (nodo &lt; v3.1).</summary>
        public VxIndicesEspaciamiento Esp;
    }

    public sealed class VxTramo
    {
        public DateTime InicioUtc;
        public DateTime FinUtc;
        public double Lat, Lon;
        public double RumboDeg;
        public double DistM;
        public double VelKmh;
        public List<VxTramoSurco> Surcos = new List<VxTramoSurco>();
    }

    public sealed class VxRegistroTramos
    {
        public const double LargoTramoDefaultM = 10.0;
        public const double MaxTramoDefaultS = 30.0;
        /// <summary>Un tramo más corto que esto (fin de pasada, cabecera) se
        /// tira: 1 m de datos no dice nada y ensucia el mapa.</summary>
        public const double MinTramoDefaultM = 2.0;
        /// <summary>Saltos de posición mayores no son metros sembrados
        /// (teleport del simulador, fix nuevo).</summary>
        public const double SaltoMaxM = 25.0;

        public double LargoTramoM { get; set; } = LargoTramoDefaultM;
        public double MaxTramoS { get; set; } = MaxTramoDefaultS;
        public double MinTramoM { get; set; } = MinTramoDefaultM;

        private sealed class AcumSurco
        {
            public int Tren, Bajada;
            public double OffM;
            public double SumSemMxD, SumD;
        }

        private readonly Dictionary<long, AcumSurco> _surcos = new Dictionary<long, AcumSurco>();
        private bool _abierto;
        private DateTime _inicio, _ultimo;
        private double _dist;
        private double _sumLat, _sumLon;
        private int _nPos;
        private double _rumboRad;
        private bool _tienePrev;
        private double _prevE, _prevN;
        private DateTime _prevT;

        /// <summary>Metros recorridos en el tramo en curso.</summary>
        public double DistanciaEnCurso { get { return _dist; } }

        public bool HayTramoEnCurso { get { return _abierto; } }

        /// <summary>
        /// Suma una muestra. Devuelve true cuando el tramo está listo para
        /// cerrar: el que llama pide los índices de espaciamiento y llama a
        /// <see cref="Cerrar"/>. También devuelve true si se dejó de sembrar
        /// con un tramo de al menos MinTramoM (se cierra el pedazo final).
        /// </summary>
        public bool Agregar(VxMuestra m)
        {
            if (m == null) return false;
            if (!m.Sembrando)
            {
                _tienePrev = false;
                if (!_abierto) return false;
                if (_dist >= MinTramoM) return true;
                Descartar();
                return false;
            }

            bool habiaPrev = _tienePrev;
            DateTime tPrev = _prevT;
            double paso = Paso(m);
            if (!_abierto)
            {
                // Continuación del tramo anterior: el paso desde la última
                // muestra ya es de este tramo y el tramo arranca en esa muestra.
                _abierto = true;
                _inicio = habiaPrev && paso > 0 ? tPrev : m.Utc;
            }
            _ultimo = m.Utc;
            _dist += paso;
            _rumboRad = m.RumboRad;
            if (Math.Abs(m.Lat) > 1e-6 || Math.Abs(m.Lon) > 1e-6)
            {
                _sumLat += m.Lat; _sumLon += m.Lon; _nPos++;
            }

            AcumularSurcos(m, paso);

            if (_dist >= LargoTramoM) return true;
            if ((_ultimo - _inicio).TotalSeconds >= MaxTramoS && _dist >= MinTramoM) return true;
            return false;
        }

        /// <summary>
        /// Cierra el tramo en curso con los índices de espaciamiento del
        /// período. Devuelve null si el tramo no alcanza o no tiene surcos con
        /// dato (y lo descarta igual).
        /// </summary>
        public VxTramo Cerrar(IList<VxIndicesSurco> espaciamiento)
        {
            if (!_abierto) return null;
            if (_dist < MinTramoM) { Descartar(); return null; }

            var t = new VxTramo
            {
                InicioUtc = _inicio,
                FinUtc = _ultimo,
                Lat = _nPos > 0 ? _sumLat / _nPos : 0,
                Lon = _nPos > 0 ? _sumLon / _nPos : 0,
                RumboDeg = NormalizarGrados(_rumboRad * 180.0 / Math.PI),
                DistM = _dist,
            };
            double dur = (_ultimo - _inicio).TotalSeconds;
            t.VelKmh = dur > 0 ? _dist / dur * 3.6 : 0;

            var porClave = new Dictionary<long, VxTramoSurco>();
            foreach (var a in _surcos.Values)
            {
                var ts = new VxTramoSurco { Tren = a.Tren, Bajada = a.Bajada, OffM = a.OffM };
                if (a.SumD > 0) ts.SemM = a.SumSemMxD / a.SumD;
                porClave[Clave(a.Tren, a.Bajada)] = ts;
            }
            if (espaciamiento != null)
            {
                foreach (var e in espaciamiento)
                {
                    if (e == null || e.Indices == null || e.Indices.NEspacios <= 0) continue;
                    VxTramoSurco ts;
                    if (!porClave.TryGetValue(Clave(e.Tren, e.Bajada), out ts))
                    {
                        // Surco con espacios pero sin lectura en las muestras
                        // (raro: mapeo nuevo). Sin offset conocido, va al centro.
                        ts = new VxTramoSurco { Tren = e.Tren, Bajada = e.Bajada };
                        porClave[Clave(e.Tren, e.Bajada)] = ts;
                    }
                    ts.Esp = e.Indices;
                }
            }
            foreach (var ts in porClave.Values)
                if (ts.SemM >= 0 || ts.Esp != null) t.Surcos.Add(ts);
            t.Surcos.Sort((x, y) => x.Bajada != y.Bajada ? x.Bajada.CompareTo(y.Bajada) : x.Tren.CompareTo(y.Tren));

            Descartar();
            return t.Surcos.Count > 0 ? t : null;
        }

        /// <summary>Tira el tramo en curso (cambio de lote, parada corta).</summary>
        public void Descartar()
        {
            _abierto = false;
            _dist = 0;
            _sumLat = _sumLon = 0;
            _nPos = 0;
            _surcos.Clear();
        }

        // ── internos ─────────────────────────────────────────────────────────

        private double Paso(VxMuestra m)
        {
            double paso = 0;
            bool tienePos = Math.Abs(m.E) > 1e-9 || Math.Abs(m.N) > 1e-9;
            if (_tienePrev)
            {
                if (tienePos)
                {
                    double dx = m.E - _prevE, dy = m.N - _prevN;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    paso = d < SaltoMaxM ? d : 0;
                }
                else
                {
                    double dt = (m.Utc - _prevT).TotalSeconds;
                    if (dt > 0 && dt <= 5 && m.VelKmh > 0) paso = m.VelKmh / 3.6 * dt;
                }
            }
            _tienePrev = true;
            _prevE = m.E; _prevN = m.N; _prevT = m.Utc;
            return paso;
        }

        private void AcumularSurcos(VxMuestra m, double paso)
        {
            if (m.Surcos == null || m.Surcos.Count == 0) return;

            // Corrimiento lateral: los surcos de semilla ordenados por bajada,
            // centrados, a la distancia entre surcos. Con trenes intercalados
            // es una aproximación (la bajada numera de izquierda a derecha).
            var bajadas = new List<int>();
            foreach (var s in m.Surcos)
                if (s != null && !bajadas.Contains(s.Bajada)) bajadas.Add(s.Bajada);
            bajadas.Sort();
            double dist = m.DistEntreSurcosM > 0 ? m.DistEntreSurcosM : 0;
            double medio = (bajadas.Count - 1) / 2.0;

            foreach (var s in m.Surcos)
            {
                if (s == null) continue;
                long k = Clave(s.Tren, s.Bajada);
                AcumSurco a;
                if (!_surcos.TryGetValue(k, out a))
                {
                    a = new AcumSurco { Tren = s.Tren, Bajada = s.Bajada };
                    _surcos[k] = a;
                }
                a.OffM = (bajadas.IndexOf(s.Bajada) - medio) * dist;
                if (s.Valida && paso > 0 && s.SemM >= 0 && !double.IsNaN(s.SemM) && !double.IsInfinity(s.SemM))
                {
                    a.SumSemMxD += s.SemM * paso;
                    a.SumD += paso;
                }
            }
        }

        private static long Clave(int tren, int bajada) { return ((long)tren << 32) | (uint)bajada; }

        private static double NormalizarGrados(double g)
        {
            g %= 360.0;
            if (g < 0) g += 360.0;
            return g;
        }
    }
}
