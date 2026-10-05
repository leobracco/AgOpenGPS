// ============================================================================
// VeredictoUpdaterTests.cs — PilotX no se cierra hasta saber qué dijo el
// AgroParallel.Updater.
//
// Caso real (pantalla de Francisco Barbero): 8 intentos de actualizar, todos
// vueltos a la misma versión. ApplyAsync lanzaba el Updater y se cerraba 1,5 s
// después sin esperar; el Updater rechazaba el parche (return 3) con PilotX ya
// cerrado, el vigilante lo relanzaba y la pantalla quedaba en "Aplicando".
//
// ResultadoArchivo.cs del Updater se compila linkeado acá: los dos lados del
// archivo updater-resultado.json se prueban juntos.
// ============================================================================

using System;
using System.Threading.Tasks;
using AgroParallel.OrbitX;
using AgroParallel.Updater;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class VeredictoUpdaterTests
    {
        private static readonly TimeSpan Gracia = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

        private static VeredictoUpdater V(string estado, string motivo = null) =>
            new VeredictoUpdater { Estado = estado, Motivo = motivo };

        // ---- Formato del archivo: lo que escribe el Updater, PilotX lo lee ----

        [Test]
        public void Archivo_LoQueEscribeElUpdater_PilotXLoEntiende()
        {
            string motivo = "Este parche es para equipos entre la 1.0.80 y la 1.0.89, y este tiene la 1.0.78. "
                          + "Ruta \"C:\\PilotX\\x\"\nsegunda línea";
            string json = ResultadoArchivo.Armar("rechazado", motivo, 3, @"C:\PilotX\AgroParallel\Updates\1.0.89\payload.zip");

            var v = VeredictoUpdater.Parsear(json);

            Assert.That(v, Is.Not.Null);
            Assert.That(v.Estado, Is.EqualTo(VeredictoUpdater.Rechazado));
            Assert.That(v.Motivo, Is.EqualTo(motivo));
            Assert.That(v.Codigo, Is.EqualTo(3));
        }

        [TestCase("")]
        [TestCase("{\"estado\":\"aplic")]   // a medio escribir
        [TestCase("[1,2]")]
        [TestCase("{\"motivo\":\"sin estado\"}")]
        public void Archivo_Ilegible_EsComoSiNoHubiera(string texto)
        {
            Assert.That(VeredictoUpdater.Parsear(texto), Is.Null);
        }

        // ---- Decidir ----

        [Test]
        public void Decidir_Aplicando_Cierra()
        {
            Assert.That(ReglasVeredicto.Decidir(V("aplicando"), false, TimeSpan.FromSeconds(1), Gracia, Max),
                Is.EqualTo(DecisionApply.Cerrar));
        }

        [Test]
        public void Decidir_Rechazado_NoCierra()
        {
            Assert.That(ReglasVeredicto.Decidir(V("rechazado", "x"), false, TimeSpan.FromSeconds(1), Gracia, Max),
                Is.EqualTo(DecisionApply.Abortar));
        }

        [Test]
        public void Decidir_UpdaterQueTermino_NoCierra_AunqueHayaDichoAplicando()
        {
            // Un Updater que instala se queda esperando que PilotX salga. Si ya
            // salió, no va a instalar: cerrar solo reinicia en la misma versión.
            Assert.That(ReglasVeredicto.Decidir(null, true, TimeSpan.FromSeconds(1), Gracia, Max),
                Is.EqualTo(DecisionApply.Abortar));
            Assert.That(ReglasVeredicto.Decidir(V("aplicando"), true, TimeSpan.FromSeconds(1), Gracia, Max),
                Is.EqualTo(DecisionApply.Abortar));
        }

        [Test]
        public void Decidir_UpdaterViejoSinArchivo_EsperaLaGraciaYDespuesCierra()
        {
            Assert.That(ReglasVeredicto.Decidir(null, false, TimeSpan.FromSeconds(2), Gracia, Max),
                Is.EqualTo(DecisionApply.Esperar));
            Assert.That(ReglasVeredicto.Decidir(null, false, TimeSpan.FromSeconds(9), Gracia, Max),
                Is.EqualTo(DecisionApply.Cerrar));
        }

        [Test]
        public void Decidir_Validando_EsperaHastaElTope()
        {
            // Con el Updater nuevo la gracia corta no aplica: dijo que está validando.
            Assert.That(ReglasVeredicto.Decidir(V("validando"), false, TimeSpan.FromSeconds(30), Gracia, Max),
                Is.EqualTo(DecisionApply.Esperar));
            Assert.That(ReglasVeredicto.Decidir(V("validando"), false, TimeSpan.FromSeconds(61), Gracia, Max),
                Is.EqualTo(DecisionApply.Cerrar));
        }

        // ---- Motivo ----

        private const string LogDosCorridas =
            "[10:00:00] ==== AgroParallel.Updater iniciando ====\r\n" +
            "[10:00:00] PARCHE RECHAZADO: motivo VIEJO de otra corrida\r\n" +
            "[11:00:00] ==== AgroParallel.Updater iniciando ====\r\n" +
            "[11:00:00] pid=1234 zip=C:\\PilotX\\AgroParallel\\Updates\\1.0.89\\payload.zip\r\n" +
            "[11:00:01] PARCHE RECHAZADO: Este parche es para equipos entre la 1.0.80 y la 1.0.89, y este tiene la 1.0.78.\r\n";

        [Test]
        public void Motivo_SaleDeLaUltimaCorridaDelLog()
        {
            Assert.That(ReglasVeredicto.MotivoDesdeLog(LogDosCorridas),
                Is.EqualTo("Este parche es para equipos entre la 1.0.80 y la 1.0.89, y este tiene la 1.0.78."));
        }

        [Test]
        public void Motivo_ElDelVeredictoGanaAlDelLog()
        {
            Assert.That(ReglasVeredicto.MotivoAborto(V("rechazado", "del archivo"), 3, LogDosCorridas),
                Is.EqualTo("del archivo"));
        }

        [Test]
        public void Motivo_SinNadaMas_UsaElCodigo()
        {
            Assert.That(ReglasVeredicto.MotivoAborto(null, 3, null), Does.Contain("código 3"));
            Assert.That(ReglasVeredicto.MotivoAborto(null, 7, ""), Does.Contain("código 7"));
            Assert.That(ReglasVeredicto.MotivoAborto(null, null, null), Is.Not.Empty);
        }

        // ---- Espera completa ----

        [Test]
        public async Task Esperar_UpdaterViejoQueRechaza_NoCierraYTraeElMotivoDelLog()
        {
            // El Updater instalado hoy en las pantallas no escribe el archivo
            // (no se actualiza a sí mismo): sale con 3 y deja el motivo en el log.
            var r = await ReglasVeredicto.EsperarAsync(
                leerResultado: () => null,
                termino: () => true,
                codigoSalida: () => 3,
                leerLog: () => LogDosCorridas,
                graciaSinArchivo: Gracia, esperaMaxima: Max, paso: TimeSpan.FromMilliseconds(10));

            Assert.That(r.Decision, Is.EqualTo(DecisionApply.Abortar));
            Assert.That(r.Motivo, Does.StartWith("Este parche es para equipos entre la 1.0.80"));
        }

        [Test]
        public async Task Esperar_UpdaterNuevoQueRechaza_NoCierraAunqueSigaVivo()
        {
            string json = ResultadoArchivo.Armar("rechazado", "no sirve para esta versión", 3, "z");
            var r = await ReglasVeredicto.EsperarAsync(() => json, () => false, () => null, () => null,
                Gracia, Max, TimeSpan.FromMilliseconds(10));

            Assert.That(r.Decision, Is.EqualTo(DecisionApply.Abortar));
            Assert.That(r.Motivo, Is.EqualTo("no sirve para esta versión"));
        }

        [Test]
        public async Task Esperar_ValidandoYDespuesAplicando_Cierra()
        {
            int lecturas = 0;
            string validando = ResultadoArchivo.Armar("validando", "", 0, "z");
            string aplicando = ResultadoArchivo.Armar("aplicando", "", 0, "z");
            var r = await ReglasVeredicto.EsperarAsync(
                () => ++lecturas < 4 ? validando : aplicando, () => false, () => null, () => null,
                TimeSpan.FromMilliseconds(1), Max, TimeSpan.FromMilliseconds(5));

            Assert.That(r.Decision, Is.EqualTo(DecisionApply.Cerrar));
            Assert.That(r.PorTiempo, Is.False, "cerró por el veredicto, no por la gracia");
        }

        [Test]
        public async Task Esperar_UpdaterViejoQueValida_CierraPasadaLaGracia()
        {
            var r = await ReglasVeredicto.EsperarAsync(() => null, () => false, () => null, () => null,
                TimeSpan.FromMilliseconds(50), Max, TimeSpan.FromMilliseconds(10));

            Assert.That(r.Decision, Is.EqualTo(DecisionApply.Cerrar));
            Assert.That(r.PorTiempo, Is.True);
        }

        [Test]
        public async Task Esperar_UnaFuenteQueTira_NoRompeLaEspera()
        {
            var r = await ReglasVeredicto.EsperarAsync(
                () => throw new System.IO.IOException("bloqueado"), () => true, () => throw new InvalidOperationException(),
                () => throw new System.IO.IOException("bloqueado"),
                Gracia, Max, TimeSpan.FromMilliseconds(10));

            Assert.That(r.Decision, Is.EqualTo(DecisionApply.Abortar));
            Assert.That(r.Motivo, Is.Not.Empty);
        }

        // ---- Al volver a arrancar ----

        [Test]
        public void Marca_IdaYVuelta()
        {
            var m = new MarcaAplicacion { VersionObjetivo = "1.0.89", VersionAnterior = "1.0.78", Ts = 123 };
            var l = MarcaAplicacion.Parsear(m.Armar());

            Assert.That(l.VersionObjetivo, Is.EqualTo("1.0.89"));
            Assert.That(l.VersionAnterior, Is.EqualTo("1.0.78"));
            Assert.That(l.Ts, Is.EqualTo(123));
        }

        [Test]
        public void Arrancar_ConLaVersionNueva_EsOk()
        {
            var m = new MarcaAplicacion { VersionObjetivo = "1.0.89", VersionAnterior = "1.0.78" };
            var r = ReglasVeredicto.EvaluarAlArrancar(m, null, null, "1.0.89");

            Assert.That(r.Ok, Is.True);
            Assert.That(r.Motivo, Is.Null);
        }

        [Test]
        public void Arrancar_EnLaMismaVersion_EsFallaConElMotivoDelUpdater()
        {
            var m = new MarcaAplicacion { VersionObjetivo = "1.0.89", VersionAnterior = "1.0.78" };
            string json = ResultadoArchivo.Armar("fallo", "Falló la extracción: acceso denegado", 1, "z");

            var r = ReglasVeredicto.EvaluarAlArrancar(m, VeredictoUpdater.Parsear(json), LogDosCorridas, "1.0.78");

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Motivo, Is.EqualTo("Falló la extracción: acceso denegado"));
        }

        [Test]
        public void Arrancar_EnLaMismaVersion_SinVeredicto_UsaElLogODiceQuePaso()
        {
            var m = new MarcaAplicacion { VersionObjetivo = "1.0.89", VersionAnterior = "1.0.78" };

            var conLog = ReglasVeredicto.EvaluarAlArrancar(m, null, LogDosCorridas, "1.0.78");
            Assert.That(conLog.Motivo, Does.StartWith("Este parche es para equipos"));

            var sinNada = ReglasVeredicto.EvaluarAlArrancar(m, null, null, "1.0.78");
            Assert.That(sinNada.Ok, Is.False);
            Assert.That(sinNada.Motivo, Does.Contain("1.0.78").And.Contain("1.0.89"));
        }

        [Test]
        public void EsParche_DistingueParcheDeCompleto()
        {
            // Si el Updater rechaza un PARCHE, PilotX lo borra y el próximo
            // Descargar baja el completo: reintentar con el mismo parche fue lo
            // que se comió los 8 intentos.
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pilotx_esparche_" + System.IO.Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string parche = System.IO.Path.Combine(dir, "parche.zip");
                string completo = System.IO.Path.Combine(dir, "completo.zip");
                using (var z = System.IO.Compression.ZipFile.Open(parche, System.IO.Compression.ZipArchiveMode.Create))
                    using (var w = new System.IO.StreamWriter(z.CreateEntry("parche.json").Open()))
                        w.Write("{\"version_base\":\"1.0.80\",\"version_nueva\":\"1.0.89\"}");
                using (var z = System.IO.Compression.ZipFile.Open(completo, System.IO.Compression.ZipArchiveMode.Create))
                    using (var w = new System.IO.StreamWriter(z.CreateEntry("Desktop/PilotX.Desktop.dll").Open()))
                        w.Write("x");

                Assert.That(PilotXSelfUpdate.EsParche(parche), Is.True);
                Assert.That(PilotXSelfUpdate.EsParche(completo), Is.False);
                Assert.That(PilotXSelfUpdate.EsParche(System.IO.Path.Combine(dir, "no-existe.zip")), Is.False);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void Arrancar_SinMarca_NoHayNadaQueReportar()
        {
            Assert.That(ReglasVeredicto.EvaluarAlArrancar(null, null, null, "1.0.89"), Is.Null);
        }
    }
}
