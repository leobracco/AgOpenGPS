using System;

namespace AgOpenGPS
{
    /// <summary>
    /// Planimetría fase 1 — decide si un fix se graba en Elevation.txt y
    /// calcula la altura del SUELO. Clase pura: no sabe de disco, de lote ni
    /// de settings; el motor le pasa una <see cref="MuestraElevacion"/> por
    /// fix y escribe lo que ésta acepte.
    ///
    /// Reglas (cada una tiene su test en RegistroElevacionTests):
    ///   · Solo RTK FIJO (calidad GGA 4). Flotante (5), DGPS (2) o autónomo (1)
    ///     no sirven: con señal libre la altura baila metros, y un mapa de
    ///     alturas hecho con eso dibuja lomas que no existen. El simulador (8)
    ///     solo con <see cref="AceptarSimulador"/> (pruebas de banco).
    ///   · Espaciado por DISTANCIA, no por tiempo: un punto cada ≥1 m desde el
    ///     último grabado. Parado no se acumulan puntos sobre el mismo lugar.
    ///   · Nada por debajo de 0,5 km/h ni en marcha atrás (en reversa el
    ///     rumbo/rolido se invierten y la maniobra no aporta terreno nuevo).
    ///   · Altura del suelo = altitud GGA − antena × cos(rolido) × cos(cabeceo).
    ///     Con 3 m de antena y 10° de rolido la antena está a 2,95 m del suelo:
    ///     5 cm de diferencia, el orden de lo que se quiere medir.
    ///   · Saltos de fix: si la altura cambia más de <see cref="SaltoMaximo"/>
    ///     (0,5 m) respecto del último punto GRABADO estando a menos de
    ///     <see cref="DistanciaSalto"/> (5 m), se descarta. A partir de 5 m el
    ///     punto viejo ya no manda: así un punto malo no traba el registro.
    ///
    /// La posición horizontal NO se corrige acá: el pipeline ya lo hace. En
    /// CHeadingUpdater (región "Offset Roll" / "Roll") pn.fix se corre por
    /// rollCorrectionDistance = sin(rolido) × −antena y por el offset lateral
    /// de la antena ANTES de llamar a TheRest(), que es donde el motor llama a
    /// este registro. Corregir de nuevo duplicaría la corrección.
    /// </summary>
    public sealed class RegistroElevacion
    {
        public const int CalidadRtkFijo = 4;
        public const int CalidadSimulador = 8;

        /// <summary>CAHRS.imuRoll vale 88888 cuando no hay IMU: no es un ángulo.</summary>
        public const double CentinelaSinImu = 88888;

        /// <summary>Más inclinación que esto no es un tractor andando: es una
        /// IMU en falla. Se ignora el ángulo (cos = 1) en vez de anular la
        /// resta de la antena.</summary>
        public const double AnguloMaximoCreible = 45.0;

        public double DistanciaMinima { get; set; } = 1.0;
        public double VelocidadMinimaKmh { get; set; } = 0.5;
        public double SaltoMaximo { get; set; } = 0.5;
        public double DistanciaSalto { get; set; } = 5.0;

        /// <summary>Aceptar fixes del simulador (calidad 8). Solo pruebas.</summary>
        public bool AceptarSimulador { get; set; }

        public EstadoElevacion Estado { get; private set; } = EstadoElevacion.SinRtkFijo;
        public int PuntosGrabados { get; private set; }
        public int Descartados { get; private set; }

        private bool _hayUltimo;
        private double _ultE, _ultN, _ultH;

