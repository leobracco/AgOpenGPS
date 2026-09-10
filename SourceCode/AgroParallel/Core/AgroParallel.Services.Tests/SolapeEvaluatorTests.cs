// La regla que decide si se vuelve a sembrar sobre lo ya sembrado. Los dos
// errores cuestan plata: apagar de más deja franjas sin sembrar, apagar de menos
// tira semilla al doble. Y el chattering en el borde castiga los solenoides.

using AgroParallel.Coverage;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class SolapeEvaluatorTests
    {
        private static SolapeInput Caso(double apagado, double encendido, bool estaba)
            => SolapeEvaluator.ConDefaults(apagado, encendido, estaba);

        // ---- Regresión 1.0.68: cada estado mira solo su distancia ----------

        [Test]
        public void Apagada_PrendeAunqueLaDistanciaDeApagadoSigaCubierta()
        {
            // Saliendo de la cabecera: a la distancia de encendido (más lejos) ya
            // está limpio, a la de apagado (más cerca) todavía no. Tiene que
            // prender YA, si no arranca (on − off) segundos tarde.
            var r = SolapeEvaluator.RequeridaOn(Caso(apagado: 1.0, encendido: 0.0, estaba: false));
            Assert.That(r, Is.True);
        }

        [Test]
        public void Encendida_NoApagaPorLoQueHayALaDistanciaDeEncendido()
        {
            // Cerca está limpio, lejos ya está sembrado: sigue sembrando hasta
            // que lo sembrado entre en la distancia de apagado.
            var r = SolapeEvaluator.RequeridaOn(Caso(apagado: 0.0, encendido: 1.0, estaba: true));
            Assert.That(r, Is.True);
        }

        // ---- Umbral desde "Cobertura mínima" ------------------------------

        [Test]
        public void CoberturaMinima_100EsElNoventaDeSiempre()
        {
            Assert.That(SolapeEvaluator.UmbralApagarDesdeCobertura(100), Is.EqualTo(0.90).Within(1e-9));
        }

        [Test]
        public void CoberturaMinima_SeAcotaA50y95()
        {
            Assert.That(SolapeEvaluator.UmbralApagarDesdeCobertura(10), Is.EqualTo(0.50).Within(1e-9));
            Assert.That(SolapeEvaluator.UmbralApagarDesdeCobertura(99), Is.EqualTo(0.95).Within(1e-9));
            Assert.That(SolapeEvaluator.UmbralApagarDesdeCobertura(70), Is.EqualTo(0.70).Within(1e-9));
        }

        [Test]
        public void CoberturaMinima_CambiaDondeCorta()
        {
            // Con 70 % una sección encendida corta al 75 % cubierto; con 90 no.
            var con70 = SolapeEvaluator.ConCobertura(0.75, 0.75, true, 70);
            var con90 = SolapeEvaluator.ConCobertura(0.75, 0.75, true, 90);
            Assert.That(SolapeEvaluator.RequeridaOn(con70), Is.False);
            Assert.That(SolapeEvaluator.RequeridaOn(con90), Is.True);
            // La histéresis queda 20 puntos abajo: con 70, apagada prende recién al 50 %.
            Assert.That(SolapeEvaluator.RequeridaOn(SolapeEvaluator.ConCobertura(0.6, 0.6, false, 70)), Is.False);
            Assert.That(SolapeEvaluator.RequeridaOn(SolapeEvaluator.ConCobertura(0.5, 0.5, false, 70)), Is.True);
        }

        // ---- casos claros --------------------------------------------------

        [Test]
        public void TerrenoVirgen_Enciende()
        {
            Assert.That(SolapeEvaluator.RequeridaOn(Caso(0.0, 0.0, false)), Is.True);
        }

        [Test]
        public void YaSembrado_Apaga()
        {
            Assert.That(SolapeEvaluator.RequeridaOn(Caso(1.0, 1.0, true)), Is.False);
        }

        [Test]
        public void JustoEnElUmbralDeApagado_Apaga()
        {
            // 90% cubierto: se considera trabajado.
            Assert.That(SolapeEvaluator.RequeridaOn(Caso(0.90, 0.90, true)), Is.False);
        }

        [Test]
        public void JustoEnElUmbralDeEncendido_Enciende()
        {
            Assert.That(SolapeEvaluator.RequeridaOn(Caso(0.70, 0.70, false)), Is.True);
        }

        // ---- histéresis ----------------------------------------------------

        [Test]
        public void ZonaGris_MantieneEncendida()
        {
            // 80%: ni una cosa ni la otra. Venía encendida -> sigue encendida.
            Assert.That(SolapeEvaluator.RequeridaOn(Caso(0.80, 0.80, true)), Is.True);
        }

        [Test]
        public void ZonaGris_MantieneApagada()
        {
            // Mismo 80%, pero venía apagada -> sigue apagada.
            Assert.That(SolapeEvaluator.RequeridaOn(Caso(0.80, 0.80, false)), Is.False);
        }

        [Test]
        public void BordeandoLaPasada_NoHaceChattering()
        {
            // Simula ir rozando el borde: el solape oscila dentro de la zona
            // gris. Sin histéresis esto prendería y apagaría en cada fix.
            double[] serie = { 0.72, 0.85, 0.78, 0.88, 0.75, 0.83, 0.79 };

            bool estado = true;
            int cambios = 0;
            foreach (var s in serie)
            {
                bool nuevo = SolapeEvaluator.RequeridaOn(Caso(s, s, estado));
                if (nuevo != estado) cambios++;
                estado = nuevo;
            }

            Assert.That(cambios, Is.EqualTo(0), "dentro de la zona gris no se toca el estado");
            Assert.That(estado, Is.True);
        }

        [Test]
        public void SaleDeLaZonaGrisPorArriba_ReciénAhiApaga()
        {
            bool estado = true;
            estado = SolapeEvaluator.RequeridaOn(Caso(0.85, 0.85, estado));
            Assert.That(estado, Is.True, "todavía en zona gris");

            estado = SolapeEvaluator.RequeridaOn(Caso(0.95, 0.95, estado));
            Assert.That(estado, Is.False, "cruzó el umbral de apagado");
        }

        [Test]
        public void SaleDeLaZonaGrisPorAbajo_ReciénAhiEnciende()
        {
            bool estado = false;
            estado = SolapeEvaluator.RequeridaOn(Caso(0.75, 0.75, estado));
            Assert.That(estado, Is.False, "todavía en zona gris");

            estado = SolapeEvaluator.RequeridaOn(Caso(0.60, 0.60, estado));
            Assert.That(estado, Is.True, "cruzó el umbral de encendido");
        }

        // ---- las dos distancias --------------------------------------------

        [Test]
        public void ApagaPorLoQueVieneAdelante_AunqueDondeEstaEsteLimpio()
        {
            // Entrando a una zona ya trabajada: el look-ahead de apagado ya la
            // ve, el de encendido todavía no. Tiene que apagar AHORA, no unos
            // metros después.
            var r = SolapeEvaluator.RequeridaOn(Caso(apagado: 1.0, encendido: 0.0, estaba: true));

            Assert.That(r, Is.False, "manda lo que viene adelante");
        }

        [Test]
        public void SaliendoDeZonaTrabajada_EnciendeCuandoElFrenteEstaLimpio()
        {
            var r = SolapeEvaluator.RequeridaOn(Caso(apagado: 0.0, encendido: 0.0, estaba: false));

            Assert.That(r, Is.True);
        }

        // ---- robustez ------------------------------------------------------

        [Test]
        public void ValoresFueraDeRango_NoRompen()
        {
            Assert.That(SolapeEvaluator.RequeridaOn(Caso(5.0, 5.0, true)), Is.False, "se satura en 1");
            Assert.That(SolapeEvaluator.RequeridaOn(Caso(-3.0, -3.0, false)), Is.True, "se satura en 0");
        }

        [Test]
        public void NaN_SeTrataComoSinCobertura()
        {
            var r = SolapeEvaluator.RequeridaOn(Caso(double.NaN, double.NaN, false));

            Assert.That(r, Is.True, "ante un dato roto conviene sembrar, no saltear");
        }

        [Test]
        public void UmbralesInvertidos_SiguenSiendoDeterministas()
        {
            // Alguien carga encender=0.95 y apagar=0.10 (al revés).
            var e = new SolapeInput
            {
                SolapeApagado = 0.5,
                SolapeEncendido = 0.5,
                EstabaEncendida = true,
                UmbralApagar = 0.10,
                UmbralEncender = 0.95,
            };

            // No debe tirar ni quedar indefinido.
            Assert.That(SolapeEvaluator.RequeridaOn(e), Is.False);
        }

        [Test]
        public void UmbralesEnCero_NoTiraExcepcion()
        {
            var e = new SolapeInput
            {
                SolapeApagado = 0.0,
                SolapeEncendido = 0.0,
                EstabaEncendida = false,
                UmbralApagar = 0.0,
                UmbralEncender = 0.0,
            };

            Assert.That(SolapeEvaluator.RequeridaOn(e), Is.False, "con umbral 0 todo cuenta como trabajado");
        }
    }
}
