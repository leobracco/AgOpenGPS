// ============================================================================
// ColaLotesBorradosTests.cs — los lotes borrados por el operario: el aviso
// pendiente al cloud y el tombstone que evita que el sync los reponga.
//
// El tombstone es lo que impide que el sync REPONGA un lote recién borrado:
// ResolutorLoteCloud devuelve Crear cuando la carpeta no existe, así que sin
// esto el operario borra un lote y le reaparece en el ciclo siguiente.
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AgroParallel.Common;
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

        // Abandonar el aviso deja de insistirle al cloud, pero NO desprotege
        // el lote: el tombstone tiene que quedar en pie. Antes (Descartar)
        // sacaba las dos cosas, y eso era justamente el bug: agotar
        // reintentos reponía el lote borrado en el próximo ciclo.
        [Test]
        public void AbandonarAviso_SacaElAvisoPeroElTombstoneQueda()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");

            c.AbandonarAviso("Lote 12");

            Assert.That(c.Pendientes(), Is.Empty);
            Assert.That(c.EstaBorrado("Lote 12"), Is.True);
        }

        // El cloud confirma explícitamente: recién ahí se levantan las DOS
        // cosas, aviso y tombstone.
        [Test]
        public void Confirmar_LevantaElAvisoYElTombstone()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");

            c.Confirmar("Lote 12");

            Assert.That(c.Pendientes(), Is.Empty);
            Assert.That(c.EstaBorrado("Lote 12"), Is.False);
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

        // Caso del espejo "(OrbitX)": el operario borra el espejo "Campo Norte
        // (OrbitX)" (nombre de CARPETA), pero el pendiente que baja del cloud
        // llega con "Campo Norte" (nombre CLOUD, sin resolver, ver
        // OrbitXSync.GuardarArchivoDeLote). Si el caller (EngineLotesService)
        // sólo encola el nombre de carpeta, EstaBorrado("Campo Norte") da falso
        // y el sync repone el lote recién borrado. El fix es que el caller
        // encole LOS DOS nombres — este test fija el contrato que necesita para
        // que el arreglo funcione: encolar ambos nombres tiene que proteger a
        // los dos por separado.
        [Test]
        public void EncolarNombreDeCarpetaYNombreCloud_ProtegeALosDos()
        {
            var c = new ColaLotesBorrados(_archivo);

            c.Encolar("Campo Norte (OrbitX)"); // nombre de carpeta (lo que borró el operario)
            c.Encolar("Campo Norte");          // nombre cloud (lo que manda el pendiente sin resolver)

            Assert.That(c.EstaBorrado("Campo Norte (OrbitX)"), Is.True);
            Assert.That(c.EstaBorrado("Campo Norte"), Is.True);
            Assert.That(c.Pendientes(), Is.EquivalentTo(new[] { "Campo Norte (OrbitX)", "Campo Norte" }));
        }

        // Espejo: si el cloud confirma el borrado de UNO de los dos nombres
        // (p.ej. el nombre cloud, que es el que existe del lado del server), el
        // otro (el de carpeta local) puede seguir protegido hasta su propia
        // confirmación — Confirmar es por nombre, no borra "la pareja" sola.
        [Test]
        public void Espejo_ConfirmarUnNombreNoLevantaElTombstoneDelOtro()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Campo Norte (OrbitX)");
            c.Encolar("Campo Norte");

            c.Confirmar("Campo Norte");

            Assert.That(c.EstaBorrado("Campo Norte"), Is.False);
            Assert.That(c.EstaBorrado("Campo Norte (OrbitX)"), Is.True);
        }

        // AtomicJson deja el .bak con la versión anterior recién en el SEGUNDO
        // guardado (el primero crea el archivo, no hay nada previo que
        // respaldar). Con el principal corrupto pero el .bak sano, la cola se
        // recupera del respaldo en vez de arrancar vacía y perder el tombstone.
        [Test]
        public void ArchivoPrincipalCorrupto_SeRecuperaDelBackup()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12"); // 1er guardado: crea el archivo, sin .bak todavía
            c.Encolar("Lote 13"); // 2do guardado: File.Replace deja el .bak = versión anterior (Lote 12)

            string bak = _archivo + AtomicJson.BakSuffix;
            Assert.That(File.Exists(bak), Is.True, "AtomicJson debería haber dejado el .bak en el 2do guardado");

            File.WriteAllText(_archivo, "{ esto no es json"); // corrompemos el principal a mano

            var recuperada = new ColaLotesBorrados(_archivo);

            Assert.That(recuperada.EstaBorrado("Lote 12"), Is.True);
        }

        // Antes de separar aviso/tombstone, el archivo era una lista pelada de
        // strings. Un archivo así (de una versión vieja de PilotX, o restaurado
        // de un backup) tiene que seguir cargando: lo conservador es tratar
        // cada nombre como las DOS cosas, tombstone y aviso pendiente.
        [Test]
        public void ArchivoFormatoViejo_SeCargaComoTombstoneYAvisoPendiente()
        {
            File.WriteAllText(_archivo, JsonSerializer.Serialize(new List<string> { "Lote 12", "Lote 13" }));

            var c = new ColaLotesBorrados(_archivo);

            Assert.That(c.EstaBorrado("Lote 12"), Is.True);
            Assert.That(c.EstaBorrado("Lote 13"), Is.True);
            Assert.That(c.Pendientes(), Is.EquivalentTo(new[] { "Lote 12", "Lote 13" }));
        }
    }

    // ========================================================================
    // Clasificación de la respuesta del cloud al aviso de borrado.
    //
    // De acá salía el bucle que llenaba orbitx_sync.log: el aviso se reintentaba
    // 5 veces contra un 404 que nunca iba a cambiar. Ahora cada código decide en
    // un solo intento si se reintenta, si se abandona o si cuenta como éxito.
    // ========================================================================
    [TestFixture]
    public class ClasificadorAvisoBorradoTests
    {
        [Test]
        public void Ok200_EsConfirmado()
        {
            Assert.That(ClasificadorAvisoBorrado.Clasificar(200, "{\"ok\":true}"),
                Is.EqualTo(ResultadoAvisoBorrado.Confirmado));
        }

        [Test]
        public void Gone410_EsConfirmado()
        {
            Assert.That(ClasificadorAvisoBorrado.Clasificar(410, ""),
                Is.EqualTo(ResultadoAvisoBorrado.Confirmado));
        }

        // El objetivo del aviso era que el lote no esté en el cloud. Si el
        // endpoint contesta que ese lote no existe, el objetivo está cumplido.
        [Test]
        public void Http404_ConCuerpoQueDiceQueElLoteNoEsta_EsConfirmado()
        {
            Assert.That(
                ClasificadorAvisoBorrado.Clasificar(404, "{\"error\":\"lote no encontrado\"}"),
                Is.EqualTo(ResultadoAvisoBorrado.Confirmado));
            Assert.That(
                ClasificadorAvisoBorrado.Clasificar(404, "{\"error\":\"not_found\",\"lote\":\"Expo 20ha\"}"),
                Is.EqualTo(ResultadoAvisoBorrado.Confirmado));
        }

        // El 404 real de orbitx_sync.log: viene del catch-all del server, que
        // responde así para CUALQUIER ruta inexistente. Leerlo como "el lote ya
        // no está" levantaría el tombstone y el sync repondría en el ciclo
        // siguiente el lote que el operario acaba de borrar.
        [Test]
        public void Http404_DelCatchAllDelServer_EsRutaInexistente()
        {
            Assert.That(
                ClasificadorAvisoBorrado.Clasificar(404,
                    "{\"error\":\"Ruta no encontrada\",\"path\":\"/api/aog/lote/borrado\"}"),
                Is.EqualTo(ResultadoAvisoBorrado.RutaInexistente));
        }

        [Test]
        public void Http404_De404HtmlDeExpress_EsRutaInexistente()
        {
            Assert.That(
                ClasificadorAvisoBorrado.Clasificar(404, "<html><body>Cannot POST /api/aog/lote/borrado</body></html>"),
                Is.EqualTo(ResultadoAvisoBorrado.RutaInexistente));
        }

        // Ante la duda NO se confirma: confirmar de más repone lotes borrados.
        [Test]
        public void Http404_SinCuerpoLegible_NoSeConfirma()
        {
            Assert.That(ClasificadorAvisoBorrado.Clasificar(404, null),
                Is.EqualTo(ResultadoAvisoBorrado.RutaInexistente));
            Assert.That(ClasificadorAvisoBorrado.Clasificar(404, "   "),
                Is.EqualTo(ResultadoAvisoBorrado.RutaInexistente));
        }

        // Fallo del lado del server: el tractor tiene que volver a avisar, no
        // dar de baja el aviso.
        [TestCase(500)]
        [TestCase(502)]
        [TestCase(503)]
        [TestCase(504)]
        [TestCase(408)]
        [TestCase(429)]
        public void FallosTransitorios_SeReintentan(int codigo)
        {
            Assert.That(ClasificadorAvisoBorrado.Clasificar(codigo, "<html>502 Bad Gateway</html>"),
                Is.EqualTo(ResultadoAvisoBorrado.Reintentable));
        }

        // Sin red (excepción, sin respuesta): jamás se descarta un aviso. La PC
        // del tractor está en el medio del campo.
        [Test]
        public void SinRespuesta_SeReintenta()
        {
            Assert.That(ClasificadorAvisoBorrado.Clasificar(ClasificadorAvisoBorrado.SinRespuesta, null),
                Is.EqualTo(ResultadoAvisoBorrado.Reintentable));
        }

        // El token se arregla desde el panel sin ir al tractor: el aviso espera.
        [TestCase(401)]
        [TestCase(403)]
        public void ProblemasDeToken_SeReintentan(int codigo)
        {
            Assert.That(ClasificadorAvisoBorrado.Clasificar(codigo, "{\"error\":\"Token requerido\"}"),
                Is.EqualTo(ResultadoAvisoBorrado.Reintentable));
        }

        // Rechazo permanente: el server no va a cambiar de opinión. Se abandona
        // el aviso en el primer intento, no a los cinco.
        [TestCase(400)]
        [TestCase(405)]
        [TestCase(409)]
        [TestCase(422)]
        public void RechazosPermanentes_NoSeReintentan(int codigo)
        {
            Assert.That(ClasificadorAvisoBorrado.Clasificar(codigo, "{\"error\":\"lote invalido\"}"),
                Is.EqualTo(ResultadoAvisoBorrado.Rechazado));
        }

        // Regresión: abandonar el aviso NO puede desproteger el lote. Es la
        // diferencia entre "el cloud no se enteró" y "el lote vuelve solo a la
        // cabina en el ciclo siguiente".
        [Test]
        public void AbandonarElAviso_DejaElTombstoneEnPie()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pilotx_cola_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                var c = new ColaLotesBorrados(Path.Combine(dir, "lotes_borrados.json"));
                c.Encolar("Expo 20ha");

                c.AbandonarAviso("Expo 20ha"); // lo que se hace ante 404 de ruta / 4xx permanente

                Assert.That(c.Pendientes(), Is.Empty, "el aviso deja de reintentarse");
                Assert.That(c.EstaBorrado("Expo 20ha"), Is.True, "pero el lote sigue protegido");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
