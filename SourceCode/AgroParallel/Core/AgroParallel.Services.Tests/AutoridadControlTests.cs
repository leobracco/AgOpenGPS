// ============================================================================
// AutoridadControlTests.cs — la máquina de estados de "quién tiene el control".
//
// Una sola pantalla acciona la máquina a la vez. Estas reglas deciden si un
// celular puede mover el volante o abrir secciones: no se prueban a mano en
// un lote, se fijan acá. Reloj inyectado: nada de Thread.Sleep.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Services.Control;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class AutoridadControlTests
    {
        private long _ahora;
        private AutoridadControl _a;
        private List<RevocacionControl> _revocaciones;

        private static readonly ClienteControl Cabina = ClienteControl.Cabina();
        private static readonly ClienteControl Celu = ClienteControl.Remoto("r:celu-juan", "Celular de Juan", "192.168.5.23");
        private static readonly ClienteControl Tablet = ClienteControl.Remoto("r:tablet", "Tablet", "192.168.5.40");

        [SetUp]
        public void SetUp()
        {
            _ahora = 100000;
            _a = new AutoridadControl(() => _ahora);
            _revocaciones = new List<RevocacionControl>();
            _a.Revocado += r => _revocaciones.Add(r);
        }

        private void CabinaViva() => _a.Latido(Cabina);
        private void Pasan(long ms) => _ahora += ms;

        // ── Arranque ────────────────────────────────────────────────────────

        [Test]
        public void Arranca_con_el_control_en_la_cabina()
        {
            var e = _a.Estado();
            Assert.That(e.DuenoEsCabina, Is.True);
            Assert.That(e.HayPedido, Is.False);
            Assert.That(_a.TieneControl(Cabina), Is.True);
            Assert.That(_a.TieneControl(Celu), Is.False);
        }

        // ── Pedir ───────────────────────────────────────────────────────────

        [Test]
        public void Con_la_cabina_activa_el_pedido_queda_pendiente_y_no_da_control()
        {
            CabinaViva();
            var r = _a.Pedir(Celu);
            Assert.That(r.Resultado, Is.EqualTo(ResultadoPedido.Pendiente));
            Assert.That(_a.TieneControl(Celu), Is.False);
            var e = _a.Estado();
            Assert.That(e.HayPedido, Is.True);
            Assert.That(e.PedidoNombre, Is.EqualTo("Celular de Juan"));
        }

        [Test]
        public void Sin_cabina_activa_el_pedido_se_concede_directo()
        {
            // Nadie latió desde la cabina (pantalla cerrada o colgada).
            var r = _a.Pedir(Celu);
            Assert.That(r.Resultado, Is.EqualTo(ResultadoPedido.Concedido));
            Assert.That(_a.TieneControl(Celu), Is.True);
            Assert.That(_a.TieneControl(Cabina), Is.False);
        }

        [Test]
        public void Cabina_que_dejo_de_latir_hace_rato_cuenta_como_ausente()
        {
            CabinaViva();
            Pasan(AutoridadControl.CabinaAusenteMs + 1);
            Assert.That(_a.Pedir(Celu).Resultado, Is.EqualTo(ResultadoPedido.Concedido));
        }

        [Test]
        public void Si_otro_remoto_tiene_el_control_se_rechaza()
        {
            Assert.That(_a.Pedir(Celu).Resultado, Is.EqualTo(ResultadoPedido.Concedido));
            var r = _a.Pedir(Tablet);
            Assert.That(r.Resultado, Is.EqualTo(ResultadoPedido.Rechazado));
            Assert.That(r.Mensaje, Does.Contain("Celular de Juan"));
            Assert.That(_a.TieneControl(Celu), Is.True);
        }

        [Test]
        public void Un_segundo_pedido_mientras_hay_uno_pendiente_se_rechaza()
        {
            CabinaViva();
            _a.Pedir(Celu);
            var r = _a.Pedir(Tablet);
            Assert.That(r.Resultado, Is.EqualTo(ResultadoPedido.Rechazado));
            Assert.That(_a.Estado().PedidoId, Is.EqualTo(Celu.Id));
        }

        [Test]
        public void El_dueno_que_vuelve_a_pedir_sigue_concedido()
        {
            _a.Pedir(Celu);
            Assert.That(_a.Pedir(Celu).Resultado, Is.EqualTo(ResultadoPedido.Concedido));
        }

        [Test]
        public void Mismo_id_desde_otra_ip_no_es_el_mismo_cliente()
        {
            _a.Pedir(Celu);
            var impostor = ClienteControl.Remoto(Celu.Id, "Celular de Juan", "192.168.5.99");
            Assert.That(_a.TieneControl(impostor), Is.False);
            Assert.That(_a.Pedir(impostor).Resultado, Is.EqualTo(ResultadoPedido.Rechazado));
        }

        // ── Ceder / rechazar ────────────────────────────────────────────────

        [Test]
        public void La_cabina_cede_al_pedido_pendiente()
        {
            CabinaViva();
            _a.Pedir(Celu);
            Assert.That(_a.Ceder(), Is.True);
            Assert.That(_a.TieneControl(Celu), Is.True);
            Assert.That(_a.Estado().HayPedido, Is.False);
            Assert.That(_a.Estado().DuenoNombre, Is.EqualTo("Celular de Juan"));
        }

        [Test]
        public void Ceder_sin_pedido_no_hace_nada()
        {
            CabinaViva();
            Assert.That(_a.Ceder(), Is.False);
            Assert.That(_a.Estado().DuenoEsCabina, Is.True);
        }

        [Test]
        public void Ceder_un_pedido_que_dejo_de_latir_no_concede()
        {
            CabinaViva();
            _a.Pedir(Celu);
            Pasan(AutoridadControl.VencimientoMs + 1);
            CabinaViva();
            Assert.That(_a.Ceder(), Is.False);
            Assert.That(_a.Estado().DuenoEsCabina, Is.True);
        }

        [Test]
        public void El_pedido_se_mantiene_vivo_con_latidos_del_que_pide()
        {
            CabinaViva();
            _a.Pedir(Celu);
            for (int i = 0; i < 10; i++) { Pasan(AutoridadControl.LatidoEsperadoMs); _a.Latido(Celu); CabinaViva(); }
            Assert.That(_a.Ceder(), Is.True);
        }

        [Test]
        public void La_cabina_rechaza_el_pedido()
        {
            CabinaViva();
            _a.Pedir(Celu);
            Assert.That(_a.RechazarPedido(), Is.True);
            Assert.That(_a.Estado().HayPedido, Is.False);
            Assert.That(_a.TieneControl(Celu), Is.False);
        }

        // ── Heartbeat / vencimiento ─────────────────────────────────────────

        [Test]
        public void Con_latidos_a_tiempo_el_remoto_conserva_el_control()
        {
            _a.Pedir(Celu);
            for (int i = 0; i < 20; i++)
            {
                Pasan(AutoridadControl.LatidoEsperadoMs);
                _a.Latido(Celu);
                Assert.That(_a.Barrer(), Is.False);
            }
            Assert.That(_a.TieneControl(Celu), Is.True);
            Assert.That(_revocaciones, Is.Empty);
        }

        [Test]
        public void Tolera_un_latido_perdido()
        {
            _a.Pedir(Celu);
            Pasan(2 * AutoridadControl.LatidoEsperadoMs); // se perdió uno
            Assert.That(_a.Barrer(), Is.False);
            Assert.That(_a.TieneControl(Celu), Is.True);
        }

        [Test]
        public void Latido_vencido_revoca_y_vuelve_el_control_a_la_cabina()
        {
            _a.Pedir(Celu);
            Pasan(AutoridadControl.VencimientoMs + 1);
            Assert.That(_a.TieneControl(Celu), Is.False, "vencido ya no acciona aunque no se haya barrido");
            Assert.That(_a.Barrer(), Is.True);
            Assert.That(_a.Estado().DuenoEsCabina, Is.True);
            Assert.That(_revocaciones, Has.Count.EqualTo(1));
            Assert.That(_revocaciones[0].Remoto.Id, Is.EqualTo(Celu.Id));
            Assert.That(_revocaciones[0].Involuntaria, Is.True);
        }

        [Test]
        public void Revocacion_avisa_si_el_remoto_engancho_la_direccion()
        {
            _a.Pedir(Celu);
            _a.MarcarDireccion(Celu);
            Pasan(AutoridadControl.VencimientoMs + 1);
            _a.Barrer();
            Assert.That(_revocaciones[0].DireccionAccionada, Is.True);
        }

        [Test]
        public void Revocacion_sin_direccion_accionada_no_pide_desenganchar()
        {
            _a.Pedir(Celu);
            Pasan(AutoridadControl.VencimientoMs + 1);
            _a.Barrer();
            Assert.That(_revocaciones[0].DireccionAccionada, Is.False);
        }

        [Test]
        public void Marcar_direccion_de_quien_no_es_dueno_no_cuenta()
        {
            _a.Pedir(Celu);
            _a.MarcarDireccion(Tablet);
            Pasan(AutoridadControl.VencimientoMs + 1);
            _a.Barrer();
            Assert.That(_revocaciones[0].DireccionAccionada, Is.False);
        }

        [Test]
        public void Barrer_sin_remoto_no_revoca_nada()
        {
            Pasan(60000);
            Assert.That(_a.Barrer(), Is.False);
            Assert.That(_revocaciones, Is.Empty);
        }

        [Test]
        public void El_latido_de_otro_no_mantiene_vivo_al_dueno()
        {
            _a.Pedir(Celu);
            Pasan(AutoridadControl.VencimientoMs - 100);
            _a.Latido(Tablet);
            Pasan(200);
            Assert.That(_a.Barrer(), Is.True);
        }

        // ── Soltar / recuperar ──────────────────────────────────────────────

        [Test]
        public void El_remoto_suelta_y_el_control_vuelve_a_la_cabina()
        {
            _a.Pedir(Celu);
            Assert.That(_a.Soltar(Celu), Is.True);
            Assert.That(_a.Estado().DuenoEsCabina, Is.True);
            Assert.That(_revocaciones, Has.Count.EqualTo(1));
            Assert.That(_revocaciones[0].Involuntaria, Is.False);
        }

        [Test]
        public void Soltar_de_quien_no_es_dueno_no_hace_nada()
        {
            _a.Pedir(Celu);
            Assert.That(_a.Soltar(Tablet), Is.False);
            Assert.That(_a.TieneControl(Celu), Is.True);
        }

        [Test]
        public void La_cabina_recupera_sin_disparar_revocacion()
        {
            // El operario está sentado ahí: no hay que desenganchar nada.
            _a.Pedir(Celu);
            _a.MarcarDireccion(Celu);
            _a.Recuperar("la cabina retomó el control");
            Assert.That(_a.Estado().DuenoEsCabina, Is.True);
            Assert.That(_a.TieneControl(Celu), Is.False);
            Assert.That(_revocaciones, Is.Empty);
        }

        [Test]
        public void Pedir_desde_la_cabina_es_recuperar()
        {
            _a.Pedir(Celu);
            Assert.That(_a.Pedir(Cabina).Resultado, Is.EqualTo(ResultadoPedido.Concedido));
            Assert.That(_a.Estado().DuenoEsCabina, Is.True);
        }

        [Test]
        public void Recuperar_borra_el_pedido_pendiente()
        {
            CabinaViva();
            _a.Pedir(Celu);
            _a.Recuperar("x");
            Assert.That(_a.Estado().HayPedido, Is.False);
        }

        // ── Eventos ─────────────────────────────────────────────────────────

        [Test]
        public void Cada_cambio_de_dueno_avisa()
        {
            var cambios = new List<EstadoControl>();
            _a.Cambio += e => cambios.Add(e);
            _a.Pedir(Celu);              // concedido (cabina ausente)
            _a.Recuperar("x");
            Assert.That(cambios, Has.Count.EqualTo(2));
            Assert.That(cambios[0].DuenoEsCabina, Is.False);
            Assert.That(cambios[1].DuenoEsCabina, Is.True);
        }

        [Test]
        public void El_estado_dice_cuanto_falta_para_vencer()
        {
            _a.Pedir(Celu);
            Pasan(1000);
            Assert.That(_a.Estado().VenceEnMs, Is.EqualTo(AutoridadControl.VencimientoMs - 1000));
        }
    }
}
