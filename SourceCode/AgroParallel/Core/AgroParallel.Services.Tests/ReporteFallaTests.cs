// Tests del "Reportar falla" en un toque.
//
// Lo que NO se negocia y se fija acá:
//   · ningún token ni contraseña sale del equipo (orbitX.json device_token,
//     master_token, NTRIP, clave del wifi, credenciales en URLs RTSP, la API
//     key de AgShare del perfil XML), ni en la config ni repetido en un log;
//   · el ZIP tiene tope (el doc de CouchDB tiene límite) y lo grande se omite
//     DICIÉNDOLO en el manifiesto, nunca en silencio;
//   · el código se puede dictar por teléfono (sin 0/O ni 1/I/L);
//   · la cola en disco sobrevive sin internet y no deja escapar rutas.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AgroParallel.Soporte;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class ReporteFallaTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pilotx_rf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Escribir(string nombre, string contenido)
        {
            string ruta = Path.Combine(_dir, nombre);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta));
            File.WriteAllText(ruta, contenido, new UTF8Encoding(false));
            return ruta;
        }

        private static Dictionary<string, string> LeerZip(byte[] zip)
        {
            var res = new Dictionary<string, string>();
            using (var ms = new MemoryStream(zip))
            using (var z = new ZipArchive(ms, ZipArchiveMode.Read))
                foreach (var e in z.Entries)
                    using (var sr = new StreamReader(e.Open(), Encoding.UTF8))
                        res[e.FullName] = sr.ReadToEnd();
            return res;
        }

        // ── Sanitizador ─────────────────────────────────────────────────────

        [TestCase("device_token", true)]
        [TestCase("master_token", true)]
        [TestCase("user_password", true)]
        [TestCase("Password", true)]
        [TestCase("clave", true)]
        [TestCase("AgShareApiKey", true)]
        [TestCase("ntripPass", true)]
        [TestCase("keya", false)]
        [TestCase("keya_steer_enabled", false)]
        [TestCase("setTram_passes", false)]
        [TestCase("device_id", false)]
        [TestCase("setKey_hotkeys", false)]
        public void EsClaveSecreta(string nombre, bool esperado)
        {
            Assert.That(ReporteFallaSanitizador.EsClaveSecreta(nombre), Is.EqualTo(esperado));
        }

        [Test]
        public void SanitizarJson_TapaSecretosAnidadosYJuntaLosValores()
        {
            string json = "{\"device_id\":\"VX-AB12\",\"device_token\":\"tok-SUPER-secreto-123\","
                        + "\"ntrip\":{\"host\":\"rtk.ar\",\"user\":\"juan\",\"user_password\":\"pw-ntrip-99\"},"
                        + "\"start_pass\":3,"
                        + "\"camaras\":[{\"url\":\"rtsp://admin:camclave77@192.168.5.20:554/s1\"}]}";
            var secretos = new HashSet<string>();
            string s = ReporteFallaSanitizador.SanitizarJson(json, secretos);

            Assert.That(s, Does.Not.Contain("tok-SUPER-secreto-123"));
            Assert.That(s, Does.Not.Contain("pw-ntrip-99"));
            Assert.That(s, Does.Not.Contain("camclave77"));
            Assert.That(s, Does.Contain("VX-AB12"), "lo que no es secreto se conserva");
            Assert.That(s, Does.Contain("rtk.ar"));
            Assert.That(s, Does.Contain("\"start_pass\": 3"), "un número no es un secreto");
            Assert.That(s, Does.Contain(ReporteFallaSanitizador.Oculto));
            Assert.That(secretos, Does.Contain("tok-SUPER-secreto-123"));
            Assert.That(secretos, Does.Contain("pw-ntrip-99"));
            Assert.That(secretos, Does.Contain("camclave77"));
        }

        [Test]
        public void SanitizarJson_Roto_IgualTapaPorPatron()
        {
            var secretos = new HashSet<string>();
            string s = ReporteFallaSanitizador.SanitizarJson("{\"device_token\": \"abcdef123456\", roto", secretos);
            Assert.That(s, Does.Not.Contain("abcdef123456"));
        }

        [Test]
        public void SanitizarXml_PerfilAog_TapaApiKey()
        {
            string xml = "<configuration><userSettings><X>"
                       + "<setting name=\"AgShareApiKey\" serializeAs=\"String\"><value>key-agshare-777</value></setting>"
                       + "<setting name=\"setTram_passes\" serializeAs=\"String\"><value>3</value></setting>"
                       + "</X></userSettings></configuration>";
            var secretos = new HashSet<string>();
            string s = ReporteFallaSanitizador.SanitizarXml(xml, secretos);
            Assert.That(s, Does.Not.Contain("key-agshare-777"));
            Assert.That(s, Does.Contain("<value>3</value>"));
            Assert.That(secretos, Does.Contain("key-agshare-777"));
        }

        [Test]
        public void SanitizarTexto_TapaSecretosConocidosTokenYUrls()
        {
            string log = "12:00 conectando a rtsp://admin:otraclave@10.0.0.2/x\n"
                       + "12:01 NTRIP auth con pw-ntrip-99 fallo\n"
                       + "12:02 header X-Auth-Token: TOKEN-DEL-EQUIPO-XYZ\n";
            string s = ReporteFallaSanitizador.SanitizarTexto(log, new[] { "pw-ntrip-99" }, "TOKEN-DEL-EQUIPO-XYZ");
            Assert.That(s, Does.Not.Contain("otraclave"));
            Assert.That(s, Does.Not.Contain("pw-ntrip-99"));
            Assert.That(s, Does.Not.Contain("TOKEN-DEL-EQUIPO-XYZ"));
            Assert.That(s, Does.Contain("12:01 NTRIP auth"));
        }

        // ── Código ──────────────────────────────────────────────────────────

        [Test]
        public void Codigo_DictablePorTelefono()
        {
            var rnd = new Random(7);
            for (int i = 0; i < 500; i++)
            {
                string c = ReporteFallaArmador.GenerarCodigo(rnd);
                Assert.That(ReporteFallaArmador.CodigoValido(c), Is.True, c);
                Assert.That(c, Does.Match("^RF-[A-Z2-9]{3}-[A-Z2-9]{3}$"));
                foreach (char ch in "01OIL")
                    Assert.That(c.Substring(3), Does.Not.Contain(ch.ToString()), c);
            }
        }

        [TestCase("RF-K7M-4QX", true)]
        [TestCase("rf-k7m-4qx", false)]
        [TestCase("RF-K7M-4Q", false)]
        [TestCase("RF-..\\-abc", false)]
        [TestCase("../../win", false)]
        [TestCase(null, false)]
        public void CodigoValido(string c, bool ok)
        {
            Assert.That(ReporteFallaArmador.CodigoValido(c), Is.EqualTo(ok));
        }

        // ── Armado del ZIP ──────────────────────────────────────────────────

        private EntradaReporteFalla EntradaBase()
        {
            return new EntradaReporteFalla
            {
                Codigo = "RF-K7M-4QX",
                Descripcion = "Se cortó el piloto en la cabecera",
                VersionPilotX = "1.0.89",
                DeviceId = "VX-AB12",
                TokenEquipo = "TOKEN-DEL-EQUIPO-XYZ",
                PerfilActivo = "John Deere 7J",
                LoteNombre = "La Loma",
                Fecha = new DateTime(2026, 10, 3, 9, 30, 0),
                CapturaPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 },
            };
        }

        [Test]
        public void Armar_IncluyeTodoYSinSecretos()
        {
            var e = EntradaBase();
            e.Archivos.Add(new ArchivoReporte("config/orbitX.json",
                Escribir("orbitX.json", "{\"device_id\":\"VX-AB12\",\"device_token\":\"TOKEN-DEL-EQUIPO-XYZ\",\"master_token\":\"MASTER-abc-123\"}"),
                TipoArchivoReporte.Config));
            e.Archivos.Add(new ArchivoReporte("config/corex-integrado.json",
                Escribir("corex-integrado.json", "{\"ntrip\":{\"user_password\":\"pw-ntrip-99\"}}"),
                TipoArchivoReporte.Config));
            e.Archivos.Add(new ArchivoReporte("logs/motor_eventos.txt",
                Escribir("ev.txt", "10:00-> NTRIP pw-ntrip-99 rechazado\n10:01-> master MASTER-abc-123\n"),
                TipoArchivoReporte.Log));
            e.Archivos.Add(new ArchivoReporte("lote/Field.txt", Escribir("La Loma/Field.txt", "$FieldDir\nLa Loma\n"),
                TipoArchivoReporte.Lote));
            e.Archivos.Add(ArchivoReporte.DeTexto("diagnostico/fuentes.txt", "GPS (NMEA): viva"));

            var r = ReporteFallaArmador.Armar(e);
            var z = LeerZip(r.Zip);

            Assert.That(z.Keys, Is.SupersetOf(new[] {
                "reporte.json", "descripcion.txt", "captura.png",
                "config/orbitX.json", "config/corex-integrado.json",
                "logs/motor_eventos.txt", "lote/Field.txt", "diagnostico/fuentes.txt" }));
            Assert.That(z["descripcion.txt"], Does.Contain("Se cortó el piloto"));
            Assert.That(z["reporte.json"], Does.Contain("RF-K7M-4QX"));
            Assert.That(z["reporte.json"], Does.Contain("John Deere 7J"));
            Assert.That(z["reporte.json"], Does.Contain("La Loma"));
            Assert.That(z["reporte.json"], Does.Contain("1.0.89"));

            foreach (var kv in z.Where(k => k.Key != "captura.png"))
            {
                Assert.That(kv.Value, Does.Not.Contain("TOKEN-DEL-EQUIPO-XYZ"), kv.Key);
                Assert.That(kv.Value, Does.Not.Contain("MASTER-abc-123"), kv.Key);
                Assert.That(kv.Value, Does.Not.Contain("pw-ntrip-99"), kv.Key);
            }
            Assert.That(z["config/orbitX.json"], Does.Contain("VX-AB12"));
        }

        [Test]
        public void Armar_LogLargo_SeRecortaALaCola()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 20000; i++) sb.Append("linea ").Append(i).Append('\n');
            sb.Append("ULTIMA LINEA\n");
            var e = EntradaBase();
            e.Archivos.Add(new ArchivoReporte("logs/grande.log", Escribir("grande.log", sb.ToString()),
                TipoArchivoReporte.Log) { MaxBytes = 4096 });

            var z = LeerZip(ReporteFallaArmador.Armar(e).Zip);
            string log = z["logs/grande.log"];
            Assert.That(log, Does.Contain("ULTIMA LINEA"));
            Assert.That(log, Does.Not.Contain("linea 0\n"));
            Assert.That(log, Does.Contain("recortado"));
            Assert.That(log.Length, Is.LessThan(4096 + 300));
        }

        [Test]
        public void Armar_ArchivoDeLoteGrande_SeOmiteYSeDice()
        {
            var e = EntradaBase();
            e.Archivos.Add(new ArchivoReporte("lote/Sections.txt",
                Escribir("Sections.txt", new string('x', 900 * 1024)), TipoArchivoReporte.Lote));
            e.Archivos.Add(new ArchivoReporte("logs/no_existe.log", Path.Combine(_dir, "nada.log"), TipoArchivoReporte.Log));

            var r = ReporteFallaArmador.Armar(e);
            var z = LeerZip(r.Zip);
            Assert.That(z.ContainsKey("lote/Sections.txt"), Is.False);
            Assert.That(r.Omitidos.Any(o => o.Contains("lote/Sections.txt")), Is.True);
            Assert.That(r.Omitidos.Any(o => o.Contains("logs/no_existe.log") && o.Contains("no existe")), Is.True);
            Assert.That(z["reporte.json"], Does.Contain("Sections.txt"));
        }

        [Test]
        public void Armar_SiSePasaDelTope_BajaDeNivel()
        {
            var rnd = new Random(1);
            var e = EntradaBase();
            // Contenido aleatorio: no comprime, así fuerza a bajar de nivel.
            for (int i = 0; i < 4; i++)
            {
                var b = new byte[400 * 1024];
                rnd.NextBytes(b);
                string ruta = Path.Combine(_dir, "lote" + i + ".bin");
                File.WriteAllBytes(ruta, b);
                e.Archivos.Add(new ArchivoReporte("lote/f" + i + ".bin", ruta, TipoArchivoReporte.Lote));
            }
            var r = ReporteFallaArmador.Armar(e, topeBytes: 600 * 1024);
            Assert.That(r.Nivel, Is.GreaterThan(0));
            Assert.That(r.Zip.Length, Is.LessThanOrEqualTo(600 * 1024));
            Assert.That(LeerZip(r.Zip).ContainsKey("reporte.json"), Is.True);
        }

        // ── Cola en disco ───────────────────────────────────────────────────

        [Test]
        public void Cola_GuardaListaYMarcaEnviado()
        {
            var cola = new ColaReportesFalla(Path.Combine(_dir, "cola"));
            cola.Guardar("RF-AAA-222", new byte[] { 1, 2, 3 });
            cola.Guardar("RF-BBB-333", new byte[] { 4 });

            Assert.That(cola.Pendientes(), Is.EquivalentTo(new[] { "RF-AAA-222", "RF-BBB-333" }));
            Assert.That(cola.EstadoDe("RF-AAA-222"), Is.EqualTo(ColaReportesFalla.EnCola));

            cola.MarcarEnviado("RF-AAA-222");
            Assert.That(cola.Pendientes(), Is.EquivalentTo(new[] { "RF-BBB-333" }));
            Assert.That(cola.EstadoDe("RF-AAA-222"), Is.EqualTo(ColaReportesFalla.Subido));
            Assert.That(cola.Leer("RF-AAA-222"), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(cola.EstadoDe("RF-ZZZ-999"), Is.Null);
        }

        [Test]
        public void Cola_RechazaCodigosQueSonRutas()
        {
            var cola = new ColaReportesFalla(Path.Combine(_dir, "cola"));
            Assert.Throws<ArgumentException>(() => cola.Guardar("..\\..\\x", new byte[] { 1 }));
            Assert.That(cola.Leer("../../etc"), Is.Null);
        }

        [Test]
        public void Cola_SobreviveReinicio()
        {
            string dir = Path.Combine(_dir, "cola");
            new ColaReportesFalla(dir).Guardar("RF-CCC-444", new byte[] { 9 });
            Assert.That(new ColaReportesFalla(dir).Pendientes(), Does.Contain("RF-CCC-444"));
        }

        // ── Payload a OrbitX ────────────────────────────────────────────────

        [Test]
        public void PayloadSync_SubtipoPropioNoEsLote()
        {
            var zip = new byte[] { 1, 2, 3, 4 };
            var p = ReporteFallaService.ArmarPayloadSync("RF-K7M-4QX", zip, "VX-AB12", "1.0.89", "Se cortó", 1700000000000);
            Assert.That(p["subtipo"], Is.EqualTo("soporte_reporte"));
            Assert.That(p["es_lote"], Is.EqualTo(false));
            Assert.That(p["ruta_rel"], Is.EqualTo("soporte/RF-K7M-4QX.zip"));
            Assert.That(p["nombre"], Is.EqualTo("RF-K7M-4QX.zip"));
            Assert.That(p["contenido_base64"], Is.EqualTo(Convert.ToBase64String(zip)));
            Assert.That(p["tamano"], Is.EqualTo(4));
            Assert.That(p["hash_md5"], Is.EqualTo("08d6c05a21512a79a1dfeb9d2a8f262f"));
            Assert.That(p["codigo"], Is.EqualTo("RF-K7M-4QX"));
            Assert.That(p.ContainsKey("device_token"), Is.False);
        }

        // ── Pendrive ────────────────────────────────────────────────────────

        [Test]
        public void Pendrive_CopiaElZipALaUnidad()
        {
            var cola = new ColaReportesFalla(Path.Combine(_dir, "cola"));
            cola.Guardar("RF-DDD-555", new byte[] { 5, 5 });
            string usb = Path.Combine(_dir, "usb");
            Directory.CreateDirectory(usb);

            var svc = new ReporteFallaService(cola, () => null, null, null)
            {
                UnidadesExtraibles = () => new[] { usb },
            };
            var r = svc.CopiarAPendrive("RF-DDD-555");
            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(File.Exists(Path.Combine(usb, "PilotX-Reportes", "RF-DDD-555.zip")), Is.True);
        }

        [Test]
        public void Pendrive_SinUnidad_DiceQueFalta()
        {
            var cola = new ColaReportesFalla(Path.Combine(_dir, "cola"));
            cola.Guardar("RF-DDD-555", new byte[] { 5 });
            var svc = new ReporteFallaService(cola, () => null, null, null)
            {
                UnidadesExtraibles = () => new string[0],
            };
            var r = svc.CopiarAPendrive("RF-DDD-555");
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Does.Contain("pendrive"));
        }

        [Test]
        public void Crear_SinVinculacion_QuedaEnColaYDevuelveCodigo()
        {
            var cola = new ColaReportesFalla(Path.Combine(_dir, "cola"));
            var svc = new ReporteFallaService(cola, () => null, () => new EntradaReporteFalla { VersionPilotX = "1.0.89" }, null);
            var r = svc.Crear("no anda el GPS", new byte[] { 0x89, 0x50 },
                new Dictionary<string, string> { { "errores.log", "boom" }, { "..\\..\\malo", "x" } });

            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(ReporteFallaArmador.CodigoValido(r.Codigo), Is.True);
            Assert.That(svc.Estado(r.Codigo), Is.EqualTo(ColaReportesFalla.EnCola));
            var z = LeerZip(cola.Leer(r.Codigo));
            Assert.That(z["descripcion.txt"], Does.Contain("no anda el GPS"));
            Assert.That(z.ContainsKey("pantalla/errores.log"), Is.True);
            Assert.That(z.Keys.Any(k => k.Contains("..")), Is.False);
        }
    }
}
