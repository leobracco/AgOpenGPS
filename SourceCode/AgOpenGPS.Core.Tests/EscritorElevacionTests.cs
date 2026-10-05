// ============================================================================
// EscritorElevacionTests.cs — Elevation.txt del lote (formato heredado de AOG).
//
// Fijan: cabecera compatible con la de AOG (los lotes migrados traen el
// archivo con la cabecera sola), append con buffer (no se escribe disco por
// fix), conteo de puntos al continuar un archivo existente, y que un fallo de
// disco no tire excepciones al pipeline de fix.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class EscritorElevacionTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pilotx_elev_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Archivo => Path.Combine(_dir, "Elevation.txt");

        private static FilaElevacion Fila(double alt, double e = 1, double n = 2) => new FilaElevacion
        {
            Latitud = -33.1234567,
            Longitud = -61.7654321,
            AlturaSuelo = alt,
            Calidad = 4,
            Easting = e,
            Northing = n,
            RumboRad = 1.5,
            RolidoGrados = 0.4,
        };

        [Test]
        public void PrimerPunto_CreaLaCabeceraDeAog()
        {
            var w = new EscritorElevacion();
            w.Abrir(_dir, new Wgs84(-33.1, -61.7));
            w.Agregar(Fila(100.123));
            w.Flush();

            var lineas = File.ReadAllLines(Archivo);
            Assert.That(lineas[1], Is.EqualTo("$FieldDir"));
            Assert.That(lineas[2], Is.EqualTo("Elevation"));
            Assert.That(lineas[3], Is.EqualTo("$Offsets"));
            Assert.That(lineas[7], Is.EqualTo("StartFix"));
            Assert.That(lineas[8], Is.EqualTo("-33.1,-61.7"));
            Assert.That(lineas[9], Is.EqualTo(ElevationFiles.ColumnHeader));
            Assert.That(lineas.Length, Is.EqualTo(11));
            Assert.That(lineas[10], Does.StartWith("-33.1234567,-61.7654321,100.123,4,"));
        }

        [Test]
        public void Fila_SinSeparadorDeMiles_YConPuntoDecimal()
        {
            // AOG escribía easting/northing con "N2", que mete coma de miles
            // ("1,234.56") y rompe el CSV a partir de 1 km del origen.
            string s = ElevationFiles.FormatearFila(Fila(100.5, e: 1234.567, n: -2345.678));
            var campos = s.Split(',');
            Assert.That(campos.Length, Is.EqualTo(8), s);
            Assert.That(campos[4], Is.EqualTo("1234.57"));
            Assert.That(campos[5], Is.EqualTo("-2345.68"));
        }

        [Test]
        public void Fila_FormateaIgualConCulturaConComaDecimal()
        {
            var antes = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("es-AR");
                string s = ElevationFiles.FormatearFila(Fila(100.5));
                Assert.That(s.Split(',').Length, Is.EqualTo(8), s);
                Assert.That(s, Does.Contain("100.5"));
            }
            finally { CultureInfo.CurrentCulture = antes; }
        }

        [Test]
        public void Agregar_NoEscribeDiscoHastaElFlush()
        {
            var w = new EscritorElevacion { FilasPorFlush = 1000, SegundosPorFlush = 3600 };
            w.Abrir(_dir, new Wgs84(0, 0));
            w.Agregar(Fila(100));
            w.Agregar(Fila(101));
            Assert.That(File.Exists(Archivo), Is.False, "nada en disco todavía");
            Assert.That(w.Puntos, Is.EqualTo(2), "pero la cuenta del lote ya los incluye");
            w.Flush();
            Assert.That(File.ReadAllLines(Archivo).Length, Is.EqualTo(12));
        }

        [Test]
        public void Agregar_FlusheaSoloAlLlegarAlLimiteDeFilas()
        {
            var w = new EscritorElevacion { FilasPorFlush = 3, SegundosPorFlush = 3600 };
            w.Abrir(_dir, new Wgs84(0, 0));
            w.Agregar(Fila(1)); w.Agregar(Fila(2));
            Assert.That(File.Exists(Archivo), Is.False);
            w.Agregar(Fila(3));
            Assert.That(File.ReadAllLines(Archivo).Length, Is.EqualTo(13));
        }

        [Test]
        public void ArchivoDeAogConSoloCabecera_SeContinuaSinReescribirla()
        {
            // Lote migrado de AOG: Elevation.txt con la cabecera de AOG y cero filas.
            ElevationFiles.CreateHeader(_dir, new DateTime(2024, 3, 1), new Wgs84(-34, -60));
            string cabeceraAog = File.ReadAllText(Archivo);

            var w = new EscritorElevacion();
            w.Abrir(_dir, new Wgs84(-33, -61));
            Assert.That(w.Puntos, Is.EqualTo(0));
            w.Agregar(Fila(100));
            w.Cerrar();

            string todo = File.ReadAllText(Archivo);
            Assert.That(todo, Does.StartWith(cabeceraAog), "la cabecera original (y su StartFix) no se toca");
            Assert.That(File.ReadAllLines(Archivo).Length, Is.EqualTo(11));
        }

        [Test]
        public void ArchivoConPuntos_CuentaLosExistentesYAgrega()
        {
            var w1 = new EscritorElevacion();
            w1.Abrir(_dir, new Wgs84(0, 0));
            for (int i = 0; i < 5; i++) w1.Agregar(Fila(100 + i));
            w1.Cerrar();

            var w2 = new EscritorElevacion();
            w2.Abrir(_dir, new Wgs84(0, 0));
            Assert.That(w2.Puntos, Is.EqualTo(5));
            w2.Agregar(Fila(200));
            w2.Cerrar();
            Assert.That(ElevationFiles.ContarPuntos(Archivo), Is.EqualTo(6));
        }

        [Test]
        public void ArchivoSinSaltoDeLineaFinal_NoPegaLaFilaNueva()
        {
            File.WriteAllText(Archivo, "x\n$FieldDir\nElevation\n$Offsets\n0,0\nConvergence\n0\nStartFix\n0,0\n"
                + ElevationFiles.ColumnHeader + "\n1,2,3,4,5,6,7,8");
            var w = new EscritorElevacion();
            w.Abrir(_dir, new Wgs84(0, 0));
            Assert.That(w.Puntos, Is.EqualTo(1));
            w.Agregar(Fila(100));
            w.Cerrar();
            var lineas = File.ReadAllLines(Archivo);
            Assert.That(lineas[10], Is.EqualTo("1,2,3,4,5,6,7,8"));
            Assert.That(lineas[11], Does.StartWith("-33.1234567,"));
        }

        [Test]
        public void ArchivoVacio_RecibeCabecera()
        {
            File.WriteAllText(Archivo, "");
            var w = new EscritorElevacion();
            w.Abrir(_dir, new Wgs84(0, 0));
            w.Agregar(Fila(100));
            w.Cerrar();
            Assert.That(File.ReadAllLines(Archivo)[9], Is.EqualTo(ElevationFiles.ColumnHeader));
        }

        [Test]
        public void FalloDeDisco_NoTira_YReintentaDespues()
        {
            var w = new EscritorElevacion { FilasPorFlush = 1 };
            string bloqueado = Path.Combine(_dir, "lote");
            Directory.CreateDirectory(bloqueado);
            w.Abrir(bloqueado, new Wgs84(0, 0));
            // Un DIRECTORIO con el nombre del archivo: cualquier escritura falla.
            Directory.CreateDirectory(Path.Combine(bloqueado, "Elevation.txt"));

            Assert.DoesNotThrow(() => w.Agregar(Fila(100)));
            Assert.That(w.UltimoError, Is.Not.Null);
            Assert.That(w.Pendientes, Is.EqualTo(1), "la fila queda en el buffer");

            Directory.Delete(Path.Combine(bloqueado, "Elevation.txt"));
            w.Agregar(Fila(101));
            Assert.That(w.Pendientes, Is.EqualTo(0));
            Assert.That(ElevationFiles.ContarPuntos(Path.Combine(bloqueado, "Elevation.txt")), Is.EqualTo(2));
        }

        [Test]
        public void SinAbrir_AgregarNoHaceNada()
        {
            var w = new EscritorElevacion();
            Assert.DoesNotThrow(() => w.Agregar(Fila(100)));
            Assert.DoesNotThrow(() => w.Cerrar());
            Assert.That(w.Puntos, Is.EqualTo(0));
        }

        [Test]
        public void AbrirOtroLote_FlusheaElAnterior()
        {
            var w = new EscritorElevacion { FilasPorFlush = 1000, SegundosPorFlush = 3600 };
            w.Abrir(_dir, new Wgs84(0, 0));
            w.Agregar(Fila(100));
            string otro = Path.Combine(_dir, "otro");
            Directory.CreateDirectory(otro);
            w.Abrir(otro, new Wgs84(0, 0));
            Assert.That(ElevationFiles.ContarPuntos(Archivo), Is.EqualTo(1));
            Assert.That(w.Puntos, Is.EqualTo(0));
            Assert.That(w.Directorio, Is.EqualTo(otro));
        }
    }
}
