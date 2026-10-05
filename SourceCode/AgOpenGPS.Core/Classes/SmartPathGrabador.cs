// ============================================================================
// SmartPathGrabador.cs — guía por última pasada ("SmartPath"): la parte pura.
//
// Lo que ofrece la competencia (Ag Leader SmartPath, Sensor "línea guía por
// última pasada"): el operario no arma ninguna guía. Hace la primera pasada a
// mano, gira en la cabecera, y la pasada recién hecha se vuelve la guía de la
// siguiente (una paralela a un ancho de implemento). Cada pasada es la
// referencia de la próxima, así el error no se acumula como al desplazar N
// veces una curva vieja.
//
// Esta clase solo decide CUÁNDO terminó una pasada y QUÉ puntos son la pasada
// (sin el giro). Armar la guía con esos puntos es trabajo del motor, con la
// misma maquinaria de curva que "AB + Curva" (CTrk modo Curve). El lado de la
// paralela no se elige acá: el seguidor de curva toma siempre la paralela más
// cercana al tractor, que al salir del giro es la del lado no trabajado.
//
// Fin de pasada = giro de cabecera: en los últimos VentanaGiroM metros el
// rumbo cambió ≥ GiroFinDePasadaDeg. Una curva de nivel no llega a eso en 60 m;
// un giro en U, aun con fondo plano o en pera, sí. La pasada se entrega recién
// cuando el tractor ya encaró la siguiente (rumbo opuesto, ± ToleranciaAlineadoDeg),
// para que la guía nueva no aparezca a mitad del giro.
//
// Los puntos se toman cada DistanciaMinPuntoM: el ruido del GPS con el tractor
// parado no fabrica rumbos. La marcha atrás la filtra quien llama (el motor).
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public sealed class SmartPathGrabador
    {
        /// <summary>Separación mínima entre puntos grabados (m).</summary>
        public double DistanciaMinPuntoM { get; set; } = 1.0;

        /// <summary>Cambio de rumbo que se considera giro de cabecera (°).</summary>
        public double GiroFinDePasadaDeg { get; set; } = 150;

        /// <summary>En cuántos metros tiene que darse ese cambio de rumbo.</summary>
        public double VentanaGiroM { get; set; } = 60;

        /// <summary>Pasadas más cortas que esto son maniobras, no generan guía (m).</summary>
        public double LargoMinimoPasadaM { get; set; } = 20;

        /// <summary>Cuánto puede diferir el rumbo de la pasada nueva del opuesto a la anterior (°).</summary>
        public double ToleranciaAlineadoDeg { get; set; } = 20;

        /// <summary>Si después del giro nunca se alinea, la pasada se entrega igual a estos metros.</summary>
        public double MaxEsperaAlineadoM { get; set; } = 60;

        /// <summary>Por debajo de esta curvatura (°/m) el tramo se toma como derecho al recortar el giro.</summary>
        public double CurvaturaGiroDegPorM { get; set; } = 2.0;

        /// <summary>Pasadas entregadas desde el último Reiniciar.</summary>
        public int PasadasTerminadas { get; private set; }

        /// <summary>Puntos de la pasada en curso (0 mientras se está girando).</summary>
        public int PuntosPasadaActual => _enGiro ? 0 : _pts.Count;

        /// <summary>true entre el giro de cabecera y el momento en que encara la pasada siguiente.</summary>
        public bool EnGiro => _enGiro;

        // Puntos de la pasada en curso. heading = rumbo del tramo que LLEGA al
        // punto (NaN si no se conoce, el primero de todos).
        private List<vec3> _pts = new List<vec3>();
        private List<vec3> _giro = new List<vec3>();
        private bool _enGiro;
        private double _distGiro;
        private double _rumboEsperado = double.NaN;
        private List<vec3> _pendiente;

        // Una pasada de 10 km no existe; el tope solo evita crecer sin fin si
        // alguien deja la función prendida dando vueltas sin girar nunca.
        private const int MaxPuntos = 20000;

        public void Reiniciar()
        {
            _pts = new List<vec3>();
            _giro = new List<vec3>();
            _enGiro = false;
            _distGiro = 0;
            _rumboEsperado = double.NaN;
            _pendiente = null;
            PasadasTerminadas = 0;
        }

        /// <summary>
        /// Agrega una posición (pivote del tractor, yendo hacia adelante). Devuelve
        /// la pasada recién terminada (puntos en el sentido de avance) cuando el
        /// tractor ya salió del giro y encaró la siguiente; null el resto del tiempo.
        /// </summary>
        public List<vec3> Agregar(double easting, double northing)
        {
            var lista = _enGiro ? _giro : _pts;
            if (!AgregarPunto(lista, easting, northing)) return null;

            return _enGiro ? TickGiro() : TickPasada();
        }

        // ---- pasada en curso ------------------------------------------------

        private List<vec3> TickPasada()
        {
            if (_pts.Count > MaxPuntos) _pts.RemoveRange(0, MaxPuntos / 10);

            int last = _pts.Count - 1;
            if (last < 2) return null;

            // ¿Giro de cabecera? Suma de cambios de rumbo (con signo: un zigzag
            // se cancela) en los últimos VentanaGiroM metros.
            double acc = 0, dist = 0;
            int ws = last;
            for (int i = last; i >= 1 && dist < VentanaGiroM; i--)
            {
                acc += Delta(_pts, i);
                dist += Largo(_pts, i);
                ws = i - 1;
            }
            if (Math.Abs(acc) < GiroFinDePasadaDeg) return null;

            // Dónde empezó el giro: hacia atrás desde el final, primero hay que
            // haber recorrido casi todo el giro (así un fondo plano —90°, tramo
            // derecho, 90°— no corta en el tramo del medio) y después el primer
            // punto con el tramo previo derecho es el fin de la pasada.
            double minimoGirado = GiroFinDePasadaDeg - 30;
            int corte = ws;
            double despues = 0;
            for (int k = last - 1; k > ws; k--)
            {
                despues += Delta(_pts, k + 1);
                if (Math.Abs(despues) >= minimoGirado && CurvaturaAntes(_pts, k) < CurvaturaGiroDegPorM)
                {
                    corte = k;
                    break;
                }
            }

            double largoPasada = 0;
            for (int i = 1; i <= corte; i++) largoPasada += Largo(_pts, i);

            _pendiente = largoPasada >= LargoMinimoPasadaM ? _pts.GetRange(0, corte + 1) : null;

            double rumboFin = _pts[corte].heading;
            if (double.IsNaN(rumboFin) && corte + 1 <= last) rumboFin = _pts[corte + 1].heading;
            _rumboEsperado = double.IsNaN(rumboFin) ? double.NaN : Normalizar(rumboFin + Math.PI);

            _giro = new List<vec3> { _pts[last] };
            _distGiro = 0;
            _enGiro = true;
            _pts = new List<vec3>();
            return null;
        }

        // ---- en el giro, esperando que encare la pasada siguiente -------------

        private List<vec3> TickGiro()
        {
            _distGiro += Largo(_giro, _giro.Count - 1);

            bool alineado = false;
            if (!double.IsNaN(_rumboEsperado) && _giro.Count >= 4)
            {
                alineado = true;
                double tol = ToleranciaAlineadoDeg * Math.PI / 180.0;
                for (int i = _giro.Count - 3; i < _giro.Count; i++)
                {
                    double h = _giro[i].heading;
                    if (double.IsNaN(h) || Math.Abs(Envolver(h - _rumboEsperado)) > tol) { alineado = false; break; }
                }
            }

            if (!alineado && _distGiro < MaxEsperaAlineadoM) return null;

            // La pasada nueva arranca con lo ya alineado (o desde acá si nunca se alineó).
            _pts = alineado ? _giro.GetRange(_giro.Count - 4, 4) : new List<vec3> { _giro[_giro.Count - 1] };
            _giro = new List<vec3>();
            _enGiro = false;

            var entregada = _pendiente;
            _pendiente = null;
            if (entregada != null) PasadasTerminadas++;
            return entregada;
        }

        // ---- geometría --------------------------------------------------------

        private bool AgregarPunto(List<vec3> lista, double e, double n)
        {
            if (lista.Count == 0)
            {
                // Primer punto después de Reiniciar: todavía no hay rumbo.
                lista.Add(new vec3(e, n, double.NaN));
                return true;
            }

            var ult = lista[lista.Count - 1];
            double de = e - ult.easting, dn = n - ult.northing;
            if (de * de + dn * dn < DistanciaMinPuntoM * DistanciaMinPuntoM) return false;

            lista.Add(new vec3(e, n, Normalizar(Math.Atan2(de, dn))));
            return true;
        }

        /// <summary>Cambio de rumbo (rad, con signo) al llegar al punto i.</summary>
        private static double Delta(List<vec3> l, int i)
        {
            if (i < 1) return 0;
            double a = l[i - 1].heading, b = l[i].heading;
            if (double.IsNaN(a) || double.IsNaN(b)) return 0;
            return Envolver(b - a) * 180.0 / Math.PI;
        }

        private static double Largo(List<vec3> l, int i)
        {
            if (i < 1) return 0;
            double de = l[i].easting - l[i - 1].easting, dn = l[i].northing - l[i - 1].northing;
            return Math.Sqrt(de * de + dn * dn);
        }

        /// <summary>Curvatura media (°/m) de los 3 tramos que llegan al punto k.</summary>
        private static double CurvaturaAntes(List<vec3> l, int k)
        {
            double ang = 0, largo = 0;
            for (int i = k; i > k - 3 && i >= 1; i--)
            {
                ang += Delta(l, i);
                largo += Largo(l, i);
            }
            return largo <= 0 ? 0 : Math.Abs(ang) / largo;
        }

        private static double Envolver(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        private static double Normalizar(double a)
        {
            a %= 2 * Math.PI;
            if (a < 0) a += 2 * Math.PI;
            return a;
        }
    }
}
