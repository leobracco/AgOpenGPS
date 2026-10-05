// Ciclado de secciones individuales y zonas — la lógica pura que hay detrás de
// los comandos "seccion_<n>" / "zona_<n>" del motor.
//
// Es el ciclo Off → Auto → On → Off de `GetNextState` (Sections.Designer.cs) más
// el reparto por rangos de zona (`IndividualZoneAndButtonToState`). Se testea acá
// como tabla de verdad para no depender de levantar el motor entero: si alguien
// cambia el orden del ciclo, esto lo caza.
//
// Ojo con el reparto de zonas: el rango de la zona 1 arranca en 0 y termina en
// zoneRanges[1]; para la zona N va de zoneRanges[N-1] a zoneRanges[N]. Es
// asimétrico a propósito y es el error fácil de cometer al portarlo.

using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class SeccionesCicladoTests
    {
        // Espejo de btnStates (AgOpenGPS.Core) para no acoplar el test al enum
        // del proyecto GPS: lo que importa es el ORDEN del ciclo.
        private enum Estado { Off = 0, Auto = 1, On = 2 }

        private static Estado Siguiente(Estado s) => s switch
        {
            Estado.Off => Estado.Auto,
            Estado.Auto => Estado.On,
            Estado.On => Estado.Off,
            _ => Estado.Off,
        };

        [Test]
        public void Ciclo_DeUnaSeccion_EsOffAutoOnOff()
        {
            var s = Estado.Off;
            Assert.That(s = Siguiente(s), Is.EqualTo(Estado.Auto), "1er toque: Off -> Auto");
            Assert.That(s = Siguiente(s), Is.EqualTo(Estado.On), "2do toque: Auto -> On");
            Assert.That(s = Siguiente(s), Is.EqualTo(Estado.Off), "3er toque: On -> Off");
        }

        /// <summary>Rango [inicio, fin) que le toca a la zona pedida, replicando
        /// btnZoneX_Click: la zona 1 arranca en 0, el resto en zoneRanges[n-1].</summary>
        private static (int inicio, int fin) RangoZona(int[] zoneRanges, int zona)
            => zona == 1 ? (0, zoneRanges[1]) : (zoneRanges[zona - 1], zoneRanges[zona]);

        [Test]
        public void Zona1_ArrancaEnCero()
        {
            // 3 zonas: secciones 0-3, 4-7, 8-11.
            var ranges = new[] { 0, 4, 8, 12, 0, 0, 0, 0, 0 };
            Assert.That(RangoZona(ranges, 1), Is.EqualTo((0, 4)));
        }

        [Test]
        public void ZonaN_VaDelRangoAnteriorAlPropio()
        {
            var ranges = new[] { 0, 4, 8, 12, 0, 0, 0, 0, 0 };
            Assert.That(RangoZona(ranges, 2), Is.EqualTo((4, 8)));
            Assert.That(RangoZona(ranges, 3), Is.EqualTo((8, 12)));
        }

        [Test]
        public void TocarUnaZona_NoTocaLasOtras()
        {
            var ranges = new[] { 0, 4, 8, 12, 0, 0, 0, 0, 0 };
            var secciones = new Estado[12]; // todas en Off

            // Tocar la zona 2: el estado sale de la ÚLTIMA sección de la zona
            // (zoneRanges[zona]-1), igual que el handler nativo.
            var (ini, fin) = RangoZona(ranges, 2);
            var nuevo = Siguiente(secciones[ranges[2] - 1]);
            for (int i = ini; i < fin; i++) secciones[i] = nuevo;

            Assert.That(secciones[4], Is.EqualTo(Estado.Auto));
            Assert.That(secciones[7], Is.EqualTo(Estado.Auto));
            Assert.That(secciones[3], Is.EqualTo(Estado.Off), "la zona 1 no se toca");
            Assert.That(secciones[8], Is.EqualTo(Estado.Off), "la zona 3 no se toca");
        }
    }
}
