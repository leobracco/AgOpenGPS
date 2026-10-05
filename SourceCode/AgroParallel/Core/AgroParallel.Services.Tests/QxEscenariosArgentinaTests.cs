// ============================================================================
// QxEscenariosArgentinaTests.cs — escenarios de sembradoras REALES del mercado
// argentino, fijados como tests para que la matemática de dosis no se rompa
// sin que salte un rojo. Cada escenario cruza las variantes que existen en el
// campo: motor hidráulico central vs eléctrico por surco, corte por sección
// con embragues (SectionX) vs surco por surco (LineX), kg/ha vs semillas/m.
//
//   1. Fina tipo Apache 27000: 33 surcos a 21 cm, UN motor hidráulico central
//      (eje solidario), trigo en kg/ha, 3 secciones de embrague.
//   2. Gruesa tipo Agrometal TX: 16 surcos a 52,5 cm, motor ELÉCTRICO POR
//      SURCO (corte surco a surco estilo LineX: sección = surco), maíz sem/m.
//   3. Tipo Crucianelli Gringa: 22 surcos a 42 cm, DOS tolvas de semilla
//      (11 surcos c/u) + fertilizante central, con secciones DESPAREJAS
//      (5/6/6/5) — el reparto real nunca es parejo.
//   4. Techo de dosis (hidráulico lento vs eléctrico rápido): el techo que ve
//      el operario tiene que calcularse con el ancho DEL MOTOR, no del total.
//
// Los números de las asserts están calculados A MANO en los comentarios: si
// mañana alguien cambia la fórmula, el test no lo perdona.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class QxEscenariosArgentinaTests
    {
        // ---- helpers ---------------------------------------------------------

        /// <summary>Implemento con secciones de tamaños arbitrarios (reparto
        /// real, no siempre parejo). surcosPorSeccion[i] = cuántos surcos tiene
        /// la sección i+1.</summary>
        private static ImplementoDto Impl(double espaciamiento, params int[] surcosPorSeccion)
        {
            var impl = new ImplementoDto { DistanciaEntreSurcosM = espaciamiento };
            int numero = 1;
            for (int s = 0; s < surcosPorSeccion.Length; s++)
                for (int i = 0; i < surcosPorSeccion[s]; i++)
                    impl.Surcos.Add(new SurcoDto { Numero = numero++, SeccionPilotX = s + 1 });
            impl.NumeroSurcos = numero - 1;
            impl.AnchoTotalM = impl.NumeroSurcos * espaciamiento;
            return impl;
        }

        private static List<int> SurcosDe(ImplementoDto impl, IList<int> secciones)
        {
            var mapa = Services.Common.SurcosPorSeccion.Construir(impl);
            var surcos = new List<int>();
            foreach (int s in secciones)
                if (mapa.ContainsKey(s)) surcos.AddRange(mapa[s]);
            return surcos;
        }

        private static List<int> Rango(int desde, int hasta)
        {
            var l = new List<int>();
            for (int i = desde; i <= hasta; i++) l.Add(i);
            return l;
        }

        // =======================================================================
        // 1. FINA — tipo Apache 27000: 33 surcos a 21 cm (6,93 m), UN motor
        //    hidráulico central a eje solidario, trigo 120 kg/ha, 3 secciones
        //    de embrague de 11 surcos. El motor gira a ancho TOTAL mientras al
        //    menos una sección esté abierta; el corte fino lo hacen los
        //    embragues (SectionX).
        // =======================================================================

        [Test]
        public void Fina_hidraulico_central_trigo_kg_ha()
        {
            var impl = Impl(0.21, 11, 11, 11);
            var cortes = Rango(1, 3);
            var surcos = SurcosDe(impl, cortes);

            Assert.That(QxAnchoMotor.Surcos(cortes, surcos), Is.EqualTo(33));
            double ancho = QxAnchoMotor.Resolver(cortes, surcos, impl, 3, impl.AnchoTotalM);
            Assert.That(ancho, Is.EqualTo(6.93).Within(0.001));

            // A mano: 120 kg/ha, 7 km/h = 1,9444 m/s, cal 45 g/pulso.
            // g/s = 120·1000·6,93·1,9444 / 10000 = 161,70  →  pps = 161,70/45 = 3,593
            double pps = QxPulseCalculator.Pps(new QxPulseInput
            {
                Dosis = 120, VelocidadKmh = 7, SeccionOn = true,
                EsSemillas = false, AnchoM = ancho, MeterCal = 45,
            });
            Assert.That(pps, Is.EqualTo(3.593).Within(0.01));
        }

        // =======================================================================
        // 2. GRUESA — tipo Agrometal TX: 16 surcos a 52,5 cm, motor ELÉCTRICO
        //    POR SURCO con corte surco a surco (LineX): en PilotX cada surco es
        //    su propia sección (16 secciones de 1). Maíz 3,5 sem/m.
        // =======================================================================

        [Test]
        public void Gruesa_electrico_por_surco_maiz_sem_m()
        {
            // 16 secciones de 1 surco (sección = surco).
            var impl = Impl(0.525, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);

            // El motor del surco 7 solo tiene su sección.
            var cortes = new List<int> { 7 };
            var surcos = SurcosDe(impl, cortes);
            Assert.That(QxAnchoMotor.Surcos(cortes, surcos), Is.EqualTo(1));

            // A mano: 3,5 sem/m, 8 km/h = 2,2222 m/s, plato 30 sem/vuelta,
            // 24 dientes → 1,25 sem/pulso. sem/s = 3,5·2,2222·1 = 7,7778
            // pps = 7,7778 / 1,25 = 6,222
            double pps = QxPulseCalculator.Pps(new QxPulseInput
            {
                Dosis = 3.5, VelocidadKmh = 8, SeccionOn = true, EsSemillas = true,
                Surcos = 1, SemillasVuelta = 30, DientesEngranaje = 24,
            });
            Assert.That(pps, Is.EqualTo(6.222).Within(0.01));

            // Su surco entra en zona ya sembrada (anti-solape apaga SU sección)
            // → SU motor se planta; los otros 15 ni se enteran.
            double ppsCortado = QxPulseCalculator.Pps(new QxPulseInput
            {
                Dosis = 3.5, VelocidadKmh = 8, SeccionOn = false, EsSemillas = true,
                Surcos = 1, SemillasVuelta = 30, DientesEngranaje = 24,
            });
            Assert.That(ppsCortado, Is.EqualTo(0));
        }

        // =======================================================================
        // 3. Tipo Crucianelli Gringa: 22 surcos a 42 cm (9,24 m), secciones
        //    DESPAREJAS 5/6/6/5. Dos tolvas de semilla (secciones 1-2 y 3-4,
        //    11 surcos cada una) + fertilizante central (las 4 secciones).
        // =======================================================================

        [Test]
        public void Gringa_dos_tolvas_desparejas_mas_ferti_central()
        {
            var impl = Impl(0.42, 5, 6, 6, 5);

            // Tolva A: secciones 1-2 → 5+6 = 11 surcos → 4,62 m.
            var tolvaA = SurcosDe(impl, Rango(1, 2));
            Assert.That(QxAnchoMotor.Surcos(Rango(1, 2), tolvaA), Is.EqualTo(11));
            Assert.That(QxAnchoMotor.Resolver(Rango(1, 2), tolvaA, impl, 4, 9.24),
                        Is.EqualTo(4.62).Within(0.001));

            // Tolva B: secciones 3-4 → 6+5 = 11 surcos → mismo ancho.
            var tolvaB = SurcosDe(impl, Rango(3, 4));
            Assert.That(QxAnchoMotor.Surcos(Rango(3, 4), tolvaB), Is.EqualTo(11));

            // Ferti: las 4 secciones → 22 surcos → ancho total.
            var ferti = SurcosDe(impl, Rango(1, 4));
            Assert.That(QxAnchoMotor.Resolver(Rango(1, 4), ferti, impl, 4, 9.24),
                        Is.EqualTo(9.24).Within(0.001));
        }

        [Test]
        public void Gringa_soja_kg_ha_cada_tolva_dosifica_su_mitad()
        {
            // Soja a chorrillo en kg/ha: 80 kg/ha, 8 km/h = 2,2222 m/s,
            // cal 60 g/pulso, tolva de 4,62 m.
            // g/s = 80·1000·4,62·2,2222 / 10000 = 82,13  →  pps = 1,369
            var impl = Impl(0.42, 5, 6, 6, 5);
            var tolvaA = SurcosDe(impl, Rango(1, 2));
            double ancho = QxAnchoMotor.Resolver(Rango(1, 2), tolvaA, impl, 4, 9.24);

            double pps = QxPulseCalculator.Pps(new QxPulseInput
            {
                Dosis = 80, VelocidadKmh = 8, SeccionOn = true,
                EsSemillas = false, AnchoM = ancho, MeterCal = 60,
            });
            Assert.That(pps, Is.EqualTo(1.369).Within(0.01));
        }

        // =======================================================================
        // 4. Techo de dosis por VELOCIDAD — hidráulico lento vs eléctrico
        //    rápido. Va por el builder entero (lo que ve el operario) con el
        //    implemento en el contexto: el techo se calcula con el ancho DEL
        //    MOTOR. Con el bug del ancho total, el techo de una tolva parcial
        //    daba la MITAD del real y el operario frenaba el tractor de gusto.
        // =======================================================================

        [Test]
        public void Techo_de_dosis_usa_el_ancho_del_motor_no_el_total()
        {
            var impl = Impl(0.42, 5, 6, 6, 5);

            var motor = new QxMotorConfig
            {
                Nombre = "Tolva A",
                UnidadDosis = "kg_ha",
                DosisFija = 80,
                MeterCal = 60,
                DientesEngranaje = 24,
                MaxHz = 20,                        // hidráulico: válvula lenta
                Cortes = new List<int> { 1, 2 },   // 11 surcos = 4,62 m
            };
            var cfg = new MotoresConfig
            {
                Nodos = new List<QxNodoConfig>
                {
                    new QxNodoConfig { Uid = "AABBCC", Habilitado = true, Motores = new[] { motor } },
                },
            };
            var ctx = new QxRuntimeContexto
            {
                VelocidadKmh = 8,
                AnchoTotalM = 9.24,
                Implemento = impl,
                TotalSecciones = 4,
            };

            // A mano: techo g/s = 20 pps · 60 g = 1200 g/s.
            // kg/ha máx = 1200·10000 / (4,62·2,2222·1000) = 1168,8
            var snap = QxRuntimeBuilder.Build(cfg, ctx);
            Assert.That(snap.Motores[0].MaxDoseAtCurrentSpeed, Is.EqualTo(1168.8).Within(1.0));

            // Eléctrico rápido: mismo motor con MaxHz 60 → techo 3x.
            motor.MaxHz = 60;
            var snapE = QxRuntimeBuilder.Build(cfg, ctx);
            Assert.That(snapE.Motores[0].MaxDoseAtCurrentSpeed,
                        Is.EqualTo(3 * 1168.8).Within(3.0));
        }
    }
}
