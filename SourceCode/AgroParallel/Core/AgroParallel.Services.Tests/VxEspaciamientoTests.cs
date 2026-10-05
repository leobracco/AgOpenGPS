// ============================================================================
// VxEspaciamientoTests — dobles / fallas / CV por surco (ISO 7256-1).
//
// El generador arma una siembra con estadística CONOCIDA (cantidades exactas
// de dobles y fallas, simples con desvío normal conocido) y la convierte a lo
// que manda el firmware v3.1: intervalos entre semillas en 0,1 ms a la
// velocidad del tractor. Los índices tienen que volver ±0,5 puntos.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgroParallel.Services.VistaX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class VxEspaciamientoTests
    {
        private const double Tol = 0.5;   // puntos porcentuales

        // ── Generador ──────────────────────────────────────────────────────

        /// <summary>Espaciamientos normalizados (x / Xref) con exactamente
        /// pDobles·n dobles, pFallas·n fallas y simples ~ N(1, cv) truncada a
        /// (0,5 ; 1,5]. Mezclados con semilla fija → test determinístico.</summary>
        private static List<double> Siembra(int n, double pDobles, double pFallas, double cv, int seed)
        {
            var rnd = new Random(seed);
            int nd = (int)Math.Round(n * pDobles);
            int nf = (int)Math.Round(n * pFallas);
            var r = new List<double>(n);
            for (int i = 0; i < nd; i++) r.Add(0.05 + rnd.NextDouble() * 0.40);        // doble
            for (int i = 0; i < nf; i++) r.Add(1.6 + rnd.NextDouble() * 0.8);          // falla (≈ 2·Xref)
            while (r.Count < n)
            {
                double z = Normal(rnd);
                double v = 1.0 + cv * z;
                if (v > 0.5 && v <= 1.5) r.Add(v);                                      // simple
            }
            // Fisher-Yates
            for (int i = r.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                double t = r[i]; r[i] = r[j]; r[j] = t;
            }
            return r;
        }

        private static double Normal(Random rnd)
        {
            double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// <summary>Distancia → intervalo del firmware (0,1 ms, uint16 saturado).</summary>
        private static int Dt01(double xM, double velKmh)
        {
            double s = xM / (velKmh / 3.6);
            double u = Math.Round(s * 10000.0);
            return u >= VxEspaciamiento.DtHueco ? VxEspaciamiento.DtHueco : (int)u;
        }

        private static void AssertIndices(VxIndicesEspaciamiento ix, double d, double f, double cv)
        {
            Assert.That(ix.DoblesPct, Is.EqualTo(d).Within(Tol), "dobles");
            Assert.That(ix.FallasPct, Is.EqualTo(f).Within(Tol), "fallas");
            Assert.That(ix.SingulacionPct, Is.EqualTo(100 - d - f).Within(Tol), "singulación");
            Assert.That(ix.CvPct, Is.EqualTo(cv).Within(Tol), "CV");
        }

        // ── Clasificación ──────────────────────────────────────────────────

        [TestCase(0.10, VxClaseEspacio.Doble)]
        [TestCase(0.50, VxClaseEspacio.Doble)]     // ≤ 0,5·Xref es doble (borde incluido)
        [TestCase(0.51, VxClaseEspacio.Simple)]
        [TestCase(1.00, VxClaseEspacio.Simple)]
        [TestCase(1.50, VxClaseEspacio.Simple)]    // > 1,5·Xref es falla (borde excluido)
        [TestCase(1.51, VxClaseEspacio.Falla)]
        public void Clasifica_segun_ISO_7256(double r, VxClaseEspacio esperado)
        {
            Assert.That(VxEspaciamiento.Clasificar(r * 0.2, 0.2), Is.EqualTo(esperado));
        }

        // ── Estadística conocida ───────────────────────────────────────────

        [Test]
        public void Maiz_con_estadistica_conocida_da_los_indices_esperados()
        {
            const double xref = 1.0 / 5.0;      // maíz 5 sem/m → 20 cm
            const double vel = 8.0;
            var esp = new VxEspaciamiento();
            foreach (var r in Siembra(10000, 0.02, 0.03, 0.15, 1))
                Assert.That(esp.Agregar(Dt01(r * xref, vel), vel, xref), Is.True);

            AssertIndices(esp.Pasada(), 2, 3, 15);
            AssertIndices(esp.Lote(), 2, 3, 15);
            Assert.That(esp.Pasada().NEspacios, Is.EqualTo(10000));
        }

        [Test]
        public void Soja_tambien_resuelve_a_44_semillas_por_segundo()
        {
            // Soja 14 sem/m a 11 km/h ≈ 43 sem/s: intervalo medio ≈ 23 ms.
            const double xref = 1.0 / 14.0;
            const double vel = 11.0;
            var esp = new VxEspaciamiento();
            foreach (var r in Siembra(10000, 0.02, 0.03, 0.15, 2))
                esp.Agregar(Dt01(r * xref, vel), vel, xref);
            AssertIndices(esp.Pasada(), 2, 3, 15);
        }

        [Test]
        public void La_ventana_movil_solo_mira_los_ultimos_espacios()
        {
            const double xref = 0.2, vel = 8.0;
            var esp = new VxEspaciamiento(300);
            // 300 espacios todos fallas...
            for (int i = 0; i < 300; i++) esp.Agregar(Dt01(2 * xref, vel), vel, xref);
            // ...y después 300 perfectos: la ventana se limpia, el acumulado no.
            for (int i = 0; i < 300; i++) esp.Agregar(Dt01(xref, vel), vel, xref);

            var v = esp.Ventana();
            Assert.That(v.NEspacios, Is.EqualTo(300));
            Assert.That(v.FallasPct, Is.EqualTo(0));
            Assert.That(v.SingulacionPct, Is.EqualTo(100));
            Assert.That(esp.Pasada().FallasPct, Is.EqualTo(50).Within(0.01));
        }

        // ── Robustez ───────────────────────────────────────────────────────

        [Test]
        public void Mensajes_MQTT_perdidos_no_crean_fallas()
        {
            // El firmware mide cada intervalo entre dos semillas consecutivas:
            // perder un mensaje pierde MUESTRAS, no inventa huecos. Se tira
            // ~15 % de los mensajes (bloques de 11 intervalos = 250 ms de soja).
            const double xref = 1.0 / 14.0, vel = 11.0;
            var rnd = new Random(7);
            var esp = new VxEspaciamiento();
            var siembra = Siembra(20000, 0.02, 0.03, 0.15, 3);
            int perdidos = 0;
            for (int i = 0; i < siembra.Count; i += 11)
            {
                if (rnd.NextDouble() < 0.15) { perdidos++; continue; }   // mensaje que no llegó
                for (int k = i; k < Math.Min(i + 11, siembra.Count); k++)
                    esp.Agregar(Dt01(siembra[k] * xref, vel), vel, xref);
            }
            Assert.That(perdidos, Is.GreaterThan(100));
            var p = esp.Pasada();
            Assert.That(p.FallasPct, Is.EqualTo(3).Within(Tol), "los mensajes perdidos inflaron las fallas");
            AssertIndices(p, 2, 3, 15);
        }

        [Test]
        public void Los_cambios_de_velocidad_no_rompen_los_indices()
        {
            // La velocidad oscila 4 ↔ 10 km/h. El intervalo se genera con la
            // velocidad REAL de esa semilla y se convierte con la del mensaje
            // (la última del bloque de 250 ms), como pasa en la PC.
            const double xref = 0.2;
            var esp = new VxEspaciamiento();
            var siembra = Siembra(10000, 0.02, 0.03, 0.15, 4);
            double t = 0;
            var bloque = new List<int>();
            double velMsg = 7;
            for (int i = 0; i < siembra.Count; i++)
            {
                double vel = 7 + 3 * Math.Sin(2 * Math.PI * t / 30.0);   // período 30 s
                double x = siembra[i] * xref;
                bloque.Add(Dt01(x, vel));
                t += x / (vel / 3.6);
                velMsg = vel;
                if (bloque.Count == 4)                                  // 4 sem por mensaje (maíz)
                {
                    foreach (var dt in bloque) esp.Agregar(dt, velMsg, xref);
                    bloque.Clear();
                }
            }
            AssertIndices(esp.Pasada(), 2, 3, 15);
        }

        [Test]
        public void Huecos_y_velocidad_baja_se_descartan()
        {
            const double xref = 0.2;
            var esp = new VxEspaciamiento();
            Assert.That(esp.Agregar(VxEspaciamiento.DtHueco, 8, xref), Is.False, "0xFFFF = hueco (parada)");
            Assert.That(esp.Agregar(Dt01(xref, 1.5), 1.5, xref), Is.False, "< 2 km/h no se mide");
            Assert.That(esp.Agregar(0, 8, xref), Is.False, "intervalo 0 no es un espacio");
            Assert.That(esp.Pasada().NEspacios, Is.EqualTo(0));
            Assert.That(esp.Pasada().SingulacionPct, Is.EqualTo(0), "sin espacios no hay índice");
        }

        [Test]
        public void Densidad_que_el_sensor_no_resuelve_no_se_clasifica()
        {
            // Trigo a chorrillo: 270 sem/m → 3,7 mm entre semillas. Por debajo
            // de ~10 ms entre semillas el filtro de 5 ms del nodo se las come.
            var esp = new VxEspaciamiento();
            Assert.That(esp.Agregar(20, 8, 1.0 / 270.0), Is.False);
        }

        [Test]
        public void Sin_objetivo_usa_la_mediana_como_referencia()
        {
            const double xreal = 0.2, vel = 8.0;
            var esp = new VxEspaciamiento();
            int aceptados = 0;
            foreach (var r in Siembra(10000, 0.02, 0.03, 0.15, 5))
                if (esp.Agregar(Dt01(r * xreal, vel), vel, 0)) aceptados++;
            // Los primeros espacios arman la mediana y no se clasifican.
            Assert.That(aceptados, Is.GreaterThan(9900));
            AssertIndices(esp.Pasada(), 2, 3, 15);
        }

        [Test]
        public void ResetPasada_no_toca_el_lote()
        {
            const double xref = 0.2, vel = 8.0;
            var esp = new VxEspaciamiento();
            for (int i = 0; i < 100; i++) esp.Agregar(Dt01(xref, vel), vel, xref);
            esp.ResetPasada();
            Assert.That(esp.Pasada().NEspacios, Is.EqualTo(0));
            Assert.That(esp.Lote().NEspacios, Is.EqualTo(100));
            esp.ResetLote();
            Assert.That(esp.Lote().NEspacios, Is.EqualTo(0));
        }

        // ── Alarma amarilla ────────────────────────────────────────────────

        [Test]
        public void Alarma_singulacion_pide_20_segundos_sostenidos()
        {
            var a = new VxAlarmaSingulacion();
            var t0 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
            // objetivo 97, margen 3 → umbral 94.
            Assert.That(a.Evaluar(90, 300, 97, true, t0), Is.False);
            Assert.That(a.Evaluar(90, 300, 97, true, t0.AddSeconds(19)), Is.False);
            Assert.That(a.Evaluar(90, 300, 97, true, t0.AddSeconds(20)), Is.True);
            // Vuelve arriba del umbral → se apaga y el reloj arranca de cero.
            Assert.That(a.Evaluar(95, 300, 97, true, t0.AddSeconds(21)), Is.False);
            Assert.That(a.Evaluar(90, 300, 97, true, t0.AddSeconds(22)), Is.False);
            Assert.That(a.Evaluar(90, 300, 97, true, t0.AddSeconds(41)), Is.False);
            Assert.That(a.Evaluar(90, 300, 97, true, t0.AddSeconds(42)), Is.True);
        }

        [Test]
        public void Alarma_singulacion_no_suena_parado_ni_con_pocos_espacios()
        {
            var a = new VxAlarmaSingulacion();
            var t0 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
            a.Evaluar(80, 300, 97, false, t0);
            Assert.That(a.Evaluar(80, 300, 97, false, t0.AddSeconds(30)), Is.False, "sin sembrar no hay alarma");
            a.Evaluar(80, 20, 97, true, t0.AddSeconds(31));
            Assert.That(a.Evaluar(80, 20, 97, true, t0.AddSeconds(60)), Is.False, "pocos espacios: índice sin peso");
            // Justo en el umbral (94) no alarma: es "menor que".
            a.Evaluar(94, 300, 97, true, t0.AddSeconds(61));
            Assert.That(a.Evaluar(94, 300, 97, true, t0.AddSeconds(90)), Is.False);
        }

        // ── Parser de la telemetría v2 ─────────────────────────────────────

        [Test]
        public void LeerDt_lee_el_array_y_tolera_payload_viejo()
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(
                "{\"cable\":1,\"valor\":40,\"raw\":10,\"acum\":99,\"dt\":[320,65535,330],\"dt_lost\":2}"))
            {
                var r = VxEspaciamiento.LeerDt(doc.RootElement, out int lost);
                Assert.That(r, Is.EqualTo(new[] { 320, 65535, 330 }));
                Assert.That(lost, Is.EqualTo(2));
            }
            using (var doc = System.Text.Json.JsonDocument.Parse("{\"cable\":1,\"valor\":40,\"raw\":10,\"acum\":99}"))
            {
                Assert.That(VxEspaciamiento.LeerDt(doc.RootElement, out int lost), Is.Null, "firmware < 3.1 no manda dt");
                Assert.That(lost, Is.EqualTo(0));
            }
        }

        [Test]
        public void TomarTramo_devuelve_lo_del_tramo_y_lo_vacia_sin_tocar_lote_ni_ventana()
        {
            var e = new VxEspaciamiento();
            // 8 km/h, Xref 20 cm → 900 = simple, 300 = doble.
            for (int i = 0; i < 9; i++) e.Agregar(900, 8, 0.2);
            e.Agregar(300, 8, 0.2);
            var t = e.TomarTramo();
            Assert.That(t.NEspacios, Is.EqualTo(10));
            Assert.That(t.DoblesPct, Is.EqualTo(10).Within(1e-9));
            Assert.That(e.Tramo().NEspacios, Is.EqualTo(0));

            e.Agregar(900, 8, 0.2);
            Assert.That(e.TomarTramo().NEspacios, Is.EqualTo(1));
            Assert.That(e.Lote().NEspacios, Is.EqualTo(11));
            Assert.That(e.Ventana().NEspacios, Is.EqualTo(11));
            e.Agregar(900, 8, 0.2);
            e.ResetTramo();
            Assert.That(e.Tramo().NEspacios, Is.EqualTo(0));
        }
    }
}
