// ============================================================================
// QxAnchoMotorTests.cs — el ancho y los surcos REALES de cada motor QuantiX.
//
// Fija los dos bugs de dosis con motores parciales (2026-08-08):
//   · kg/ha usaba SIEMPRE el ancho total → motor con la mitad de los surcos
//     dosificaba al DOBLE.
//   · sem/m contaba las SECCIONES como surcos → 4 secciones de 12 surcos
//     dosificaban 12 VECES de menos.
//
// Los escenarios son los reales del usuario:
//   A) 14 surcos: 2 motores de semilla (7 c/u) + 1 de fertilizante (los 14).
//   B) 96 surcos, 2 tolvas, 1 motor por tren, cada tren 4 secciones de 12.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class QxAnchoMotorTests
    {
        // ---- helpers ---------------------------------------------------------

        /// <summary>Implemento de N surcos repartidos parejo en S secciones.</summary>
        private static ImplementoDto Impl(int surcos, int secciones, double espaciamiento)
        {
            var impl = new ImplementoDto
            {
                NumeroSurcos = surcos,
                DistanciaEntreSurcosM = espaciamiento,
                AnchoTotalM = surcos * espaciamiento,
            };
            int porSeccion = surcos / secciones;
            for (int n = 1; n <= surcos; n++)
            {
                impl.Surcos.Add(new SurcoDto
                {
                    Numero = n,
                    SeccionPilotX = ((n - 1) / porSeccion) + 1,
                });
            }
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

        private static List<int> Secciones(int desde, int hasta)
        {
            var l = new List<int>();
            for (int i = desde; i <= hasta; i++) l.Add(i);
            return l;
        }

        // ---- Escenario A: 14 surcos, 0.525 m, secciones 1:1 con surcos -------

        [Test]
        public void Ferti_14_de_14_usa_el_ancho_total()
        {
            var impl = Impl(14, 14, 0.525);
            var cortes = Secciones(1, 14);
            double ancho = QxAnchoMotor.Resolver(cortes, SurcosDe(impl, cortes), impl, 14, 7.35);
            Assert.That(ancho, Is.EqualTo(7.35).Within(0.001));
        }

        [Test]
        public void Semilla_7_de_14_usa_la_mitad_del_ancho_no_el_total()
        {
            var impl = Impl(14, 14, 0.525);
            var cortes = Secciones(1, 7);
            double ancho = QxAnchoMotor.Resolver(cortes, SurcosDe(impl, cortes), impl, 14, 7.35);
            Assert.That(ancho, Is.EqualTo(3.675).Within(0.001));   // 7 × 0.525 — antes daba 7.35
        }

        [Test]
        public void Semilla_7_de_14_en_kg_ha_dosifica_la_mitad_que_antes()
        {
            // El bug en plata: 100 kg/ha a 8 km/h, meter_cal 50 g/pulso.
            var impl = Impl(14, 14, 0.525);
            var cortes = Secciones(1, 7);
            double ancho = QxAnchoMotor.Resolver(cortes, SurcosDe(impl, cortes), impl, 14, 7.35);

            double ppsBien = QxPulseCalculator.Pps(new QxPulseInput
            {
                Dosis = 100, VelocidadKmh = 8, SeccionOn = true,
                EsSemillas = false, AnchoM = ancho, MeterCal = 50,
            });
            double ppsMal = QxPulseCalculator.Pps(new QxPulseInput
            {
                Dosis = 100, VelocidadKmh = 8, SeccionOn = true,
                EsSemillas = false, AnchoM = 7.35, MeterCal = 50,   // ancho total (bug)
            });
            Assert.That(ppsBien, Is.EqualTo(ppsMal / 2).Within(0.001));
        }

        // ---- Escenario B: 96 surcos, 2 tolvas, 2 trenes, 4 secciones c/u -----

        [Test]
        public void Tolva_de_96_surcos_4_secciones_de_12_cuenta_48_surcos_no_4()
        {
            var impl = Impl(96, 8, 0.21);
            var cortesTolvaA = Secciones(1, 4);          // tren delantero
            var surcos = SurcosDe(impl, cortesTolvaA);

            Assert.That(QxAnchoMotor.Surcos(cortesTolvaA, surcos), Is.EqualTo(48)); // antes: 4
            double ancho = QxAnchoMotor.Resolver(cortesTolvaA, surcos, impl, 8, 96 * 0.21);
            Assert.That(ancho, Is.EqualTo(48 * 0.21).Within(0.001));
        }

        [Test]
        public void Tolva_96_en_sem_m_pide_12_veces_mas_pulsos_que_con_el_bug()
        {
            var impl = Impl(96, 8, 0.21);
            var cortes = Secciones(5, 8);                // tren trasero
            var surcos = SurcosDe(impl, cortes);

            var e = new QxPulseInput
            {
                Dosis = 30, VelocidadKmh = 7, SeccionOn = true, EsSemillas = true,
                SemillasVuelta = 100, DientesEngranaje = 24,
            };
            e.Surcos = QxAnchoMotor.Surcos(cortes, surcos);   // 48
            double ppsBien = QxPulseCalculator.Pps(e);
            e.Surcos = cortes.Count;                           // 4 (bug)
            double ppsMal = QxPulseCalculator.Pps(e);

            Assert.That(ppsBien, Is.EqualTo(ppsMal * 12).Within(0.01));
        }

        // ---- Fallbacks -------------------------------------------------------

        [Test]
        public void Sin_cortes_alimenta_todo_el_implemento()
        {
            Assert.That(QxAnchoMotor.Resolver(null, null, null, 8, 7.35), Is.EqualTo(7.35));
            Assert.That(QxAnchoMotor.Resolver(new List<int>(), null, null, 8, 7.35), Is.EqualTo(7.35));
            Assert.That(QxAnchoMotor.Surcos(null, null), Is.EqualTo(1));
        }

        [Test]
        public void Sin_implemento_cae_a_proporcional_por_secciones()
        {
            // 4 de 8 secciones sobre 7 m = 3.5 m.
            double ancho = QxAnchoMotor.Resolver(Secciones(1, 4), null, null, 8, 7.0);
            Assert.That(ancho, Is.EqualTo(3.5).Within(0.001));
        }

        [Test]
        public void Sin_implemento_ni_secciones_mantiene_el_historico()
        {
            double ancho = QxAnchoMotor.Resolver(Secciones(1, 4), null, null, 0, 7.0);
            Assert.That(ancho, Is.EqualTo(7.0));
            // rigs viejos: sección ≈ surco → Cortes.Count sigue valiendo
            Assert.That(QxAnchoMotor.Surcos(Secciones(1, 4), null), Is.EqualTo(4));
        }

        [Test]
        public void Espaciamiento_cero_se_deriva_del_ancho_total()
        {
            var impl = Impl(14, 14, 0);                 // sin espaciamiento cargado
            impl.AnchoTotalM = 7.35;
            var cortes = Secciones(1, 7);
            double ancho = QxAnchoMotor.Resolver(cortes, SurcosDe(impl, cortes), impl, 14, 7.35);
            Assert.That(ancho, Is.EqualTo(3.675).Within(0.001));   // 7 × (7.35/14)
        }

        [Test]
        public void Secciones_sin_surcos_mapeados_caen_a_proporcional()
        {
            // Implemento cargado pero sus surcos no mapean a estas secciones.
            var impl = Impl(14, 14, 0.525);
            double ancho = QxAnchoMotor.Resolver(Secciones(20, 23), new List<int>(), impl, 8, 7.0);
            Assert.That(ancho, Is.EqualTo(3.5).Within(0.001));
        }
    }
}
