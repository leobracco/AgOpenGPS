// ============================================================================
// QueHacerAlarmaTests.cs — la linea "Que hacer" de cada alarma conocida.
//
// Lo que se protege aca:
//  · cada alarma que la cabina muestra (nodo caido, surcos VistaX, piloto que
//    se solto) tiene una solucion corta y accionable;
//  · una alarma SIN entrada devuelve null: la pantalla no muestra nada. Un
//    consejo inventado manda al operario a tocar donde no es — peor que no
//    decir nada;
//  · en el registro de eventos se reconoce el codigo AGP-* adentro de una
//    linea y los desenganches del piloto por su texto, y nada mas.
// ============================================================================

using AgroParallel.Cabina;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class QueHacerAlarmaTests
    {
        // ── Catalogo ────────────────────────────────────────────────────────

        [TestCase(QueHacerAlarma.NodoOffline)]
        [TestCase(QueHacerAlarma.PilotoSinGps)]
        [TestCase(QueHacerAlarma.PilotoLejosDeLaGuia)]
        [TestCase(QueHacerAlarma.PilotoSinRtk)]
        [TestCase("AGP-MQTT-001")]
        [TestCase("AGP-MQTT-010")]
        [TestCase("AGP-NET-010")]
        [TestCase("AGP-GPS-001")]
        [TestCase("AGP-USB-001")]
        [TestCase("AGP-USB-002")]
        [TestCase("AGP-USB-007")]
        public void AlarmasConocidas_TienenQueHacer(string codigo)
        {
            string s = QueHacerAlarma.Para(codigo);
            Assert.That(s, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TodasLasSoluciones_CortasYSinJerga()
        {
            foreach (var codigo in QueHacerAlarma.Codigos)
            {
                string s = QueHacerAlarma.Para(codigo);
                Assert.That(s, Is.Not.Empty, codigo);
                // Se lee de reojo manejando: una o dos frases.
                Assert.That(s.Length, Is.LessThanOrEqualTo(110), codigo);
                Assert.That(s, Does.EndWith("."), codigo);
                // Nombres de producto: nada de AOG/AgOpenGPS/AgIO en cabina.
                Assert.That(s, Does.Not.Contain("AOG").And.Not.Contain("AgOpenGPS").And.Not.Contain("AgIO"), codigo);
                // Nada tecnico: el operario es tractorista.
                Assert.That(s, Does.Not.Contain("Exception").And.Not.Contain("MQTT").And.Not.Contain("broker"), codigo);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("AGP-SYS-009")]      // no clasificada: no hay consejo honesto
        [TestCase("AGP-MQTT-009")]
        [TestCase("AGP-XXX-123")]
        [TestCase("vistax-desconocido")]
        public void SinEntrada_NoSeMuestraNada(string codigo)
        {
            Assert.That(QueHacerAlarma.Para(codigo), Is.Null);
        }

        [Test]
        public void Codigo_IgnoraMayusculasYEspacios()
        {
            Assert.That(QueHacerAlarma.Para(" agp-gps-001 "), Is.EqualTo(QueHacerAlarma.Para("AGP-GPS-001")));
        }

        [Test]
        public void PilotoSinGps_EsElDelPedido()
        {
            Assert.That(QueHacerAlarma.Para(QueHacerAlarma.PilotoSinGps),
                Is.EqualTo("Revisá el cable de la antena. Tocá Piloto de nuevo cuando vuelva la señal."));
        }

        // ── Surcos VistaX (los estados que pinta el banner de cabina) ───────

        [TestCase("tapado")]
        [TestCase("bajo")]
        [TestCase("no-data")]
        [TestCase("alerta")]
        [TestCase("exceso")]
        public void EstadosDeSurcoDelBanner_TienenQueHacer(string estado)
        {
            Assert.That(QueHacerAlarma.ParaSurco(estado), Is.Not.Null.And.Not.Empty);
        }

        [TestCase(null)]
        [TestCase("ok")]
        [TestCase("muted")]
        public void EstadoDeSurcoSinFalla_NoTieneQueHacer(string estado)
        {
            Assert.That(QueHacerAlarma.ParaSurco(estado), Is.Null);
        }

        [Test]
        public void TolvaVacia_DiceCargarSemilla()
        {
            Assert.That(QueHacerAlarma.ParaSurco("alerta"), Does.Contain("tolva"));
        }

        // ── Registro de eventos ─────────────────────────────────────────────

        [Test]
        public void EnLinea_EncuentraElCodigoAgpAdentro()
        {
            string linea = "12:03:44 Nodos: AGP-GPS-001 COM1 ocupado";
            Assert.That(QueHacerAlarma.EnLinea(linea), Is.EqualTo(QueHacerAlarma.Para("AGP-GPS-001")));
        }

        [Test]
        public void EnLinea_CodigoSinEntrada_Null()
        {
            Assert.That(QueHacerAlarma.EnLinea("10:00 AGP-SYS-009 algo salio mal"), Is.Null);
        }

        [TestCase("Piloto desenganchado: sin señal de GPS hace 3 s.", QueHacerAlarma.PilotoSinGps)]
        [TestCase("Piloto desenganchado: el tractor se fue 2,5 m de la guía (máximo 1,0 m).", QueHacerAlarma.PilotoLejosDeLaGuia)]
        [TestCase("Piloto desenganchado: se perdió el RTK fijo.", QueHacerAlarma.PilotoSinRtk)]
        public void EnLinea_ReconoceLosDesenganchesDelPiloto(string linea, string codigo)
        {
            Assert.That(QueHacerAlarma.EnLinea("09:12:01 " + linea), Is.EqualTo(QueHacerAlarma.Para(codigo)));
            Assert.That(QueHacerAlarma.CodigoDePiloto(linea), Is.EqualTo(codigo));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("GuidanceEngine: lote abierto")]
        [TestCase("Piloto activado")]
        public void EnLinea_LineaComun_Null(string linea)
        {
            Assert.That(QueHacerAlarma.EnLinea(linea), Is.Null);
        }

        [Test]
        public void AnotarLog_AgregaQueHacerDebajoDeLaLineaConocida()
        {
            string log = "10:00 Lote abierto\n10:01 Piloto desenganchado: se perdió el RTK fijo.\n10:02 Nada";

            string anotado = QueHacerAlarma.AnotarLog(log);

            Assert.That(anotado, Is.EqualTo(
                "10:00 Lote abierto\n" +
                "10:01 Piloto desenganchado: se perdió el RTK fijo.\n" +
                "    → Qué hacer: " + QueHacerAlarma.Para(QueHacerAlarma.PilotoSinRtk) + "\n" +
                "10:02 Nada"));
        }

        [Test]
        public void AnotarLog_SinAlarmasConocidas_QuedaIgual()
        {
            string log = "a\nb\nAGP-SYS-009 x";
            Assert.That(QueHacerAlarma.AnotarLog(log), Is.EqualTo(log));
            Assert.That(QueHacerAlarma.AnotarLog(""), Is.EqualTo(""));
            Assert.That(QueHacerAlarma.AnotarLog(null), Is.EqualTo(""));
        }

        [Test]
        public void AnotarLog_TraduceConElTraductorQueLePasan()
        {
            string anotado = QueHacerAlarma.AnotarLog("AGP-GPS-001", t => "[" + t + "]");
            Assert.That(anotado, Is.EqualTo(
                "AGP-GPS-001\n    → [Qué hacer]: [" + QueHacerAlarma.Para("AGP-GPS-001") + "]"));
        }
    }
}
