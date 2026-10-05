// ============================================================================
// VxRegistroArchivoTests — disco del registro VistaX por lote: formato NDJSON
// ida y vuelta, partes de N tramos, reapertura, resumen por surco, CSV y SHP.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgroParallel.Services.VistaX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class VxRegistroArchivoTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vx_registro_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private static VxTramo Tramo(int i, double semM = 5, VxIndicesEspaciamiento esp = null)
        {
            var fin = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc).AddSeconds(5 * (i + 1));
            var t = new VxTramo
            {
                InicioUtc = fin.AddSeconds(-5),
                FinUtc = fin,
                Lat = -33.1234567 + i * 1e-4,
                Lon = -61.7654321,
                RumboDeg = 90,
                DistM = 10,
                VelKmh = 7.2,
            };
            t.Surcos.Add(new VxTramoSurco { Tren = 1, Bajada = 1, OffM = -0.26, SemM = semM, Esp = esp });
            t.Surcos.Add(new VxTramoSurco { Tren = 1, Bajada = 2, OffM = 0.26, SemM = -1 });
            return t;
        }

        private static VxIndicesEspaciamiento Ix(int n, double d, double f, double cv)
            => new VxIndicesEspaciamiento { NEspacios = n, DoblesPct = d, FallasPct = f, SingulacionPct = 100 - d - f, CvPct = cv };

        [Test]
        public void Tramo_ida_y_vuelta_por_NDJSON()
        {
            var t = Tramo(0, 4.567, Ix(48, 2.1, 4.2, 18.36));
            string l = VxRegistroArchivo.SerializarTramo(t);
            Assert.That(l, Does.Contain("\"s\":[[1,1,-0.26,4.57,93.7,2.1,4.2,18.4,48],[1,2,0.26,null,null,null,null,null,0]]"));
            var r = VxRegistroArchivo.ParsearTramo(l);
            Assert.That(r, Is.Not.Null);
            Assert.That(r.FinUtc, Is.EqualTo(t.FinUtc));
            Assert.That(r.InicioUtc, Is.EqualTo(t.InicioUtc));
            Assert.That(r.Lat, Is.EqualTo(-33.1234567).Within(1e-9));
            Assert.That(r.Surcos[0].SemM, Is.EqualTo(4.57).Within(1e-9));
            Assert.That(r.Surcos[0].Esp.NEspacios, Is.EqualTo(48));
            Assert.That(r.Surcos[0].Esp.FallasPct, Is.EqualTo(4.2).Within(1e-9));
            Assert.That(r.Surcos[1].SemM, Is.EqualTo(-1));
            Assert.That(r.Surcos[1].Esp, Is.Null);
        }

        [Test]
        public void Cabecera_y_basura_no_son_tramos()
        {
            Assert.That(VxRegistroArchivo.ParsearTramo(VxRegistroArchivo.Cabecera("Lote \"A\"", 1)), Is.Null);
            Assert.That(VxRegistroArchivo.ParsearTramo("{\"t\":\"2026-10-03T12:0"), Is.Null, "línea cortada por apagón");
            Assert.That(VxRegistroArchivo.ParsearTramo(""), Is.Null);
        }

        [Test]
        public void No_crea_nada_hasta_el_primer_tramo()
        {
            var a = new VxRegistroArchivo(_dir, "L1");
            Assert.That(Directory.Exists(_dir), Is.False);
            a.Escribir(Tramo(0));
            Assert.That(File.Exists(Path.Combine(_dir, "vistax_surcos_0001.ndjson")), Is.True);
        }

        [Test]
        public void Parte_nueva_cada_N_tramos_con_cabecera_propia()
        {
            var a = new VxRegistroArchivo(_dir, "L1", tramosPorParte: 3);
            for (int i = 0; i < 7; i++) a.Escribir(Tramo(i));
            var partes = VxRegistroArchivo.Partes(_dir);
            Assert.That(partes.Select(Path.GetFileName), Is.EqualTo(new[]
                { "vistax_surcos_0001.ndjson", "vistax_surcos_0002.ndjson", "vistax_surcos_0003.ndjson" }));
            var lineas = File.ReadAllLines(partes[1]);
            Assert.That(lineas.Length, Is.EqualTo(4), "cabecera + 3 tramos");
            Assert.That(lineas[0], Does.StartWith("{\"schema\":\"agp.vistax.surcos/1\",\"lote\":\"L1\",\"parte\":2"));
            Assert.That(VxRegistroArchivo.LeerTramos(_dir).Count(), Is.EqualTo(7));
        }

        [Test]
        public void Reabrir_el_lote_sigue_en_la_ultima_parte_y_recupera_el_resumen()
        {
            var a = new VxRegistroArchivo(_dir, "L1", tramosPorParte: 3);
            for (int i = 0; i < 4; i++) a.Escribir(Tramo(i));

            var b = new VxRegistroArchivo(_dir, "L1", tramosPorParte: 3);
            Assert.That(b.ParteActual, Is.EqualTo(2));
            Assert.That(b.Resumen.Tramos, Is.EqualTo(4));
            b.Escribir(Tramo(4));
            b.Escribir(Tramo(5));
            b.Escribir(Tramo(6));
            Assert.That(VxRegistroArchivo.Partes(_dir).Count, Is.EqualTo(3), "la parte 2 se completó con 3 y abrió la 3");
        }

        [Test]
        public void Resumen_pondera_dobles_y_fallas_por_espacios_y_sem_m_por_distancia()
        {
            var r = new VxResumenLote { Lote = "L1" };
            var t1 = Tramo(0, 4, Ix(100, 2, 4, 20));
            var t2 = Tramo(1, 6, Ix(300, 6, 0, 10));
            t2.DistM = 30;
            r.Sumar(t1);
            r.Sumar(t2);
            var s = r.Surcos[0];
            Assert.That(s.SemM, Is.EqualTo((4 * 10 + 6 * 30) / 40.0).Within(1e-9));
            Assert.That(s.NEspacios, Is.EqualTo(400));
            Assert.That(s.DoblesPct, Is.EqualTo((2 * 100 + 6 * 300) / 400.0).Within(1e-9));
            Assert.That(s.FallasPct, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(s.SingulacionPct, Is.EqualTo(100 - 5 - 1).Within(1e-9));
            Assert.That(s.CvPct, Is.EqualTo(12.5).Within(1e-9));
            Assert.That(r.Surcos[1].SemM, Is.EqualTo(-1), "surco sin lecturas");
            Assert.That(r.DistM, Is.EqualTo(40));
            Assert.That(r.PromedioSingulacion(), Is.EqualTo(94).Within(1e-9), "solo surcos con espaciamiento");
        }

        [Test]
        public void Resumen_JSON_ida_y_vuelta_y_CSV_con_coma_decimal()
        {
            var a = new VxRegistroArchivo(_dir, "Lote Norte");
            a.Escribir(Tramo(0, 4.25, Ix(100, 2, 4, 20.04)));
            a.GuardarResumen(new DateTime(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc));
            string json = File.ReadAllText(Path.Combine(_dir, "resumen.json"));
            Assert.That(json, Does.Contain("\"schema\":\"agp.vistax.resumen/1\""));
            var r = VxResumenLote.FromJson(json);
            Assert.That(r.Lote, Is.EqualTo("Lote Norte"));
            Assert.That(r.Tramos, Is.EqualTo(1));
            Assert.That(r.Surcos[0].SemM, Is.EqualTo(4.25).Within(1e-9));
            Assert.That(r.Surcos[0].SingulacionPct, Is.EqualTo(94).Within(1e-9));

            string csv = a.Resumen.ToCsv();
            Assert.That(csv, Does.StartWith("Tren;Surco;Metros;sem/m;"));
            Assert.That(csv, Does.Contain("1;1;10;4,25;94,0;2,0;4,0;20,0;100"));
            Assert.That(csv, Does.Contain("1;2;0;;;;;;0"));
        }

        [Test]
        public void Resumen_de_carpeta_vacia_es_null()
        {
            Assert.That(VxRegistroArchivo.ResumenDeCarpeta(_dir, "L1"), Is.Null);
        }

        [Test]
        public void SHP_un_punto_por_surco_y_tramo_corrido_a_la_derecha_del_rumbo()
        {
            var a = new VxRegistroArchivo(_dir, "L1");
            a.Escribir(Tramo(0, 5, Ix(50, 1, 2, 15)));
            a.Escribir(Tramo(1));
            int n = a.ExportarShp();
            Assert.That(n, Is.EqualTo(4));
            Assert.That(File.Exists(Path.Combine(_dir, "vistax_surcos.shp")), Is.True);
            Assert.That(File.Exists(Path.Combine(_dir, "vistax_surcos.dbf")), Is.True);
            Assert.That(File.Exists(Path.Combine(_dir, "vistax_surcos.prj")), Is.True);

            // Rumbo 90° (este): la derecha es el sur.
            VxRegistroArchivo.PosicionSurco(-33, -61, 90, 1.0, out double lat, out double lon);
            Assert.That(lat, Is.EqualTo(-33 - 1 / 111320.0).Within(1e-10));
            Assert.That(lon, Is.EqualTo(-61).Within(1e-10));
            // Rumbo 0° (norte): la derecha es el este.
            VxRegistroArchivo.PosicionSurco(0, 0, 0, 1.0, out lat, out lon);
            Assert.That(lon, Is.EqualTo(1 / 111320.0).Within(1e-10));
        }

        [Test]
        public void EsParte_reconoce_solo_el_patron()
        {
            Assert.That(VxRegistroArchivo.EsParte("vistax_surcos_0012.ndjson", out int i), Is.True);
            Assert.That(i, Is.EqualTo(12));
            Assert.That(VxRegistroArchivo.EsParte("vistax_20260101.ndjson", out _), Is.False);
            Assert.That(VxRegistroArchivo.EsParte("resumen.json", out _), Is.False);
        }

        [Test]
        public void Tamano_por_tramo_de_30_surcos_es_chico()
        {
            var t = Tramo(0);
            t.Surcos.Clear();
            for (int b = 1; b <= 30; b++)
                t.Surcos.Add(new VxTramoSurco { Tren = 1, Bajada = b, OffM = (b - 15.5) * 0.52, SemM = 3.84, Esp = Ix(46, 1.3, 2.6, 17.9) });
            int bytes = System.Text.Encoding.UTF8.GetByteCount(VxRegistroArchivo.SerializarTramo(t));
            Assert.That(bytes, Is.LessThan(1600), "≈ 1,4 KB por tramo de 10 m con 30 surcos");
        }
    }
}
