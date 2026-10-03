// ============================================================================
// PuertaControlTests.cs — clasificación de comandos y la puerta que los deja
// pasar (o no) según quién tenga el control.
//
// Lo que se fija acá: que un comando que mueve la máquina NUNCA se clasifique
// como neutro por descuido, que desenganchar el piloto pase siempre, y que el
// modo "solo registro" (el default de hoy) no rompa a los clientes remotos
// que ya accionan (PWA del celular).
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Services.Control;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class PuertaControlTests
    {
        private long _ahora;
        private AutoridadControl _a;
        private List<string> _log;
        private bool _pilotoOn;

        private static readonly ClienteControl Cabina = ClienteControl.Cabina();
        private static readonly ClienteControl Celu = ClienteControl.Remoto("r:celu", "Celular", "192.168.5.23");

        [SetUp]
        public void SetUp()
        {
            _ahora = 50000;
            _a = new AutoridadControl(() => _ahora);
            _log = new List<string>();
            _pilotoOn = false;
        }

        private PuertaControl Puerta(ModoAutoridad modo) =>
            new PuertaControl(_a, modo, s => _log.Add(s), () => _ahora, () => _pilotoOn);

        // ── Clasificación de comandos de guiado ─────────────────────────────

        [TestCase("autosteer")]
        [TestCase("uturn")]
        [TestCase("seccion_3")]
        [TestCase("zona_2")]
        [TestCase("sec_auto")]
        [TestCase("sec_manual")]
        [TestCase("job_start_Lote 1")]
        [TestCase("job_close")]
        [TestCase("shift_north_0.05")]
        [TestCase("offsets_on")]
        [TestCase("ref_volver")]
        [TestCase("uturn_manual_izq")]
        [TestCase("lateral_der")]
        [TestCase("nudge_left")]
        [TestCase("track_next")]
        [TestCase("hidraulico")]
        [TestCase("borrar_aplicado")]
        [TestCase("reset_all")]
        [TestCase("sim_coords_-34.1_-60.2")]
        [TestCase("comando_que_no_existe")]
        public void Comandos_que_mueven_la_maquina_son_accionamiento(string cmd)
        {
            Assert.That(ClasificadorComandos.ClasificarComandoGuiado(cmd, pilotoEnganchado: false),
                Is.EqualTo(NivelComando.Accionamiento));
        }

        [TestCase("modo_banderillero")]
        [TestCase("modo_piloto")]
        [TestCase("tram_vista")]
        [TestCase("  MODO_PILOTO ")]
        public void Comandos_de_solo_pantalla_son_lectura(string cmd)
        {
            Assert.That(ClasificadorComandos.ClasificarComandoGuiado(cmd, false), Is.EqualTo(NivelComando.Lectura));
        }

        [Test]
        public void Autosteer_con_el_piloto_puesto_es_desenganchar_y_pasa_siempre()
        {
            Assert.That(ClasificadorComandos.ClasificarComandoGuiado("autosteer", pilotoEnganchado: true),
                Is.EqualTo(NivelComando.Parada));
        }

        [Test]
        public void Solo_enganchar_el_piloto_cuenta_como_direccion()
        {
            Assert.That(ClasificadorComandos.EsComandoDeDireccion("autosteer", false), Is.True);
            Assert.That(ClasificadorComandos.EsComandoDeDireccion("autosteer", true), Is.False);
            Assert.That(ClasificadorComandos.EsComandoDeDireccion("seccion_1", false), Is.False);
        }

        // ── Clasificación de endpoints ──────────────────────────────────────

        [TestCase("GET", "/aog/state")]
        [TestCase("GET", "/steer/freedrive")]
        [TestCase("POST", "/control/pedir")]
        [TestCase("POST", "/chat/enviar")]
        [TestCase("POST", "/teclado/tecla")]
        [TestCase("POST", "/idioma")]
        [TestCase("POST", "/overlays")]
        [TestCase("POST", "/sistema/brillo")]
        [TestCase("POST", "/flags/add")]
        [TestCase("POST", "/tareas/crear")]
        [TestCase("POST", "/pilotx/update/check")]
        public void Endpoints_neutros(string metodo, string ruta)
        {
            Assert.That(ClasificadorComandos.ClasificarEndpoint(metodo, ruta), Is.EqualTo(NivelComando.Lectura));
        }

        [TestCase("POST", "/steer/freedrive")]
        [TestCase("POST", "/steer/freedrive/angle")]
        [TestCase("POST", "/steer/cal/iniciar")]
        [TestCase("POST", "/steer/cal/latido")]
        [TestCase("POST", "/steer/zero-was")]
        [TestCase("POST", "/quantix/QX-AB12/cmd")]
        [TestCase("POST", "/widget-quantix/manual")]
        [TestCase("POST", "/vistax/calibrar/start")]
        [TestCase("POST", "/lotes/open")]
        [TestCase("POST", "/corex-ecu/motor/test")]
        [TestCase("POST", "/nodos/QX-1/ota")]
        [TestCase("POST", "/sistema/power")]
        [TestCase("POST", "/pilotx/update/apply")]
        [TestCase("PUT", "/tool")]
        [TestCase("POST", "/endpoint/nuevo/que/nadie/clasifico")]
        public void Endpoints_que_accionan(string metodo, string ruta)
        {
            Assert.That(ClasificadorComandos.ClasificarEndpoint(metodo, ruta), Is.EqualTo(NivelComando.Accionamiento));
        }

        [TestCase("POST", "/corex-ecu/motor/stop")]
        [TestCase("DELETE", "/corex-ecu/calibration/pwm-sweep")]
        [TestCase("POST", "/widget-quantix/apagar")]
        public void Endpoints_de_parada_pasan_siempre(string metodo, string ruta)
        {
            Assert.That(ClasificadorComandos.ClasificarEndpoint(metodo, ruta), Is.EqualTo(NivelComando.Parada));
        }

        [Test]
        public void El_comando_de_guiado_lo_decide_el_controller()
        {
            Assert.That(ClasificadorComandos.ClasificarEndpoint("POST", "/aog/guidance/command"),
                Is.EqualTo(NivelComando.PorComando));
        }

        [TestCase("/steer/freedrive", true)]
        [TestCase("/steer/cal/accion", true)]
        [TestCase("/corex-ecu/motor/test", true)]
        [TestCase("/quantix/QX-1/cmd", false)]
        public void Endpoints_de_direccion(string ruta, bool esperado)
        {
            Assert.That(ClasificadorComandos.EsEndpointDeDireccion("POST", ruta), Is.EqualTo(esperado));
        }

        // ── Puerta en modo EXIGIR ───────────────────────────────────────────

        [Test]
        public void Exigir_rechaza_accionamiento_de_un_remoto_sin_control()
        {
            _a.Latido(Cabina);
            var d = Puerta(ModoAutoridad.Exigir).Autorizar(Celu, NivelComando.Accionamiento, "seccion_1");
            Assert.That(d.Permitido, Is.False);
            Assert.That(d.StatusHttp, Is.EqualTo(423));
            Assert.That(d.Motivo, Does.Contain("no tiene el control"));
        }

        [Test]
        public void Exigir_deja_leer_y_parar_a_cualquiera()
        {
            var p = Puerta(ModoAutoridad.Exigir);
            Assert.That(p.Autorizar(Celu, NivelComando.Lectura, "x").Permitido, Is.True);
            Assert.That(p.Autorizar(Celu, NivelComando.Parada, "autosteer").Permitido, Is.True);
        }

        [Test]
        public void Exigir_deja_accionar_al_remoto_con_control()
        {
            _a.Pedir(Celu); // cabina ausente → concedido
            var d = Puerta(ModoAutoridad.Exigir).Autorizar(Celu, NivelComando.Accionamiento, "seccion_1");
            Assert.That(d.Permitido, Is.True);
        }

        [Test]
        public void La_cabina_que_acciona_retoma_el_control()
        {
            _a.Pedir(Celu);
            var d = Puerta(ModoAutoridad.Exigir).Autorizar(Cabina, NivelComando.Accionamiento, "seccion_1");
            Assert.That(d.Permitido, Is.True);
            Assert.That(_a.Estado().DuenoEsCabina, Is.True);
            Assert.That(_a.TieneControl(Celu), Is.False);
        }

        [Test]
        public void Remoto_que_engancha_el_piloto_queda_marcado_para_el_desenganche()
        {
            var revs = new List<RevocacionControl>();
            _a.Revocado += r => revs.Add(r);
            _a.Pedir(Celu);
            Puerta(ModoAutoridad.Exigir).Autorizar(Celu, NivelComando.Accionamiento, "autosteer", esDireccion: true);
            _ahora += AutoridadControl.VencimientoMs + 1;
            _a.Barrer();
            Assert.That(revs[0].DireccionAccionada, Is.True);
        }

        [Test]
        public void Recibir_el_control_con_el_piloto_puesto_hace_responsable_al_remoto()
        {
            // La cabina cede con el piloto andando: si el celular se cae, el
            // piloto no puede seguir sin nadie mirando.
            var revs = new List<RevocacionControl>();
            _a.Revocado += r => revs.Add(r);
            _pilotoOn = true;
            var p = Puerta(ModoAutoridad.Exigir);
            _a.Latido(Cabina);
            p.Pedir(Celu);
            _a.Latido(Cabina);
            Assert.That(p.Ceder(), Is.True);
            _ahora += AutoridadControl.VencimientoMs + 1;
            _a.Barrer();
            Assert.That(revs[0].DireccionAccionada, Is.True);
        }

        // ── Puerta en modo SOLO REGISTRO (default) ──────────────────────────

        [Test]
        public void Solo_registro_deja_pasar_pero_anota_quien_acciono()
        {
            _a.Latido(Cabina);
            var p = Puerta(ModoAutoridad.SoloRegistro);
            var d = p.Autorizar(Celu, NivelComando.Accionamiento, "POST /quantix/QX-1/cmd");
            Assert.That(d.Permitido, Is.True);
            Assert.That(d.SoloRegistrado, Is.True);
            Assert.That(_log, Has.Count.EqualTo(1));
            Assert.That(_log[0], Does.Contain("192.168.5.23").And.Contain("/quantix/QX-1/cmd"));
            Assert.That(p.UltimaAccionRemota, Is.Not.Null);
            Assert.That(p.UltimaAccionRemota.Ip, Is.EqualTo("192.168.5.23"));
        }

        [Test]
        public void Solo_registro_no_repite_el_mismo_renglon_en_rafaga()
        {
            var p = Puerta(ModoAutoridad.SoloRegistro);
            for (int i = 0; i < 10; i++) { p.Autorizar(Celu, NivelComando.Accionamiento, "POST /steer/cal/latido"); _ahora += 300; }
            Assert.That(_log, Has.Count.EqualTo(1));
            _ahora += PuertaControl.DedupLogMs;
            p.Autorizar(Celu, NivelComando.Accionamiento, "POST /steer/cal/latido");
            Assert.That(_log, Has.Count.EqualTo(2));
        }

        [Test]
        public void La_cabina_no_ensucia_el_registro()
        {
            Puerta(ModoAutoridad.SoloRegistro).Autorizar(Cabina, NivelComando.Accionamiento, "seccion_1");
            Assert.That(_log, Is.Empty);
        }

        [Test]
        public void Lectura_remota_no_se_registra()
        {
            Puerta(ModoAutoridad.SoloRegistro).Autorizar(Celu, NivelComando.Lectura, "GET /aog/state");
            Assert.That(_log, Is.Empty);
        }
    }
}
