// ============================================================================
// RegistroElevacionTests.cs — planimetría fase 1: qué fix se graba en
// Elevation.txt y con qué altura de SUELO.
//
// Fijan la parte pura (RegistroElevacion): solo RTK fijo, un punto cada ≥1 m
// recorrido, nada parado ni en marcha atrás, corrección de la altura de la
// antena por rolido/cabeceo y descarte de saltos de fix.
// ============================================================================

using System;
using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class RegistroElevacionTests
    {
        private static MuestraElevacion Muestra(double e, double n, double alt,
            int calidad = 4, double kmh = 8, bool atras = false,
            double rolido = 0, double cabeceo = 0, double antena = 3.0)
        {
            return new MuestraElevacion
            {
                Easting = e,
                Northing = n,
                AltitudAntena = alt,
                CalidadFix = calidad,
                VelocidadKmh = kmh,
                MarchaAtras = atras,
                RolidoGrados = rolido,
                CabeceoGrados = cabeceo,
                AlturaAntena = antena,
            };
        }

        // ---- altura del suelo ---------------------------------------------

        [Test]
        public void AlturaSuelo_SinInclinacion_RestaLaAntenaEntera()
        {
            Assert.That(RegistroElevacion.AlturaSuelo(103.0, 3.0, 0, 0), Is.EqualTo(100.0).Within(1e-9));
        }

        [Test]
        public void AlturaSuelo_ConRolido_RestaLaProyeccionVertical()
        {
            // 10° de rolido con 3 m de antena: la antena queda a 3·cos(10°)
            // = 2,954 m sobre el suelo, no a 3 m.
            double h = RegistroElevacion.AlturaSuelo(103.0, 3.0, 10, 0);
            Assert.That(h, Is.EqualTo(103.0 - 3.0 * Math.Cos(10 * Math.PI / 180)).Within(1e-9));
        }

        [Test]
        public void AlturaSuelo_ConRolidoYCabeceo_MultiplicaLosCosenos()
        {
            double h = RegistroElevacion.AlturaSuelo(50.0, 2.5, 5, 4);
            double esperado = 50.0 - 2.5 * Math.Cos(5 * Math.PI / 180) * Math.Cos(4 * Math.PI / 180);
            Assert.That(h, Is.EqualTo(esperado).Within(1e-9));
        }

        [Test]
        public void AlturaSuelo_RolidoCentinelaSinImu_SeTomaComoCero()
        {
            // 88888 = "sin dato de IMU" en CAHRS. No es un ángulo.
            Assert.That(RegistroElevacion.AlturaSuelo(103.0, 3.0, 88888, 0), Is.EqualTo(100.0).Within(1e-9));
            Assert.That(RegistroElevacion.AlturaSuelo(103.0, 3.0, double.NaN, double.NaN), Is.EqualTo(100.0).Within(1e-9));
        }

        [Test]
        public void AlturaSuelo_AnguloAbsurdo_SeIgnora()
        {
            // Una IMU en falla (90°) anularía la resta de la antena: ±45° es
            // el techo de lo creíble para un tractor andando.
            Assert.That(RegistroElevacion.AlturaSuelo(103.0, 3.0, 90, 0), Is.EqualTo(100.0).Within(1e-9));
        }

        // ---- calidad de fix ------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(5)]
        [TestCase(8)]
        public void SoloRtkFijo_ElRestoNoSeGraba(int calidad)
        {
            var r = new RegistroElevacion();
            bool ok = r.Evaluar(Muestra(0, 0, 100, calidad), out _);
            Assert.That(ok, Is.False);
            Assert.That(r.Estado, Is.EqualTo(EstadoElevacion.SinRtkFijo));
        }

        [Test]
        public void RtkFijo_PrimerPuntoSeGraba()
        {
            var r = new RegistroElevacion();
            bool ok = r.Evaluar(Muestra(10, 20, 103, 4), out var p);
            Assert.That(ok, Is.True);
            Assert.That(r.Estado, Is.EqualTo(EstadoElevacion.Grabando));
            Assert.That(p.Easting, Is.EqualTo(10));
            Assert.That(p.Northing, Is.EqualTo(20));
            Assert.That(p.AlturaSuelo, Is.EqualTo(100.0).Within(1e-9));
            Assert.That(r.PuntosGrabados, Is.EqualTo(1));
        }

        [Test]
        public void Simulador_SoloConElFlagDePruebas()
        {
            var r = new RegistroElevacion { AceptarSimulador = true };
            Assert.That(r.Evaluar(Muestra(0, 0, 100, 8), out _), Is.True);
        }

        // ---- espaciado / velocidad / marcha atrás --------------------------

        [Test]
        public void Espaciado_MenosDeUnMetro_NoGraba_MasDeUnMetro_Si()
        {
            var r = new RegistroElevacion();
            Assert.That(r.Evaluar(Muestra(0, 0, 100), out _), Is.True);
            Assert.That(r.Evaluar(Muestra(0, 0.6, 100), out _), Is.False, "0,6 m: todavía no");
            Assert.That(r.Estado, Is.EqualTo(EstadoElevacion.Grabando), "esperar distancia no es una pausa");
            Assert.That(r.Evaluar(Muestra(0, 1.05, 100), out _), Is.True, "1,05 m del último grabado");
            Assert.That(r.PuntosGrabados, Is.EqualTo(2));
        }

        [Test]
        public void Espaciado_EsPorDistanciaNoPorTiempo()
        {
            // Mil fixes en el mismo lugar (parado con velocidad ruidosa) = 1 punto.
            var r = new RegistroElevacion();
            int grabados = 0;
            for (int i = 0; i < 1000; i++)
                if (r.Evaluar(Muestra(0, 0.0001 * i, 100), out _)) grabados++;
            Assert.That(grabados, Is.EqualTo(1));
        }

        [Test]
        public void Parado_NoGraba()
        {
            var r = new RegistroElevacion();
            Assert.That(r.Evaluar(Muestra(0, 0, 100, kmh: 0.3), out _), Is.False);
            Assert.That(r.Estado, Is.EqualTo(EstadoElevacion.Detenido));
        }

        [Test]
        public void MarchaAtras_NoGraba()
        {
            var r = new RegistroElevacion();
            Assert.That(r.Evaluar(Muestra(0, 0, 100, atras: true), out _), Is.False);
            Assert.That(r.Estado, Is.EqualTo(EstadoElevacion.MarchaAtras));
        }

        // ---- saltos de fix -------------------------------------------------

        [Test]
        public void Salto_MasDeMedioMetroEnPocosMetros_SeDescarta()
        {
            var r = new RegistroElevacion();
            Assert.That(r.Evaluar(Muestra(0, 0, 100), out _), Is.True);
            Assert.That(r.Evaluar(Muestra(0, 1.2, 100.8), out _), Is.False, "0,8 m en 1,2 m = salto de fix");
            Assert.That(r.Descartados, Is.EqualTo(1));
            // El siguiente bueno se compara contra el último GRABADO, no contra el salto.
            Assert.That(r.Evaluar(Muestra(0, 2.4, 100.05), out _), Is.True);
        }

        [Test]
        public void Salto_LejosDelUltimoPunto_SeAcepta()
        {
            // Volver al lote después de una cabecera larga: 20 m sin puntos,
            // el terreno puede cambiar más de medio metro.
            var r = new RegistroElevacion();
            Assert.That(r.Evaluar(Muestra(0, 0, 100), out _), Is.True);
            Assert.That(r.Evaluar(Muestra(0, 20, 101.5), out _), Is.True);
        }

        [Test]
        public void PendienteSuave_NoEsSalto()
        {
            // 3% de pendiente con un punto por metro: 3 cm por punto.
            var r = new RegistroElevacion();
            int grabados = 0;
            for (int i = 0; i <= 50; i++)
                if (r.Evaluar(Muestra(0, i * 1.01, 100 + i * 0.0303), out _)) grabados++;
            Assert.That(grabados, Is.EqualTo(51));
            Assert.That(r.Descartados, Is.EqualTo(0));
        }

        [Test]
        public void PerderElRtk_YVolver_NoSeComparaContraUnPuntoViejo()
        {
            // Si el RTK se cae y vuelve en el mismo lugar con otra altura
            // (re-fix con otra base), el primer punto después del corte
            // debería seguir pudiendo grabarse si está lejos; si está cerca y
            // salta, se descarta — pero nunca se queda trabado: a los 5 m el
            // punto viejo ya no manda.
            var r = new RegistroElevacion();
            Assert.That(r.Evaluar(Muestra(0, 0, 100), out _), Is.True);
            Assert.That(r.Evaluar(Muestra(0, 1.5, 100, calidad: 5), out _), Is.False);
            Assert.That(r.Evaluar(Muestra(0, 3, 101), out _), Is.False, "salto cerca del último");
            Assert.That(r.Evaluar(Muestra(0, 5.5, 101), out _), Is.True, ">5 m: se acepta y pasa a ser la referencia");
            Assert.That(r.Evaluar(Muestra(0, 6.6, 101.02), out _), Is.True);
        }

        [Test]
        public void Reiniciar_OlvidaElUltimoPuntoYLaCuenta()
        {
            var r = new RegistroElevacion();
            r.Evaluar(Muestra(0, 0, 100), out _);
            r.Reiniciar();
            Assert.That(r.PuntosGrabados, Is.EqualTo(0));
            Assert.That(r.Evaluar(Muestra(0, 0.2, 100), out _), Is.True, "sin último punto: graba de una");
        }
    }
}