        /// <summary>
        /// Evalúa un fix. true = grabarlo (<paramref name="punto"/> trae la
        /// altura del suelo). Actualiza <see cref="Estado"/> en todos los casos.
        /// </summary>
        public bool Evaluar(MuestraElevacion m, out PuntoElevacion punto)
        {
            punto = default(PuntoElevacion);

            bool calidadOk = m.CalidadFix == CalidadRtkFijo
                || (AceptarSimulador && m.CalidadFix == CalidadSimulador);
            if (!calidadOk)
            {
                Estado = EstadoElevacion.SinRtkFijo;
                return false;
            }
            if (m.MarchaAtras)
            {
                Estado = EstadoElevacion.MarchaAtras;
                return false;
            }
            if (!(m.VelocidadKmh >= VelocidadMinimaKmh))
            {
                Estado = EstadoElevacion.Detenido;
                return false;
            }
            if (double.IsNaN(m.AltitudAntena) || double.IsInfinity(m.AltitudAntena)
                || double.IsNaN(m.Easting) || double.IsNaN(m.Northing))
            {
                Estado = EstadoElevacion.SinRtkFijo;
                return false;
            }

            // Desde acá el registro está "andando": esperar el próximo metro
            // o descartar un salto no es una pausa para el operario.
            Estado = EstadoElevacion.Grabando;

            double h = AlturaSuelo(m.AltitudAntena, m.AlturaAntena, m.RolidoGrados, m.CabeceoGrados);

            if (_hayUltimo)
            {
                double de = m.Easting - _ultE;
                double dn = m.Northing - _ultN;
                double d = Math.Sqrt(de * de + dn * dn);
                if (d < DistanciaMinima) return false;
                if (d < DistanciaSalto && Math.Abs(h - _ultH) > SaltoMaximo)
                {
                    Descartados++;
                    return false;
                }
            }

            _hayUltimo = true;
            _ultE = m.Easting;
            _ultN = m.Northing;
            _ultH = h;
            PuntosGrabados++;
            punto = new PuntoElevacion { Easting = m.Easting, Northing = m.Northing, AlturaSuelo = h };
            return true;
        }

        /// <summary>Olvida el último punto y las cuentas (al cambiar de lote).</summary>
        public void Reiniciar()
        {
            _hayUltimo = false;
            PuntosGrabados = 0;
            Descartados = 0;
            Estado = EstadoElevacion.SinRtkFijo;
        }

        /// <summary>
        /// Altura del suelo bajo la antena: altitud − antena × cos(rolido) ×
        /// cos(cabeceo). Ángulos en grados; NaN, el centinela 88888 o más de
        /// ±45° se toman como 0 (sin dato creíble de inclinación).
        /// </summary>
        public static double AlturaSuelo(double altitudAntena, double alturaAntena,
            double rolidoGrados, double cabeceoGrados)
        {
            double antena = alturaAntena > 0 ? alturaAntena : 0;
            return altitudAntena - antena * CosSeguro(rolidoGrados) * CosSeguro(cabeceoGrados);
        }

        private static double CosSeguro(double grados)
        {
            if (double.IsNaN(grados) || double.IsInfinity(grados)) return 1.0;
            if (grados == CentinelaSinImu) return 1.0;
            if (Math.Abs(grados) > AnguloMaximoCreible) return 1.0;
            return Math.Cos(grados * Math.PI / 180.0);
        }
    }

    /// <summary>Por qué el registro está (o no) grabando, para mostrarle al operario.</summary>
    public enum EstadoElevacion
    {
        Grabando = 0,
        SinRtkFijo = 1,
        Detenido = 2,
        MarchaAtras = 3,
    }

    /// <summary>Lo que el motor sabe de un fix, ya corregido por el pipeline.</summary>
    public struct MuestraElevacion
    {
        /// <summary>pn.fix (plano local del lote) — YA corregido por rolido y offset de antena.</summary>
        public double Easting;
        public double Northing;
        /// <summary>Altitud GGA de la ANTENA (m).</summary>
        public double AltitudAntena;
        public int CalidadFix;
        public double VelocidadKmh;
        public bool MarchaAtras;
        /// <summary>Grados; 88888/NaN = sin IMU.</summary>
        public double RolidoGrados;
        /// <summary>Grados; NaN = sin dato.</summary>
        public double CabeceoGrados;
        /// <summary>setVehicle_antennaHeight (m).</summary>
        public double AlturaAntena;
    }

    public struct PuntoElevacion
    {
        public double Easting;
        public double Northing;
        public double AlturaSuelo;
    }
}
