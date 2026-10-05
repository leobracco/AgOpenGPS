// ============================================================================
// CeroWasEstadistico.cs — "Cero automático del WAS": MIDE y PROPONE, nunca
// aplica.
//
// Criterio del usuario (no negociable): el cero del WAS se calibra una vez y
// queda fijo; NUNCA se autocorrige en silencio. Esta clase solo junta el
// ángulo real de las ruedas mientras se anda derecho y estable con el piloto
// puesto, y estima cuánto está corrido el cero. El offset lo cambia el
// operario tocando "Aplicar" (y queda "Deshacer"); eso vive en el motor
// (EngineCeroWasService), no acá.
//
// Idea: andando en una recta y sobre la línea, la rueda tiene que estar en
// ~0°. Si el piloto tiene que sostener +1,8° para seguir derecho, el cero
// está corrido 1,8° a la derecha. El estimador es la MEDIANA (no la media):
// una corrección grande aislada no lo mueve.
//
// Inspirado en el SmartWAS de AgOpenWeb (Shared/AgOpenWeb.Services/AutoSteer/
// SmartWasCalibrationService.cs, a su vez port de CSmartWAS.cs de AgOpenGPS;
// © AgOpenWeb Contributors / AgOpenGPS, GPL-3.0): de ahí la mediana como
// estimador y el no invertir el ángulo por "Invertir WAS". Lo demás es propio:
// SmartWAS acepta muestras a ≥2 km/h y hasta 50 cm de la línea sin pedir
// recta, y su "recommended offset" se aplica directo; acá el filtro es mucho
// más estricto (recta, rumbo y error lateral estables durante 3 s) y la
// confianza mira además si el cero medido es el mismo al principio y al final.
//
// Signo (verificado contra el firmware AiO, ver tests):
//   firmware:  normal    → ángulo = (crudo − 6805 + offset) /  cpd
//              invertido → ángulo = (crudo − 6805 − offset) / −cpd
//   En las DOS ramas d(ángulo)/d(offset) = +1/cpd, así que la corrección es
//   la misma que el cero manual (SteerConfigService.ZeroWas):
//       offset_nuevo = offset − sesgo × cpd
//   Ackermann: el firmware multiplica los ángulos NEGATIVOS por ack/100
//   después del offset; un sesgo a la izquierda se pasa a cuentas crudas
//   dividiendo por ack/100.
//
// Clase pura (C# 7.3, sin settings ni host): el motor le pasa una
// EntradaCeroWas por fix y la config del WAS vigente.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgOpenGPS.SteerCal
{
    public enum EstadoCeroWas
    {
        /// <summary>Todavía no hay tiempo/muestras suficientes.</summary>
        Juntando,
        /// <summary>El sesgo medido está debajo del umbral: no hay nada que cambiar.</summary>
        CeroBien,
        /// <summary>Hay una propuesta de offset lista para que el operario la aplique.</summary>
        Propuesta,
        /// <summary>Hay datos, pero dispersos o cambiantes: no se propone nada.</summary>
        Inestable,
        /// <summary>El sesgo es tan grande que no es un cero corrido: es el sensor mal montado.</summary>
        SesgoExcesivo,
        /// <summary>El offset resultante pasaría el tope de ±3900 cuentas.</summary>
        FueraDeRango,
    }

    /// <summary>Una foto por fix. Ángulos en grados, positivo = derecha.</summary>
    public struct EntradaCeroWas
    {
        /// <summary>Reloj monotónico, en segundos.</summary>
        public double T;
        /// <summary>Ángulo real de las ruedas del PGN 253. NaN = no llega.</summary>
        public double AnguloReal;
        public bool PilotoEnganchado;
        public bool ManejoLibre;
        /// <summary>Guía AB, o curva con curvatura despreciable donde está el tractor.</summary>
        public bool GuiaRecta;
        public double VelKmh;
        public bool MarchaAtras;
        public bool UTurn;
        /// <summary>Error lateral a la guía, en metros. NaN = sin guía.</summary>
        public double ErrorLateralM;
        /// <summary>Rumbo del fix, en radianes (para el yaw rate).</summary>
        public double RumboRad;
        /// <summary>Rolido de la IMU. NaN o 88888 = sin IMU (no se filtra).</summary>
        public double RolidoGrados;
    }

    /// <summary>Config del WAS con la que se están tomando las muestras.</summary>
    public struct ConfigWasCeroAuto
    {
        public int WasOffset;
        public int CuentasPorGrado;
        public int AckermanPct;
        public bool InvertWas;
    }

    public struct ResultadoCeroWas
    {
        public EstadoCeroWas Estado;
        /// <summary>Lo de ESTE fix cumple las condiciones de "andar derecho estable".</summary>
        public bool CondicionesOk;
        /// <summary>Por qué no (snake): piloto_suelto, manejo_libre, marcha_atras,
        /// vuelta_u, sin_angulo, sin_guia, guia_curva, velocidad, lejos_de_la_linea,
        /// corrigiendo, girando, rolido, angulo_grande, estabilizando. null = ok.</summary>
        public string Motivo;
        public int Muestras;
        public double SegundosEfectivos;
        public double SegundosRequeridos;
        /// <summary>Mediana del ángulo real en recta (= cuánto está corrido el cero).</summary>
        public double SesgoGrados;
        /// <summary>Dispersión robusta (1,4826 × MAD), en grados.</summary>
        public double DispersionGrados;
        /// <summary>0..100.</summary>
        public double Confianza;
        public int OffsetActual;
        /// <summary>Igual a OffsetActual si no hay propuesta.</summary>
        public int OffsetPropuesto;
        public bool HayPropuesta;
    }

    public sealed class CeroWasEstadistico
    {
        /// <summary>CAHRS.imuRoll vale 88888 cuando no hay IMU.</summary>
        public const double CentinelaSinImu = 88888;
        /// <summary>Mismo tope que el cero manual (btnZeroWAS_Click).</summary>
        public const int OffsetLimite = 3900;

        // ---- condiciones de muestreo ----
        public double VelMinKmh { get; set; } = 4.0;
        public double VelMaxKmh { get; set; } = 20.0;
        /// <summary>15 cm y no 10: con el cero corrido 2° un Pure Pursuit sin
        /// integral se estaciona a ~9 cm de la línea; con 10 cm justo los ceros
        /// más corridos (los que más importan) no se medirían nunca.</summary>
        public double ErrorLateralMaxM { get; set; } = 0.15;
        /// <summary>El error lateral no se tiene que estar moviendo (entrando a la línea).</summary>
        public double ErrorLateralVelMaxMs { get; set; } = 0.05;
        public double YawMaxGradS { get; set; } = 1.0;
        public double RolidoMaxGrados { get; set; } = 5.0;
        /// <summary>En recta, una muestra de más de 8° es una corrección, no el cero.</summary>
        public double AnguloMaxGrados { get; set; } = 8.0;
        /// <summary>Todas las condiciones sostenidas este tiempo antes de contar.</summary>
        public double EstabilizacionS { get; set; } = 3.0;
        /// <summary>Más que esto entre dos fixes = hueco: se vuelve a estabilizar.</summary>
        public double HuecoMaxS { get; set; } = 0.5;

        // ---- cuándo hay resultado ----
        public double TiempoMinimoS { get; set; } = 60.0;
        public int MuestrasMinimas { get; set; } = 300;
        public double UmbralCeroBienGrados { get; set; } = 0.3;
        public double SesgoMaxGrados { get; set; } = 6.0;
        public double ConfianzaMinima { get; set; } = 50.0;
        /// <summary>Ventana deslizante: ~10 min a 10 Hz.</summary>
        public int MaxMuestras { get; set; } = 6000;

        private const double TauFiltroS = 1.0;

        private readonly Queue<double> _muestras = new Queue<double>();
        private bool _hayCfg;
        private ConfigWasCeroAuto _cfg;
        private bool _hayPrevio;
        private double _tPrevio, _rumboPrevio, _xtePrevio;
        private bool _xtePrevioValido;
        private double _yawFilt, _xteVelFilt;
        private int _filtrosCargados;
        private double _desdeOk = double.NaN;
        private bool _previoAceptado;
        private double _efectivoS;
        private int _tramos;
        private bool _condOk;
        private string _motivo = "estabilizando";

        private bool _sucio = true;
        private ResultadoCeroWas _ultimo;

        /// <summary>Borra la medición (no la config). Lo llama el motor al aplicar o deshacer.</summary>
        public void Reiniciar()
        {
            _muestras.Clear();
            _efectivoS = 0;
            _tramos = 0;
            _desdeOk = double.NaN;
            _previoAceptado = false;
            _hayPrevio = false;
            _filtrosCargados = 0;
            _yawFilt = _xteVelFilt = 0;
            _condOk = false;
            _motivo = "estabilizando";
            _sucio = true;
        }

        /// <summary>Un fix. true = la muestra se sumó a la medición.</summary>
        public bool Evaluar(EntradaCeroWas e, ConfigWasCeroAuto cfg)
        {
            // Las muestras se tomaron con otro offset/escala/sentido: no valen más.
            if (!_hayCfg || !MismaCfg(cfg, _cfg))
            {
                bool habia = _hayCfg;
                _cfg = cfg;
                _hayCfg = true;
                if (habia) Reiniciar();
                _sucio = true;
            }

            double dt = _hayPrevio ? e.T - _tPrevio : double.NaN;
            bool hueco = !_hayPrevio || !(dt > 0) || dt > HuecoMaxS;
            ActualizarFiltros(e, dt, hueco);

            string motivo = Motivo(e);
            bool ok = motivo == null;
            if (ok && hueco) { ok = false; motivo = "estabilizando"; }

            bool acepta = false;
            if (ok)
            {
                if (double.IsNaN(_desdeOk)) _desdeOk = e.T;
                if (e.T - _desdeOk >= EstabilizacionS) acepta = true;
                else motivo = "estabilizando";
            }
            else
            {
                _desdeOk = double.NaN;
            }

            if (acepta)
            {
                if (!_previoAceptado) _tramos++;
                else _efectivoS += Math.Min(dt, HuecoMaxS);
                _muestras.Enqueue(e.AnguloReal);
                while (_muestras.Count > Math.Max(1, MaxMuestras)) _muestras.Dequeue();
                _sucio = true;
            }
            _previoAceptado = acepta;

            if (_condOk != acepta || !string.Equals(_motivo, motivo, StringComparison.Ordinal)) _sucio = true;
            _condOk = acepta;
            _motivo = acepta ? null : motivo;

            _tPrevio = e.T;
            _rumboPrevio = e.RumboRad;
            _xtePrevio = e.ErrorLateralM;
            _xtePrevioValido = !double.IsNaN(e.ErrorLateralM);
            _hayPrevio = true;
            return acepta;
        }

        private void ActualizarFiltros(EntradaCeroWas e, double dt, bool hueco)
        {
            if (hueco)
            {
                _filtrosCargados = 0;
                _yawFilt = _xteVelFilt = 0;
                return;
            }
            double dRumbo = e.RumboRad - _rumboPrevio;
            while (dRumbo > Math.PI) dRumbo -= 2 * Math.PI;
            while (dRumbo < -Math.PI) dRumbo += 2 * Math.PI;
            double yaw = dRumbo * 180.0 / Math.PI / dt;
            double xteVel = (_xtePrevioValido && !double.IsNaN(e.ErrorLateralM))
                ? (e.ErrorLateralM - _xtePrevio) / dt : 0;
            double a = dt / (TauFiltroS + dt);
            if (_filtrosCargados == 0) { _yawFilt = yaw; _xteVelFilt = xteVel; }
            else
            {
                _yawFilt += a * (yaw - _yawFilt);
                _xteVelFilt += a * (xteVel - _xteVelFilt);
            }
            _filtrosCargados++;
        }

        private string Motivo(EntradaCeroWas e)
        {
            if (!e.PilotoEnganchado) return "piloto_suelto";
            if (e.ManejoLibre) return "manejo_libre";
            if (e.MarchaAtras) return "marcha_atras";
            if (e.UTurn) return "vuelta_u";
            if (double.IsNaN(e.AnguloReal) || double.IsInfinity(e.AnguloReal)) return "sin_angulo";
            if (double.IsNaN(e.ErrorLateralM)) return "sin_guia";
            if (!e.GuiaRecta) return "guia_curva";
            double v = Math.Abs(e.VelKmh);
            if (double.IsNaN(v) || v < VelMinKmh || v > VelMaxKmh) return "velocidad";
            if (Math.Abs(e.ErrorLateralM) > ErrorLateralMaxM) return "lejos_de_la_linea";
            if (_filtrosCargados > 0 && Math.Abs(_xteVelFilt) > ErrorLateralVelMaxMs) return "corrigiendo";
            if (_filtrosCargados > 0 && Math.Abs(_yawFilt) > YawMaxGradS) return "girando";
            double rol = e.RolidoGrados;
            if (!double.IsNaN(rol) && rol != CentinelaSinImu && Math.Abs(rol) > RolidoMaxGrados) return "rolido";
            if (Math.Abs(e.AnguloReal) > AnguloMaxGrados) return "angulo_grande";
            return null;
        }

        /// <summary>Foto del estado. El análisis (ordenar) se hace acá, solo si hubo cambios.</summary>
        public ResultadoCeroWas Resultado()
        {
            if (!_sucio) return _ultimo;
            _ultimo = Analizar();
            _sucio = false;
            return _ultimo;
        }

        private ResultadoCeroWas Analizar()
        {
            var r = new ResultadoCeroWas
            {
                Estado = EstadoCeroWas.Juntando,
                CondicionesOk = _condOk,
                Motivo = _motivo,
                Muestras = _muestras.Count,
                SegundosEfectivos = _efectivoS,
                SegundosRequeridos = TiempoMinimoS,
                OffsetActual = _cfg.WasOffset,
                OffsetPropuesto = _cfg.WasOffset,
            };
            int n = _muestras.Count;
            if (n == 0) return r;

            double[] arr = _muestras.ToArray();      // orden cronológico
            double[] ord = (double[])arr.Clone();
            Array.Sort(ord);
            double med = Mediana(ord);
            var dev = new double[n];
            for (int i = 0; i < n; i++) dev[i] = Math.Abs(arr[i] - med);
            Array.Sort(dev);
            double sigma = 1.4826 * Mediana(dev);
            r.SesgoGrados = med;
            r.DispersionGrados = sigma;

            // Confianza: dispersión, cantidad, mismo cero al principio y al
            // final de la ventana, y repartido en varios tramos de recta.
            double disp = Clamp01(1.0 - sigma / 1.5);
            double cant = Clamp01(_efectivoS / (2.0 * TiempoMinimoS));
            double temporal = 0;
            if (n >= 20)
            {
                int mitad = n / 2;
                var a = new double[mitad];
                var b = new double[n - mitad];
                Array.Copy(arr, 0, a, 0, mitad);
                Array.Copy(arr, mitad, b, 0, n - mitad);
                Array.Sort(a);
                Array.Sort(b);
                temporal = Clamp01(1.0 - Math.Abs(Mediana(a) - Mediana(b)) / 0.6);
            }
            double tramos = Clamp01(_tramos / 3.0);
            r.Confianza = 100.0 * (0.30 * disp + 0.20 * cant + 0.35 * temporal + 0.15 * tramos);

            if (n < MuestrasMinimas || _efectivoS < TiempoMinimoS) return r;

            if (Math.Abs(med) > SesgoMaxGrados) { r.Estado = EstadoCeroWas.SesgoExcesivo; return r; }
            if (r.Confianza < ConfianzaMinima) { r.Estado = EstadoCeroWas.Inestable; return r; }
            if (Math.Abs(med) < UmbralCeroBienGrados) { r.Estado = EstadoCeroWas.CeroBien; return r; }

            int prop = CalcularOffsetPropuesto(_cfg.WasOffset, med, _cfg.CuentasPorGrado, _cfg.AckermanPct);
            if (Math.Abs(prop) > OffsetLimite) { r.Estado = EstadoCeroWas.FueraDeRango; return r; }

            r.Estado = EstadoCeroWas.Propuesta;
            r.OffsetPropuesto = prop;
            r.HayPropuesta = prop != _cfg.WasOffset;
            return r;
        }

        /// <summary>
        /// offset − sesgo × cpd (el mismo signo que ZeroWas). Un sesgo a la
        /// izquierda se pasa a cuentas crudas deshaciendo el Ackermann del firmware.
        /// </summary>
        public static int CalcularOffsetPropuesto(int offsetActual, double sesgoGrados, int cuentasPorGrado, int ackermanPct)
        {
            double grados = sesgoGrados;
            if (grados < 0 && ackermanPct > 0) grados = grados * 100.0 / ackermanPct;
            return offsetActual - (int)Math.Round(grados * cuentasPorGrado, MidpointRounding.AwayFromZero);
        }

        private static bool MismaCfg(ConfigWasCeroAuto a, ConfigWasCeroAuto b)
            => a.WasOffset == b.WasOffset && a.CuentasPorGrado == b.CuentasPorGrado
               && a.AckermanPct == b.AckermanPct && a.InvertWas == b.InvertWas;

        private static double Mediana(double[] ordenado)
        {
            int n = ordenado.Length;
            if (n == 0) return 0;
            return (n % 2 == 1) ? ordenado[n / 2] : 0.5 * (ordenado[n / 2 - 1] + ordenado[n / 2]);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
