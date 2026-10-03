// ============================================================================
// VxEspaciamiento.cs — dobles, fallas y CV de espaciamiento por surco
// (ISO 7256-1), a partir de los intervalos entre semillas del nodo VistaX.
//
// Desde el firmware vistax-node v3.1.0 cada cable PULSE manda en la
// telemetría `"dt":[...]`: el tiempo entre dos semillas consecutivas en
// unidades de 0,1 ms (0xFFFF = hueco ≥ 6,5 s: parada/cabecera). Con la
// velocidad del tractor, cada intervalo es una DISTANCIA entre semillas:
//
//     x_i = v · Δt_i
//
// y contra la distancia de referencia Xref = 1 / (sem/m objetivo):
//
//     doble   x ≤ 0,5·Xref
//     falla   x > 1,5·Xref
//     simple  el resto
//
// Índices (ISO 7256-1):
//     %Dobles, %Fallas, Singulación = 100 − D − F,
//     CV de precisión = desvío estándar de los SIMPLES / Xref.
//
// Se lleva una ventana móvil (los últimos ~300 espacios, lo que ve el
// operario) y dos acumulados (pasada y lote) para el informe.
//
// Exclusiones que resuelve ESTA clase (las de contexto — sección cortada,
// herramienta arriba, 2 s tras arrancar — las decide el que llama):
//   · huecos 0xFFFF y Δt = 0,
//   · velocidad < 2 km/h (la distancia v·Δt deja de ser confiable),
//   · densidades que el sensor no resuelve: si entre semillas hay menos de
//     ~10 ms (trigo a chorrillo), el filtro de 5 ms del nodo junta semillas
//     y los "dobles" serían ficción.
//
// Si no hay objetivo (Xref ≤ 0) se usa la MEDIANA de los últimos espacios:
// con pocos dobles/fallas la mediana cae casi sobre el espaciamiento real.
//
// Mensajes MQTT perdidos NO crean fallas: cada Δt lo mide el nodo entre dos
// semillas consecutivas; perder un mensaje pierde muestras, no inventa
// huecos (el nodo además avisa con `dt_lost`).
//
// Clase pura (sin reloj, sin MQTT): la cubre VxEspaciamientoTests.
// NO es thread-safe: VistaXLiveService la usa adentro de su lock.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AgroParallel.Services.VistaX
{
    public enum VxClaseEspacio { Doble, Simple, Falla }

    /// <summary>Índices de calidad de siembra sobre un conjunto de espacios.</summary>
    public sealed class VxIndicesEspaciamiento
    {
        public int NEspacios;
        public double DoblesPct;
        public double FallasPct;
        /// <summary>100 − dobles − fallas. 0 si no hay espacios.</summary>
        public double SingulacionPct;
        /// <summary>CV de precisión: desvío de los simples / Xref, en %.</summary>
        public double CvPct;
    }

    public sealed class VxEspaciamiento
    {
        public const int DtHueco = 0xFFFF;
        public const double VelMinKmh = 2.0;
        public const double FactorDoble = 0.5;
        public const double FactorFalla = 1.5;
        public const int VentanaDefault = 300;
        /// <summary>Espacios necesarios para confiar en la mediana (sin objetivo).</summary>
        public const int MinMuestrasMediana = 30;
        /// <summary>Por debajo de este tiempo esperado entre semillas el nodo
        /// (filtro de 5 ms) no separa semillas: no se clasifica.</summary>
        public const double IntervaloMinResolubleS = 0.010;

        // ── Ventana móvil: clase + espacio normalizado (x / Xref) ────────────
        private readonly int _cap;
        private readonly VxClaseEspacio[] _clase;
        private readonly double[] _r;
        private int _head, _count;

        // ── Mediana (modo sin objetivo) ──────────────────────────────────────
        private readonly double[] _xCrudo;
        private int _xHead, _xCount, _desdeMediana;
        private double _mediana;

        private readonly Acum _pasada = new Acum();
        private readonly Acum _lote = new Acum();
        // Tramo del registro por lote (VistaXRegistroLote): lo vacía quien lo
        // toma con TomarTramo(), cada ~10 m sembrados.
        private readonly Acum _tramo = new Acum();

        public VxEspaciamiento(int ventana = VentanaDefault)
        {
            _cap = ventana < 10 ? 10 : ventana;
            _clase = new VxClaseEspacio[_cap];
            _r = new double[_cap];
            _xCrudo = new double[_cap];
        }

        /// <summary>Espacios descartados por las exclusiones de esta clase.</summary>
        public long Descartados { get; private set; }

        public static VxClaseEspacio Clasificar(double xM, double xrefM)
        {
            if (xM <= FactorDoble * xrefM) return VxClaseEspacio.Doble;
            if (xM > FactorFalla * xrefM) return VxClaseEspacio.Falla;
            return VxClaseEspacio.Simple;
        }

        /// <summary>
        /// Suma un intervalo del firmware. <paramref name="dt01ms"/> en 0,1 ms;
        /// <paramref name="velKmh"/> la velocidad filtrada del tractor;
        /// <paramref name="xrefM"/> la distancia objetivo entre semillas (m),
        /// o ≤ 0 para usar la mediana. Devuelve true si el espacio contó.
        /// </summary>
        public bool Agregar(int dt01ms, double velKmh, double xrefM)
        {
            if (dt01ms <= 0 || dt01ms >= DtHueco || velKmh < VelMinKmh
                || double.IsNaN(velKmh) || double.IsInfinity(velKmh))
            {
                Descartados++;
                return false;
            }
            double vMs = velKmh / 3.6;
            double x = vMs * dt01ms * 1e-4;

            double xref = xrefM;
            if (!(xref > 0))
            {
                GuardarCrudo(x);
                if (_xCount < MinMuestrasMediana) { Descartados++; return false; }
                xref = Mediana();
            }
            if (!(xref > 0) || xref / vMs < IntervaloMinResolubleS)
            {
                Descartados++;
                return false;
            }

            var clase = Clasificar(x, xref);
            double r = x / xref;

            _clase[_head] = clase;
            _r[_head] = r;
            _head = (_head + 1) % _cap;
            if (_count < _cap) _count++;

            _pasada.Sumar(clase, r);
            _lote.Sumar(clase, r);
            _tramo.Sumar(clase, r);
            return true;
        }

        /// <summary>Índices de los últimos espacios (lo que ve el operario).</summary>
        public VxIndicesEspaciamiento Ventana()
        {
            var a = new Acum();
            for (int i = 0; i < _count; i++) a.Sumar(_clase[i], _r[i]);
            return a.Indices();
        }

        public VxIndicesEspaciamiento Pasada() { return _pasada.Indices(); }
        public VxIndicesEspaciamiento Lote() { return _lote.Indices(); }

        public void ResetPasada() { _pasada.Reset(); }
        public void ResetLote() { _lote.Reset(); }

        /// <summary>Índices desde el último TomarTramo (sin vaciar).</summary>
        public VxIndicesEspaciamiento Tramo() { return _tramo.Indices(); }

        /// <summary>Índices del tramo en curso y lo vacía: el registro por
        /// lote lo llama al cerrar cada tramo de ~10 m.</summary>
        public VxIndicesEspaciamiento TomarTramo()
        {
            var ix = _tramo.Indices();
            _tramo.Reset();
            return ix;
        }

        public void ResetTramo() { _tramo.Reset(); }

        /// <summary>Vacía la ventana (ej. cambio de insumo): los espacios viejos
        /// se midieron contra otro objetivo.</summary>
        public void ResetVentana()
        {
            _head = _count = 0;
            _xHead = _xCount = 0;
            _desdeMediana = 0;
            _mediana = 0;
        }

        // ── Parser del payload v2 ────────────────────────────────────────────

        /// <summary>
        /// Lee `dt` (y `dt_lost`) de un objeto de sensor de la telemetría
        /// `agp.vistax.telemetry/2`. Devuelve null si el payload no trae `dt`
        /// (firmware &lt; 3.1): el camino viejo sigue igual.
        /// </summary>
        public static int[] LeerDt(JsonElement sensor, out int dtLost)
        {
            dtLost = 0;
            if (sensor.ValueKind != JsonValueKind.Object) return null;
            JsonElement jl;
            if (sensor.TryGetProperty("dt_lost", out jl) && jl.ValueKind == JsonValueKind.Number)
            {
                long l;
                if (jl.TryGetInt64(out l) && l > 0) dtLost = l > int.MaxValue ? int.MaxValue : (int)l;
            }
            JsonElement jd;
            if (!sensor.TryGetProperty("dt", out jd) || jd.ValueKind != JsonValueKind.Array) return null;
            var lista = new List<int>(jd.GetArrayLength());
            foreach (var e in jd.EnumerateArray())
            {
                int v;
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v)) lista.Add(v);
            }
            return lista.ToArray();
        }

        // ── internos ─────────────────────────────────────────────────────────

        private void GuardarCrudo(double x)
        {
            _xCrudo[_xHead] = x;
            _xHead = (_xHead + 1) % _cap;
            if (_xCount < _cap) _xCount++;
            _desdeMediana++;
        }

        // Recalcula cada 25 espacios: ordenar 300 doubles por semilla es plata
        // tirada y la mediana se mueve despacio.
        private double Mediana()
        {
            if (_mediana > 0 && _desdeMediana < 25) return _mediana;
            var copia = new double[_xCount];
            Array.Copy(_xCrudo, copia, _xCount);
            Array.Sort(copia);
            int m = _xCount / 2;
            _mediana = (_xCount % 2 == 1) ? copia[m] : 0.5 * (copia[m - 1] + copia[m]);
            _desdeMediana = 0;
            return _mediana;
        }

        private sealed class Acum
        {
            private long _n, _d, _f, _s;
            private double _sumR, _sumR2;

            public void Sumar(VxClaseEspacio c, double r)
            {
                _n++;
                if (c == VxClaseEspacio.Doble) _d++;
                else if (c == VxClaseEspacio.Falla) _f++;
                else
                {
                    _s++;
                    _sumR += r;
                    _sumR2 += r * r;
                }
            }

            public void Reset() { _n = _d = _f = _s = 0; _sumR = _sumR2 = 0; }

            public VxIndicesEspaciamiento Indices()
            {
                var ix = new VxIndicesEspaciamiento
                {
                    NEspacios = _n > int.MaxValue ? int.MaxValue : (int)_n
                };
                if (_n <= 0) return ix;
                ix.DoblesPct = 100.0 * _d / _n;
                ix.FallasPct = 100.0 * _f / _n;
                ix.SingulacionPct = 100.0 - ix.DoblesPct - ix.FallasPct;
                if (_s >= 2)
                {
                    // Desvío muestral de x/Xref = desvío de x / Xref (ISO 7256-1).
                    double media = _sumR / _s;
                    double var = (_sumR2 - _s * media * media) / (_s - 1);
                    ix.CvPct = var > 0 ? 100.0 * Math.Sqrt(var) : 0;
                }
                return ix;
            }
        }
    }

    /// <summary>
    /// Aviso AMARILLO de singulación: la ventana del surco cae por debajo de
    /// (objetivo del insumo − margen) de forma SOSTENIDA mientras se siembra.
    /// El rojo sigue siendo el surco tapado/caído — esto es calidad, no corte.
    /// </summary>
    public sealed class VxAlarmaSingulacion
    {
        public const double MargenDefault = 3.0;
        public const double SostenidoSegundos = 20.0;
        /// <summary>Con menos espacios en la ventana el índice no tiene peso.</summary>
        public const int MinEspacios = 100;

        private DateTime _bajoDesde = DateTime.MinValue;

        public double Margen { get; set; } = MargenDefault;
        public bool Activa { get; private set; }

        public bool Evaluar(double singulacionPct, int nEspacios, double objetivoPct,
            bool sembrando, DateTime now)
        {
            bool bajo = sembrando && nEspacios >= MinEspacios && objetivoPct > 0
                        && singulacionPct < objetivoPct - Margen;
            if (!bajo)
            {
                _bajoDesde = DateTime.MinValue;
                Activa = false;
                return false;
            }
            if (_bajoDesde == DateTime.MinValue) _bajoDesde = now;
            Activa = (now - _bajoDesde).TotalSeconds >= SostenidoSegundos;
            return Activa;
        }

        public void Reset()
        {
            _bajoDesde = DateTime.MinValue;
            Activa = false;
        }
    }
}
