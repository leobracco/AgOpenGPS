// ============================================================================
// AvisosSistemaTests.cs — el texto y las prioridades de los avisos de sistema.
//
// El 2026-09-25 una sembradora estuvo 45 minutos parada y la pantalla decia
// "no anda nada". Estos avisos son la respuesta a eso, y el texto que ve el
// operario NO se comprueba a mano arriba de una sembradora: se fija aca.
//
// Lo que estos tests protegen, mas alla del texto:
//  · que un aviso NO aparezca cuando no corresponde (un falso positivo por dia
//    y en dos semanas nadie lee los carteles — y entonces el 25/9 se repite,
//    con un cartel de mas),
//  · el ORDEN y la SUPRESION: si el firewall bloquea, "ningun equipo se
//    conecto" es la CONSECUENCIA. Decir las dos cosas manda al operario a
//    revisar cables que estan bien.
// ============================================================================

using System.Linq;
using AgroParallel.Cabina;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class AvisosSistemaTests
    {
        /// <summary>Una pantalla sana: nada que decir.</summary>
        private static EstadoSistemaCabina Sana()
        {
            return new EstadoSistemaCabina
            {
                Firewall = FirewallEstado.Ok,
                FirewallSePuedeArreglar = true,
                NodosConfigurados = 2,
                NodosAnunciados = 2,
                SegDesdeArranque = 600,
                GpsPuertoConfigurado = true,
                GpsPuertoNombre = "COM1",
                GpsPuertoAbierto = true,
                GpsPuertoTomado = false,
                LoteAbierto = true,
            };
        }

        [Test]
        public void PantallaSana_NoDiceNada()
        {
            Assert.That(AvisosSistema.Evaluar(Sana()), Is.Empty);
            Assert.That(AvisosSistema.MasGrave(Sana()), Is.Null);
        }

        // ── Firewall ────────────────────────────────────────────────────────

        [Test]
        public void FirewallBloqueando_LoDiceYOfreceArreglarlo()
        {
            var e = Sana();
            e.Firewall = FirewallEstado.Bloqueando;

            var a = AvisosSistema.MasGrave(e);

            Assert.That(a, Is.Not.Null);
            Assert.That(a.Codigo, Is.EqualTo("AGP-NET-010"));
            Assert.That(a.Severidad, Is.EqualTo(SeveridadAviso.Critico));
            Assert.That(a.Accion, Is.EqualTo(AvisosSistema.AccionArreglarFirewall),
                "sin boton, el aviso convierte 45 minutos de parada en 45 minutos de parada CON un cartel");
            // El titulo tiene que nombrar al culpable, no al operario.
            Assert.That(a.Titulo, Does.Contain("Windows"));
            // Y el detalle, que hacer.
            Assert.That(a.Detalle, Does.Contain("Arreglar"));
        }

        [Test]
        public void SinArregloDisponible_NoOfreceUnBotonQueNoVaAHacerNada()
        {
            var e = Sana();
            e.Firewall = FirewallEstado.Bloqueando;
            e.FirewallSePuedeArreglar = false;

            var a = AvisosSistema.MasGrave(e);

            Assert.That(a.Accion, Is.Null);
            Assert.That(a.Detalle, Does.Contain("soporte"));
            Assert.That(a.Detalle, Does.Contain("AGP-NET-010"),
                "si no se puede arreglar solo, el operario tiene que poder dictar el codigo");
        }

        [Test]
        public void FirewallDesconocido_NoGrita()
        {
            // En Linux, o si netsh no contesta, NO se sabe. Sin saber no se
            // inventa un aviso — mismo criterio que DistanciaAlLoteKm < 0.
            var e = Sana();
            e.Firewall = FirewallEstado.Desconocido;

            Assert.That(AvisosSistema.Evaluar(e), Is.Empty);
        }

        // ── Nodos ───────────────────────────────────────────────────────────

        [Test]
        public void NodosConfiguradosYNingunoAnunciado_LoDiceConElNumero()
        {
            var e = Sana();
            e.NodosAnunciados = 0;

            var a = AvisosSistema.MasGrave(e);

            Assert.That(a, Is.Not.Null);
            Assert.That(a.Codigo, Is.EqualTo("AGP-MQTT-010"));
            Assert.That(a.Detalle, Does.Contain("2"), "decir cuantos faltan sin abrir otra pantalla");
            // Que hacer, en orden de probabilidad.
            Assert.That(a.Detalle, Does.Contain("corriente"));
            Assert.That(a.Detalle, Does.Contain("router"));
        }

        [Test]
        public void UnSoloNodo_HablaEnSingular()
        {
            var e = Sana();
            e.NodosConfigurados = 1;
            e.NodosAnunciados = 0;

            Assert.That(AvisosSistema.MasGrave(e).Detalle, Does.Contain("1 equipo"));
            Assert.That(AvisosSistema.MasGrave(e).Detalle, Does.Not.Contain("1 equipos"));
        }

        [Test]
        public void ReciénArrancado_NoGritaPorLosNodos()
        {
            // Un ESP32 tarda en bootear y engancharse al WiFi (el firmware
            // QuantiX hasta la 3.0.1 espera 15 s a proposito). Avisar a los 5 s
            // seria ruido garantizado todas las mananas.
            var e = Sana();
            e.NodosAnunciados = 0;
            e.SegDesdeArranque = 20;

            Assert.That(AvisosSistema.Evaluar(e), Is.Empty);

            e.SegDesdeArranque = AvisosSistema.GraciaNodosSeg + 1;
            Assert.That(AvisosSistema.Evaluar(e), Is.Not.Empty);
        }

        [Test]
        public void SinNodosConfigurados_NoHayNadaQueEsperar()
        {
            // Una pantalla de puro guiado, sin QuantiX ni VistaX, no tiene por
            // que ver un cartel de nodos.
            var e = Sana();
            e.NodosConfigurados = 0;
            e.NodosAnunciados = 0;

            Assert.That(AvisosSistema.Evaluar(e), Is.Empty);
        }

        [Test]
        public void UnNodoDeDosAnunciado_NoEsEsteAviso()
        {
            // "Falta uno" lo cubre el banner de nodos offline que ya existe.
            // Este aviso es para "no aparecio NINGUNO", que es red o corriente.
            var e = Sana();
            e.NodosAnunciados = 1;

            Assert.That(AvisosSistema.Evaluar(e).Any(x => x.Codigo == "AGP-MQTT-010"), Is.False);
        }

        [Test]
        public void SinLoteAbierto_LosNodosSonAtencionNoCritico()
        {
            // Parado en el galpon esto es informativo. A punto de sembrar, grave.
            var e = Sana();
            e.NodosAnunciados = 0;

            e.LoteAbierto = false;
            Assert.That(AvisosSistema.MasGrave(e).Severidad, Is.EqualTo(SeveridadAviso.Atencion));

            e.LoteAbierto = true;
            Assert.That(AvisosSistema.MasGrave(e).Severidad, Is.EqualTo(SeveridadAviso.Critico));
        }

        // ── Supresion: la causa tapa a la consecuencia ──────────────────────

        [Test]
        public void ConElFirewallBloqueando_NoSeCulpaALosNodos()
        {
            // El caso EXACTO de Las Gringas: el firewall bloqueaba y por eso no
            // habia nodos. Decir las dos cosas manda al operario a revisar
            // corriente y router de dos nodos que estaban perfectos.
            var e = Sana();
            e.Firewall = FirewallEstado.Bloqueando;
            e.NodosAnunciados = 0;

            var avisos = AvisosSistema.Evaluar(e);

            Assert.That(avisos.Any(x => x.Codigo == "AGP-NET-010"), Is.True);
            Assert.That(avisos.Any(x => x.Codigo == "AGP-MQTT-010"), Is.False,
                "el aviso de nodos es la CONSECUENCIA del firewall, no otra falla");
        }

        // ── GPS ─────────────────────────────────────────────────────────────

        [Test]
        public void PuertoDelGpsTomado_NombraElPuertoYQueHacer()
        {
            var e = Sana();
            e.GpsPuertoAbierto = false;
            e.GpsPuertoTomado = true;

            var a = AvisosSistema.MasGrave(e);

            Assert.That(a, Is.Not.Null);
            Assert.That(a.Codigo, Is.EqualTo("AGP-GPS-001"));
            Assert.That(a.Severidad, Is.EqualTo(SeveridadAviso.Critico));
            Assert.That(a.Detalle, Does.Contain("COM1"), "el puerto es el dato que sirve para buscar al culpable");
            Assert.That(a.Detalle, Does.Contain("reiniciá la pantalla"),
                "es lo unico que lo suelta, y hay que decirlo");
        }

        [Test]
        public void PuertoCerradoPeroNoTomado_NoEsEsteAviso()
        {
            // "El puerto no existe" o "no hay antena" es otro problema y otro
            // mensaje. Este aviso es solo para el handle huerfano.
            var e = Sana();
            e.GpsPuertoAbierto = false;
            e.GpsPuertoTomado = false;

            Assert.That(AvisosSistema.Evaluar(e).Any(x => x.Codigo == "AGP-GPS-001"), Is.False);
        }

        [Test]
        public void SinPuertoSerieConfigurado_NoHayNadaQueAbrir()
        {
            // GPS por LAN/UDP: el puerto serie no aplica.
            var e = Sana();
            e.GpsPuertoConfigurado = false;
            e.GpsPuertoAbierto = false;
            e.GpsPuertoTomado = true;

            Assert.That(AvisosSistema.Evaluar(e).Any(x => x.Codigo == "AGP-GPS-001"), Is.False);
        }

        [Test]
        public void SinNombreDePuerto_ElTextoSigueSiendoLegible()
        {
            var e = Sana();
            e.GpsPuertoNombre = null;
            e.GpsPuertoAbierto = false;
            e.GpsPuertoTomado = true;

            var a = AvisosSistema.MasGrave(e);
            Assert.That(a.Detalle, Does.StartWith("El puerto del GPS"));
        }

        // ── Orden entre avisos ──────────────────────────────────────────────

        [Test]
        public void ElFirewallManda_DespuesElGps()
        {
            // Todo roto a la vez: primero la causa raiz de red.
            var e = Sana();
            e.Firewall = FirewallEstado.Bloqueando;
            e.GpsPuertoAbierto = false;
            e.GpsPuertoTomado = true;
            e.NodosAnunciados = 0;

            var avisos = AvisosSistema.Evaluar(e);

            Assert.That(avisos[0].Codigo, Is.EqualTo("AGP-NET-010"));
            Assert.That(avisos[1].Codigo, Is.EqualTo("AGP-GPS-001"));
            Assert.That(avisos.Count, Is.EqualTo(2), "el de nodos queda suprimido por el firewall");
        }

        [Test]
        public void ElGpsLeGanaALosNodos()
        {
            // Sin GPS no se siembra; con nodos caidos se siembra mal. El que no
            // deja trabajar va primero.
            var e = Sana();
            e.GpsPuertoAbierto = false;
            e.GpsPuertoTomado = true;
            e.NodosAnunciados = 0;

            var avisos = AvisosSistema.Evaluar(e);

            Assert.That(avisos[0].Codigo, Is.EqualTo("AGP-GPS-001"));
            Assert.That(avisos[1].Codigo, Is.EqualTo("AGP-MQTT-010"));
        }

        // ── Contrato de los avisos ──────────────────────────────────────────

        [Test]
        public void TodosLosAvisosSonPersistentesYSuenan()
        {
            // Todos describen el AHORA y se autodescartan cuando la condicion
            // se resuelve. Y todos frenan trabajo, asi que todos suenan.
            var e = Sana();
            e.Firewall = FirewallEstado.Bloqueando;
            e.GpsPuertoAbierto = false;
            e.GpsPuertoTomado = true;

            foreach (var a in AvisosSistema.Evaluar(e))
            {
                Assert.That(a.Persistente, Is.True, a.Codigo);
                Assert.That(a.EventoSonido, Is.Not.Null.And.Not.Empty, a.Codigo);
                Assert.That(a.Titulo, Is.Not.Null.And.Not.Empty, a.Codigo);
                Assert.That(a.Detalle, Is.Not.Null.And.Not.Empty, a.Codigo);
            }
        }

        [Test]
        public void NingunTextoTieneJergaTecnica()
        {
            // El operario es tractorista. "socket", "MQTT", "broker" y "puerto
            // 1883" van al detalle tecnico del log, nunca al cartel.
            var e = Sana();
            e.Firewall = FirewallEstado.Bloqueando;
            e.GpsPuertoAbierto = false;
            e.GpsPuertoTomado = true;

            string[] prohibidas = { "socket", "MQTT", "broker", "1883", "inbound", "profile", "netsh" };
            foreach (var a in AvisosSistema.Evaluar(e))
            {
                string texto = (a.Titulo + " " + a.Detalle).ToLowerInvariant();
                foreach (var p in prohibidas)
                    Assert.That(texto, Does.Not.Contain(p.ToLowerInvariant()),
                        a.Codigo + " le habla al operario, no a un sysadmin");
            }
        }
    }
}
