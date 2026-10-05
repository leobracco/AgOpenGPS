// ============================================================================
// VxRegistroTramosTests — tramos del registro VistaX por lote: cuándo cierra,
// sem/m ponderado por distancia, corrimiento lateral, índices ISO del tramo.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Services.VistaX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class VxRegistroTramosTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        // Máquina de 4 surcos a 0,5 m yendo al norte por la posición local N.
        private static VxMuestra Muestra(int seg, double n, bool sembrando = true, double semM = 5,
            double velKmh = 7.2, double e = 100)
        {
            var m = new VxMuestra
            {
                Utc = T0.AddSeconds(seg),
                Sembrando = sembrando,
                VelKmh = velKmh,
                Lat = -33.0 + n / 111320.0,
                Lon = -61.0,
                E = e,
                N = n,
                RumboRad = 0,
                DistEntreSurcosM = 0.5,
            };
            for (int b = 1; b <= 4; b++)
                m.Surcos.Add(new VxMuestraSurco { Tren = 1, Bajada = b, SemM = semM, Valida = true });
            return m;
        }

        [Test]
        public void Cierra_a_los_10_metros_con_sem_m_y_offsets()
        {
            var r = new VxRegistroTramos();
            bool listo = false;
            int seg = 0;
            // 2 m/s: 10 m en 5 pasos (el primero arranca el tramo sin distancia).
            for (double n = 1000; !listo && seg < 20; n += 2, seg++)
                listo = r.Agregar(Muestra(seg, n));
            Assert.That(listo, Is.True);
            Assert.That(seg, Is.EqualTo(6), "6 muestras: 1 de arranque + 5 pasos de 2 m");

            var t = r.Cerrar(null);
            Assert.That(t, Is.Not.Null);
            Assert.That(t.DistM, Is.EqualTo(10).Within(1e-9));
            Assert.That(t.VelKmh, Is.EqualTo(7.2).Within(1e-6));
            Assert.That(t.Surcos.Count, Is.EqualTo(4));
            Assert.That(t.Surcos[0].SemM, Is.EqualTo(5).Within(1e-9));
            Assert.That(t.Surcos[0].OffM, Is.EqualTo(-0.75).Within(1e-9));
            Assert.That(t.Surcos[3].OffM, Is.EqualTo(0.75).Within(1e-9));
            Assert.That(t.Surcos[0].Esp, Is.Null, "sin dt (nodo viejo) no hay índices");
            Assert.That(r.HayTramoEnCurso, Is.False);
        }

        [Test]
        public void El_sem_m_se_pondera_por_distancia_no_por_lectura()
        {
            var r = new VxRegistroTramos { LargoTramoM = 1000, MaxTramoS = 1000 };
            r.Agregar(Muestra(0, 0, semM: 0));
            r.Agregar(Muestra(1, 1, semM: 2));   // 1 m a 2 sem/m
            r.Agregar(Muestra(2, 10, semM: 8));  // 9 m a 8 sem/m
            var t = r.Cerrar(null);
            Assert.That(t.Surcos[0].SemM, Is.EqualTo((1 * 2 + 9 * 8) / 10.0).Within(1e-9));
        }

        [Test]
        public void Lecturas_no_validas_no_cuentan_para_el_sem_m()
        {
            var r = new VxRegistroTramos { LargoTramoM = 1000, MaxTramoS = 1000 };
            r.Agregar(Muestra(0, 0));
            var m = Muestra(1, 5, semM: 0);
            m.Surcos[1].Valida = false;            // surco 2: sección cortada
            r.Agregar(m);
            var t = r.Cerrar(null);
            Assert.That(t.Surcos.Count, Is.EqualTo(3), "el surco 2 no tuvo ninguna lectura válida");
        }

        [Test]
        public void Dejar_de_sembrar_cierra_el_pedazo_final_si_supera_el_minimo()
        {
            var r = new VxRegistroTramos();
            r.Agregar(Muestra(0, 0));
            r.Agregar(Muestra(1, 3));
            r.Agregar(Muestra(2, 6));
            Assert.That(r.Agregar(Muestra(3, 7, sembrando: false)), Is.True);
            var t = r.Cerrar(null);
            Assert.That(t.DistM, Is.EqualTo(6).Within(1e-9));
        }

        [Test]
        public void Un_pedazo_corto_al_parar_se_descarta()
        {
            var r = new VxRegistroTramos();
            r.Agregar(Muestra(0, 0));
            r.Agregar(Muestra(1, 1));
            Assert.That(r.Agregar(Muestra(2, 1, sembrando: false)), Is.False);
            Assert.That(r.HayTramoEnCurso, Is.False);
            Assert.That(r.Cerrar(null), Is.Null);
        }

        [Test]
        public void Un_salto_de_posicion_no_suma_metros()
        {
            var r = new VxRegistroTramos();
            r.Agregar(Muestra(0, 0));
            Assert.That(r.Agregar(Muestra(1, 500)), Is.False, "500 m en 1 s es un teleport");
            Assert.That(r.DistanciaEnCurso, Is.EqualTo(0));
        }

        [Test]
        public void Sin_posicion_local_integra_la_velocidad()
        {
            var r = new VxRegistroTramos();
            var a = Muestra(0, 0, e: 0); a.N = 0;
            r.Agregar(a);
            var b = Muestra(2, 0, velKmh: 18, e: 0); b.N = 0;   // 5 m/s × 2 s
            r.Agregar(b);
            Assert.That(r.DistanciaEnCurso, Is.EqualTo(10).Within(1e-9));
        }

        [Test]
        public void Muy_despacio_cierra_por_tiempo()
        {
            var r = new VxRegistroTramos();
            bool listo = false;
            int seg = 0;
            for (; seg <= 30 && !listo; seg++) listo = r.Agregar(Muestra(seg, seg * 0.2));
            Assert.That(listo, Is.True, "a 0,2 m/s cierra a los 30 s con 6 m");
            Assert.That(r.Cerrar(null).DistM, Is.EqualTo(6).Within(1e-6));
        }

        [Test]
        public void Los_indices_de_espaciamiento_van_al_surco_que_corresponde()
        {
            var r = new VxRegistroTramos();
            for (int s = 0; s < 6; s++) r.Agregar(Muestra(s, s * 2));
            var esp = new List<VxIndicesSurco>
            {
                new VxIndicesSurco { Tren = 1, Bajada = 3, Indices = new VxIndicesEspaciamiento
                    { NEspacios = 50, DoblesPct = 2, FallasPct = 4, SingulacionPct = 94, CvPct = 21 } },
                new VxIndicesSurco { Tren = 1, Bajada = 4, Indices = new VxIndicesEspaciamiento() }, // vacío
            };
            var t = r.Cerrar(esp);
            Assert.That(t.Surcos[2].Esp, Is.Not.Null);
            Assert.That(t.Surcos[2].Esp.SingulacionPct, Is.EqualTo(94));
            Assert.That(t.Surcos[3].Esp, Is.Null, "0 espacios = sin dato");
        }

        [Test]
        public void El_tramo_siguiente_arranca_donde_cerro_el_anterior()
        {
            var r = new VxRegistroTramos();
            int seg = 0; double n = 0;
            while (!r.Agregar(Muestra(seg, n))) { seg++; n += 2; }
            r.Cerrar(null);
            seg++; n += 2;
            r.Agregar(Muestra(seg, n));
            Assert.That(r.DistanciaEnCurso, Is.EqualTo(2).Within(1e-9), "el paso entre tramos no se pierde");
        }

        [Test]
        public void Rumbo_y_centroide()
        {
            var r = new VxRegistroTramos();
            var a = Muestra(0, 0); a.RumboRad = Math.PI / 2;
            var b = Muestra(5, 10); b.RumboRad = Math.PI / 2;
            r.Agregar(a); r.Agregar(b);
            var t = r.Cerrar(null);
            Assert.That(t.RumboDeg, Is.EqualTo(90).Within(1e-9));
            Assert.That(t.Lat, Is.EqualTo(-33.0 + 5 / 111320.0).Within(1e-9));
        }
    }
}
