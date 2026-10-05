// Casos de borde de la alarma por surco de VistaX. Son los que deciden si el
// operario frena el tractor: una alarma que no salta cuesta un lote mal
// sembrado, y una que salta de más hace que la apaguen y dejen de mirarla.

using AgroParallel.VistaX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class VxSurcoEvaluatorTests
    {
        // Surco sembrando normal: objetivo 300 sem/min, banda 240–360.
        private static VxSurcoInput Sembrando(double spm) => new VxSurcoInput
        {
            EsSiembra = true,
            SembrandoActivo = true,
            Spm = spm,
            ObjetivoSpm = 300,
            LimiteBajo = 240,
            LimiteAlto = 360,
        };

        // ---- La alarma que más plata salva --------------------------------

        [Test]
        public void Bajada_tapada_alarma()
        {
            var r = VxSurcoEvaluator.Evaluar(Sembrando(0));
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.Tapado));
            Assert.That(r.Alerta, Is.True, "hay telemetria pero no caen semillas: bajada bloqueada");
        }

        [Test]
        public void Siembra_por_debajo_del_limite_alarma()
        {
            var r = VxSurcoEvaluator.Evaluar(Sembrando(200));
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.Bajo));
            Assert.That(r.Alerta, Is.True);
        }

        [Test]
        public void Dentro_de_la_banda_no_alarma()
        {
            var r = VxSurcoEvaluar(300);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.Ok));
            Assert.That(r.Alerta, Is.False);
        }

        [Test]
        public void Exceso_se_marca_pero_NO_alarma()
        {
            // Sembrar de mas no es falla productiva: frenar el tractor por esto
            // seria peor que seguir.
            var r = VxSurcoEvaluar(500);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.Exceso));
            Assert.That(r.Alerta, Is.False);
        }

        // ---- Lo que NO tiene que alarmar (o la apagan y dejan de mirarla) --

        [Test]
        public void Seccion_cortada_no_alarma_aunque_no_caiga_nada()
        {
            // Es el caso de la cabecera: el cuerpo esta levantado a proposito.
            var e = Sembrando(0);
            e.SeccionCortada = true;
            var r = VxSurcoEvaluator.Evaluar(e);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.SeccionOff));
            Assert.That(r.Alerta, Is.False);
        }

        [Test]
        public void Seccion_cortada_gana_sobre_silenciado_y_sin_datos()
        {
            // El orden de las reglas es parte del contrato: si se invierte, cada
            // cabecera dispara una alarma por cuerpo.
            var e = Sembrando(0);
            e.SeccionCortada = true;
            e.Silenciado = true;
            e.SinDatos = true;
            Assert.That(VxSurcoEvaluator.Evaluar(e).Estado, Is.EqualTo(VxSurcoEvaluator.SeccionOff));
        }

        [Test]
        public void Sensor_silenciado_no_alarma()
        {
            var e = Sembrando(0);
            e.Silenciado = true;
            var r = VxSurcoEvaluator.Evaluar(e);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.Silenciado));
            Assert.That(r.Alerta, Is.False);
        }

        [Test]
        public void Turbina_sin_objetivo_propio_no_alarma()
        {
            // Comparar rpm de turbina contra densidad de siembra daria falsas
            // alarmas todo el tiempo.
            var e = new VxSurcoInput { EsSiembra = false, SembrandoActivo = true, Spm = 0, ObjetivoSpm = 0 };
            var r = VxSurcoEvaluator.Evaluar(e);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.Ok));
            Assert.That(r.Alerta, Is.False);
        }

        [Test]
        public void Sin_objetivo_cargado_no_alarma()
        {
            var e = Sembrando(0);
            e.ObjetivoSpm = 0;
            Assert.That(VxSurcoEvaluator.Evaluar(e).Alerta, Is.False,
                "sin objetivo no hay contra que comparar");
        }

        // ---- Sensor mudo ---------------------------------------------------

        [Test]
        public void Sin_datos_mientras_se_siembra_ES_alarma()
        {
            // Nodo caido o cable cortado: no es un gris neutro, es una falla.
            var e = Sembrando(0);
            e.SinDatos = true;
            var r = VxSurcoEvaluator.Evaluar(e);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.SinDatos));
            Assert.That(r.Alerta, Is.True);
        }

        [Test]
        public void Sin_datos_fuera_de_siembra_es_informativo()
        {
            var e = Sembrando(0);
            e.SinDatos = true;
            e.SembrandoActivo = false;
            var r = VxSurcoEvaluator.Evaluar(e);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.SinDatos));
            Assert.That(r.Alerta, Is.False, "con el tractor parado no tiene sentido alarmar");
        }

        [Test]
        public void Sin_datos_de_un_sensor_que_no_es_de_siembra_no_alarma()
        {
            var e = Sembrando(0);
            e.SinDatos = true;
            e.EsSiembra = false;
            Assert.That(VxSurcoEvaluator.Evaluar(e).Alerta, Is.False);
        }

        // ---- Sensores de estado -------------------------------------------

        [Test]
        public void Tolva_vacia_alarma()
        {
            var e = new VxSurcoInput { EsEstado = true, EsTolvaVacia = true, Valor = 1, SembrandoActivo = true };
            var r = VxSurcoEvaluator.Evaluar(e);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.Alerta));
            Assert.That(r.Alerta, Is.True);
        }

        [Test]
        public void Tolva_con_insumo_no_alarma()
        {
            var e = new VxSurcoInput { EsEstado = true, EsTolvaVacia = true, Valor = 0, SembrandoActivo = true };
            Assert.That(VxSurcoEvaluator.Evaluar(e).Alerta, Is.False);
        }

        [Test]
        public void Otros_sensores_de_estado_no_usan_umbrales_de_densidad()
        {
            // Bajada/presion/final de carrera: no se comparan contra sem/min.
            var e = new VxSurcoInput { EsEstado = true, EsTolvaVacia = false, Valor = 1, SembrandoActivo = true };
            var r = VxSurcoEvaluator.Evaluar(e);
            Assert.That(r.Estado, Is.EqualTo(VxSurcoEvaluator.Ok));
            Assert.That(r.Alerta, Is.False);
        }

        private static VxSurcoEstado VxSurcoEvaluar(double spm)
            => VxSurcoEvaluator.Evaluar(Sembrando(spm));
    }
}
