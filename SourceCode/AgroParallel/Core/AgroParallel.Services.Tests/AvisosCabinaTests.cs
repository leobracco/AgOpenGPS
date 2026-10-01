// ============================================================================
// AvisosCabinaTests.cs — los motivos que ve el operario, fijados con tests.
//
// El texto de estos avisos se lee arriba de un tractor andando, con la maquina
// trabajando. Que digan lo correcto NO se comprueba a mano en una sembradora:
// se fija aca.
// ============================================================================

using AgroParallel.Cabina;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class AvisosCabinaTests
    {
        /// <summary>Todo en orden: conectado, con GPS, lote, lindero y guía.</summary>
        private static EstadoCabina Todo()
        {
            return new EstadoCabina
            {
                Conectado = true,
                HayGps = true,
                LoteAbierto = true,
                HayLindero = true,
                FueraDelLindero = false,
                Guias = 1,
                DistanciaAlLoteKm = 0.3,
            };
        }

        // ── Guía A/B ────────────────────────────────────────────────────────

        [Test]
        public void Con_todo_en_orden_la_guia_se_puede_tirar()
        {
            Assert.That(AvisosCabina.PorQueNoSePuedeTirarGuia(Todo()), Is.Null);
        }

        [Test]
        public void Sin_lote_no_se_tira_una_guia_y_se_explica_por_que()
        {
            var e = Todo(); e.LoteAbierto = false;
            string m = AvisosCabina.PorQueNoSePuedeTirarGuia(e);
            Assert.That(m, Is.Not.Null);
            Assert.That(m, Does.Contain("lote"));
            // El motivo tiene que decir POR QUE, no solo que no se puede.
            Assert.That(m, Does.Contain("se guarda"));
        }

        [Test]
        public void Sin_gps_tampoco_se_tira_una_guia()
        {
            var e = Todo(); e.HayGps = false;
            Assert.That(AvisosCabina.PorQueNoSePuedeTirarGuia(e), Does.Contain("GPS"));
        }

        [Test]
        public void Primero_se_pide_el_lote_y_despues_el_gps()
        {
            // Sin lote Y sin GPS, el operario tiene que leer UNA cosa: la que le
            // toca resolver primero. Cuatro avisos juntos no se leen manejando.
            var e = Todo(); e.LoteAbierto = false; e.HayGps = false;
            Assert.That(AvisosCabina.PorQueNoSePuedeTirarGuia(e), Does.Contain("lote"));
        }

        // ── Piloto ──────────────────────────────────────────────────────────

        [Test]
        public void Con_todo_en_orden_el_piloto_se_puede_activar()
        {
            Assert.That(AvisosCabina.PorQueNoSePuedeActivarPiloto(Todo()), Is.Null);
        }

        [Test]
        public void Sin_gps_el_piloto_no_arranca_y_lo_dice()
        {
            var e = Todo(); e.HayGps = false;
            Assert.That(AvisosCabina.PorQueNoSePuedeActivarPiloto(e), Does.Contain("GPS"));
        }

        [Test]
        public void Sin_guia_el_piloto_no_arranca_y_dice_que_hacer()
        {
            var e = Todo(); e.Guias = 0;
            string m = AvisosCabina.PorQueNoSePuedeActivarPiloto(e);
            Assert.That(m, Does.Contain("guía"));
            Assert.That(m, Does.Contain("A/B"), "el aviso tiene que decir qué hacer, no solo qué falta");
        }

        // ── Fuera del lindero: avisa, NO bloquea ────────────────────────────

        [Test]
        public void Fuera_del_lindero_avisa_que_no_va_a_pintar_ni_sembrar()
        {
            var e = Todo(); e.FueraDelLindero = true;
            string m = AvisosCabina.AvisoFueraDelLindero(e);
            Assert.That(m, Does.Contain("FUERA"));
            Assert.That(m, Does.Contain("pintar"));
            Assert.That(m, Does.Contain("sembrar"));
        }

        [Test]
        public void Estar_fuera_del_lindero_NO_impide_activar_el_piloto()
        {
            // Puede estar entrando al lote, o haciendo una pasada a proposito.
            // El aviso es informativo: bloquear seria peor que avisar.
            var e = Todo(); e.FueraDelLindero = true;
            Assert.That(AvisosCabina.PorQueNoSePuedeActivarPiloto(e), Is.Null);
        }

        [Test]
        public void Sin_lindero_cargado_no_se_avisa_nada()
        {
            var e = Todo(); e.HayLindero = false; e.FueraDelLindero = true;
            Assert.That(AvisosCabina.AvisoFueraDelLindero(e), Is.Null,
                "sin lindero no se puede estar afuera de nada");
        }

        // ── Lejos del lote ──────────────────────────────────────────────────

        [Test]
        public void A_mas_de_veinte_km_avisa_con_la_distancia()
        {
            var e = Todo(); e.DistanciaAlLoteKm = 47.3;
            string m = AvisosCabina.AvisoLejosDelLote(e);
            Assert.That(m, Does.Contain("47"));
            Assert.That(m, Does.Contain("km"));
        }

        [Test]
        public void Adentro_del_lote_no_molesta()
        {
            Assert.That(AvisosCabina.AvisoLejosDelLote(Todo()), Is.Null);
        }

        [Test]
        public void Sin_saber_la_distancia_no_se_inventa_un_aviso()
        {
            var e = Todo(); e.DistanciaAlLoteKm = -1;
            Assert.That(AvisosCabina.AvisoLejosDelLote(e), Is.Null);
        }

        // ── Por qué no se ve la sembradora ──────────────────────────────────

        [Test]
        public void Con_todo_en_orden_la_sembradora_tiene_que_verse()
        {
            Assert.That(AvisosCabina.PorQueNoSeVeLaSembradora(Todo()), Is.Null,
                "si devuelve null y igual no se ve, el problema es de dibujo — " +
                "y poder distinguir eso es justamente el punto");
        }

        [Test]
        public void Sin_gps_explica_por_que_no_se_ve_la_sembradora()
        {
            var e = Todo(); e.HayGps = false;
            Assert.That(AvisosCabina.PorQueNoSeVeLaSembradora(e), Does.Contain("GPS"));
        }

        // ── Distancia ───────────────────────────────────────────────────────

        [Test]
        public void La_distancia_se_calcula_bien()
        {
            // Las Gringas (aprox) a Rosario: ~90 km en linea recta.
            double d = AvisosCabina.DistanciaKm(-33.05, -61.60, -32.95, -60.65);
            Assert.That(d, Is.GreaterThan(80).And.LessThan(100));
        }

        [Test]
        public void Cero_cero_no_es_una_coordenada_valida()
        {
            // 0/0 es "sin dato" en todo el repo. Tomarlo como el golfo de Guinea
            // haria que TODO lote quede "a 10 mil km" y el aviso gritaria siempre.
            Assert.That(AvisosCabina.DistanciaKm(0, 0, -33.05, -61.60), Is.EqualTo(-1));
            Assert.That(AvisosCabina.DistanciaKm(-33.05, -61.60, 0, 0), Is.EqualTo(-1));
        }

        [Test]
        public void El_mismo_punto_da_cero()
        {
            Assert.That(AvisosCabina.DistanciaKm(-33.05, -61.60, -33.05, -61.60), Is.EqualTo(0).Within(0.001));
        }

        // ── Punto de referencia contra la deriva ────────────────────────────

        [Test]
        public void Con_todo_en_orden_la_referencia_se_puede_usar()
        {
            Assert.That(AvisosCabina.PorQueNoSePuedeUsarReferencia(Todo()), Is.Null);
        }

        [Test]
        public void Sin_lote_la_referencia_dice_que_se_guarda_en_el_lote()
        {
            var e = Todo(); e.LoteAbierto = false;
            Assert.That(AvisosCabina.PorQueNoSePuedeUsarReferencia(e), Does.Contain("lote"));
        }

        [Test]
        public void Sin_gps_la_referencia_dice_gps()
        {
            var e = Todo(); e.HayGps = false;
            Assert.That(AvisosCabina.PorQueNoSePuedeUsarReferencia(e), Does.Contain("GPS"));
        }

        [Test]
        public void Sin_conexion_la_referencia_lo_dice()
        {
            var e = Todo(); e.Conectado = false;
            Assert.That(AvisosCabina.PorQueNoSePuedeUsarReferencia(e), Does.Contain("conexión"));
        }
    }
}
