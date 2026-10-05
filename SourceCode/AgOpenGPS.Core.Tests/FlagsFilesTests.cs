// ============================================================================
// FlagsFilesTests — Flags.txt con TIPO de bandera, sin romper lo que ya hay.
//
// El Flags.txt viaja: se sincroniza a OrbitX, se exporta, y lo leen versiones
// viejas de PilotX y el AOG original. Reglas que se fijan aca:
//   · bandera SIN tipo → la linea queda EXACTAMENTE como antes (8 campos);
//   · bandera CON tipo → un 9no campo "tipo=<codigo>" al FINAL, que un lector
//     viejo ignora (solo mira hasta words[7]);
//   · archivos viejos (7 u 8 campos) se siguen leyendo, con tipo vacio;
//   · notas con comas ya no se cortan en la primera coma, y una nota no se
//     confunde con el tipo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using AgOpenGPS;
using AgOpenGPS.IO;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class FlagsFilesTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pilotx-flags-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Ruta => Path.Combine(_dir, "Flags.txt");

        private static CFlag Bandera(double lat, int id, string notas = "", string tipo = "", int color = 0)
            => new CFlag(lat, -60.5, 10.25, 20.5, 1.5, color, id, notas) { kind = tipo };

        [Test]
        public void SinTipo_LaLineaQuedaIgualQueAntes()
        {
            FlagsFiles.Save(_dir, new List<CFlag> { Bandera(-33.1, 1, "pozo") });

            var lineas = File.ReadAllLines(Ruta);
            Assert.That(lineas[0], Is.EqualTo("$Flags"));
            Assert.That(lineas[1], Is.EqualTo("1"));
            Assert.That(lineas[2], Is.EqualTo("-33.1,-60.5,10.25,20.5,1.5,0,1,pozo"));
        }

        [Test]
        public void ConTipo_AgregaNovenoCampoAlFinal()
        {
            FlagsFiles.Save(_dir, new List<CFlag> { Bandera(-33.1, 2, "el grande", "arbol", 1) });

            var linea = File.ReadAllLines(Ruta)[2];
            Assert.That(linea, Is.EqualTo("-33.1,-60.5,10.25,20.5,1.5,1,2,el grande,tipo=arbol"));

            // Lo que hace un lector viejo (FlagsFiles de antes / AOG): words[7].
            var words = linea.Split(',');
            Assert.That(words[5], Is.EqualTo("1"));
            Assert.That(words[7], Is.EqualTo("el grande"));
        }

        [Test]
        public void IdaYVuelta_ConservaTipoYNotas()
        {
            FlagsFiles.Save(_dir, new List<CFlag>
            {
                Bandera(-33.1, 1, "pozo"),
                Bandera(-33.2, 2, "", "molino", 2),
                Bandera(-33.3, 3, "ojo, tiene alambre", "piedra"),
            });

            var leidas = FlagsFiles.Load(_dir);

            Assert.That(leidas, Has.Count.EqualTo(3));
            Assert.That(leidas[0].kind, Is.EqualTo(""));
            Assert.That(leidas[0].notes, Is.EqualTo("pozo"));
            Assert.That(leidas[1].kind, Is.EqualTo("molino"));
            Assert.That(leidas[1].notes, Is.EqualTo(""));
            Assert.That(leidas[1].color, Is.EqualTo(2));
            Assert.That(leidas[2].kind, Is.EqualTo("piedra"));
            Assert.That(leidas[2].notes, Is.EqualTo("ojo, tiene alambre"));
            Assert.That(leidas[2].ID, Is.EqualTo(3));
        }

        [Test]
        public void ArchivoViejoDeOchoCampos_SeLeeSinTipo()
        {
            File.WriteAllLines(Ruta, new[]
            {
                "$Flags", "1", "-33.1,-60.5,10.25,20.5,1.5,2,7,alambrado caido",
            });

            var f = FlagsFiles.Load(_dir)[0];
            Assert.That(f.kind, Is.EqualTo(""));
            Assert.That(f.color, Is.EqualTo(2));
            Assert.That(f.ID, Is.EqualTo(7));
            Assert.That(f.notes, Is.EqualTo("alambrado caido"));
        }

        [Test]
        public void ArchivoMuyViejoDeSieteCampos_SeLeeSinTipo()
        {
            // Formato sin heading: lat,lon,e,n,color,id,(nada)
            File.WriteAllLines(Ruta, new[] { "$Flags", "1", "-33.1,-60.5,10.25,20.5,1,4" });

            var f = FlagsFiles.Load(_dir)[0];
            Assert.That(f.kind, Is.EqualTo(""));
            Assert.That(f.color, Is.EqualTo(1));
            Assert.That(f.ID, Is.EqualTo(4));
        }

        [Test]
        public void NotaQueEmpiezaConTipo_SinCampoExtra_NoSeConfundeConElTipo()
        {
            // 8 campos: lo ultimo es la NOTA aunque diga "tipo=...".
            File.WriteAllLines(Ruta, new[] { "$Flags", "1", "-33.1,-60.5,10.25,20.5,1.5,0,1,tipo=arbol" });

            var f = FlagsFiles.Load(_dir)[0];
            Assert.That(f.kind, Is.EqualTo(""));
            Assert.That(f.notes, Is.EqualTo("tipo=arbol"));
        }

        [Test]
        public void TipoConComaOEspacios_NoRompeElCsv()
        {
            FlagsFiles.Save(_dir, new List<CFlag> { Bandera(-33.1, 1, "", " Tipo,Raro ") });

            var linea = File.ReadAllLines(Ruta)[2];
            Assert.That(linea.Split(',').Length, Is.EqualTo(9));
            Assert.That(FlagsFiles.Load(_dir)[0].kind, Is.EqualTo("tiporaro"));
        }
    }
}
