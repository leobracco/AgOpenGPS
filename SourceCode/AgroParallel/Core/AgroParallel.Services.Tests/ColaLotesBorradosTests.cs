// ============================================================================
// ColaLotesBorradosTests.cs — la cola de lotes borrados que esperan aviso al
// cloud, y que mientras tanto hace de tombstone.
//
// El tombstone es lo que impide que el sync REPONGA un lote recién borrado:
// ResolutorLoteCloud devuelve Crear cuando la carpeta no existe, así que sin
// esto el operario borra un lote y le reaparece en el ciclo siguiente.
// ============================================================================

using System.IO;
using AgroParallel.Services.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class ColaLotesBorradosTests
    {
        private string _dir;
        private string _archivo;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pilotx_cola_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            _archivo = Path.Combine(_dir, "lotes_borrados.json");
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void ColaNueva_EstaVacia()
        {
            var c = new ColaLotesBorrados(_archivo);

            Assert.That(c.Pendientes(), Is.Empty);
            Assert.That(c.EstaBorrado("Lote 12"), Is.False);
        }

        [Test]
        public void Encolar_QuedaPendienteYCuentaComoBorrado()
        {
            var c = new ColaLotesBorrados(_archivo);

            c.Encolar("Lote 12");

            Assert.That(c.Pendientes(), Is.EquivalentTo(new[] { "Lote 12" }));
            Assert.That(c.EstaBorrado("Lote 12"), Is.True);
        }

        // Sin esto el tombstone se pierde al reiniciar PilotX y el sync repone
        // el lote borrado.
        [Test]
        public void Encolar_SobreviveAlReinicio()
        {
            new ColaLotesBorrados(_archivo).Encolar("Lote 12");

            var otra = new ColaLotesBorrados(_archivo);

            Assert.That(otra.EstaBorrado("Lote 12"), Is.True);
        }

        [Test]
        public void EncolarDosVeces_NoDuplica()
        {
            var c = new ColaLotesBorrados(_archivo);

            c.Encolar("Lote 12");
            c.Encolar("Lote 12");

            Assert.That(c.Pendientes().Count, Is.EqualTo(1));
        }

        // El cloud confirmó: ya no hay nada que reponer, el tombstone se levanta.
        [Test]
        public void Confirmar_SacaElLoteDeLaCola()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");

            c.Confirmar("Lote 12");

            Assert.That(c.Pendientes(), Is.Empty);
            Assert.That(c.EstaBorrado("Lote 12"), Is.False);
        }

        [Test]
        public void Confirmar_PersisteEnDisco()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");
            c.Confirmar("Lote 12");

            var otra = new ColaLotesBorrados(_archivo);

            Assert.That(otra.EstaBorrado("Lote 12"), Is.False);
        }

        [Test]
        public void Descartar_SacaElLoteIgualQueConfirmar()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");

            c.Descartar("Lote 12");

            Assert.That(c.Pendientes(), Is.Empty);
        }

        [Test]
        public void VariosLotes_SeManejanIndependientes()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");
            c.Encolar("Lote 13");

            c.Confirmar("Lote 12");

            Assert.That(c.EstaBorrado("Lote 12"), Is.False);
            Assert.That(c.EstaBorrado("Lote 13"), Is.True);
        }

        [Test]
        public void NombreConOtraCapitalizacion_CuentaComoElMismoLote()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");

            Assert.That(c.EstaBorrado("LOTE 12"), Is.True);
        }

        // Un archivo corrupto no puede dejar a PilotX sin poder borrar: se
        // arranca con la cola vacía y se sigue.
        [Test]
        public void ArchivoCorrupto_ArrancaVaciaYNoExplota()
        {
            File.WriteAllText(_archivo, "{ esto no es json");

            var c = new ColaLotesBorrados(_archivo);

            Assert.That(c.Pendientes(), Is.Empty);
            Assert.DoesNotThrow(() => c.Encolar("Lote 12"));
            Assert.That(c.EstaBorrado("Lote 12"), Is.True);
        }

        [Test]
        public void NombreVacio_SeIgnora()
        {
            var c = new ColaLotesBorrados(_archivo);

            c.Encolar("");
            c.Encolar(null);

            Assert.That(c.Pendientes(), Is.Empty);
        }
    }
}
