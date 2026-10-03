// ============================================================================
// ElevacionPartesTests.cs — cómo viaja Elevation.txt a OrbitX.
//
// El sync manda archivos ENTEROS y el server guarda una copia histórica en
// cada cambio de hash: un Elevation.txt de 100.000 puntos (~6 MB) re-subido
// cada 30 s llenaría CouchDB de copias. Por eso viaja en PARTES de N filas:
// las completas no cambian más (se suben una vez), solo la última crece.
// ============================================================================

using System.Linq;
using System.Text;
using AgroParallel.Services.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class ElevacionPartesTests
    {
        private const string Cabecera =
            "2026-October-03 10:00:00 AM\r\n$FieldDir\r\nElevation\r\n$Offsets\r\n0,0\r\nConvergence\r\n0\r\nStartFix\r\n-33.1,-61.7\r\n"
            + "Latitude,Longitude,Elevation,Quality,Easting,Northing,Heading,Roll\r\n";

        private static string Fila(int i) =>
            "-33.1000000,-61.7000000," + (100 + i * 0.01).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",4," + i + ".00,0.00,1.571,0";

        private static string Archivo(int filas, bool ultimaCortada = false)
        {
            var sb = new StringBuilder(Cabecera);
            for (int i = 0; i < filas; i++) sb.Append(Fila(i)).Append("\r\n");
            if (ultimaCortada) sb.Append("-33.1000000,-61.70");
            return sb.ToString();
        }

        [Test]
        public void SoloCabecera_NoHayNadaQueSubir()
        {
            // Lote migrado de AOG: trae Elevation.txt con la cabecera sola.
            Assert.That(ElevacionPartes.Partir(Cabecera, 10), Is.Empty);
            Assert.That(ElevacionPartes.Partir("", 10), Is.Empty);
            Assert.That(ElevacionPartes.Partir(null, 10), Is.Empty);
        }

        [Test]
        public void PartesDeNFilas_LaUltimaIncompleta()
        {
            var partes = ElevacionPartes.Partir(Archivo(25), 10);
            Assert.That(partes.Select(p => p.Filas), Is.EqualTo(new[] { 10, 10, 5 }));
            Assert.That(partes.Select(p => p.Completa), Is.EqualTo(new[] { true, true, false }));
            Assert.That(partes.Select(p => p.Indice), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void CadaParteLlevaLaCabecera_YEsUnElevationTxtValido()
        {
            var partes = ElevacionPartes.Partir(Archivo(12), 10);
            foreach (var p in partes)
            {
                Assert.That(p.Contenido, Does.StartWith(Cabecera));
                Assert.That(p.Contenido, Does.EndWith("\r\n"));
            }
            Assert.That(partes[1].Contenido, Is.EqualTo(Cabecera + Fila(10) + "\r\n" + Fila(11) + "\r\n"));
        }

        [Test]
        public void UnaParteCompleta_NoCambiaCuandoElArchivoCrece()
        {
            // Esto es lo que evita re-subir (y que el server archive) lo ya mandado.
            var antes = ElevacionPartes.Partir(Archivo(15), 10);
            var despues = ElevacionPartes.Partir(Archivo(40), 10);
            Assert.That(despues[0].Contenido, Is.EqualTo(antes[0].Contenido));
        }

        [Test]
        public void UltimaLineaSinTerminar_NoViaja()
        {
            // El sync puede leer justo mientras el motor escribe.
            var partes = ElevacionPartes.Partir(Archivo(3, ultimaCortada: true), 10);
            Assert.That(partes.Single().Filas, Is.EqualTo(3));
        }

        [Test]
        public void NombreDeParte_EsEstableYOrdenable()
        {
            Assert.That(ElevacionPartes.NombreParte(1), Is.EqualTo("Elevation_0001.txt"));
            Assert.That(ElevacionPartes.NombreParte(123), Is.EqualTo("Elevation_0123.txt"));
        }

        [Test]
        public void ArchivoConLfSolo_TambienSeParte()
        {
            string lf = Archivo(3).Replace("\r\n", "\n");
            var partes = ElevacionPartes.Partir(lf, 10);
            Assert.That(partes.Single().Filas, Is.EqualTo(3));
        }
    }
}
