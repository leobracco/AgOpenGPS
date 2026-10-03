// ============================================================================
// VistaXSurcosSyncTests — qué partes del registro VistaX por surco suben a
// OrbitX y cuándo: completas una vez, la que crece cada 5 min, todo al cerrar.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgroParallel.Services.OrbitX;
using AgroParallel.Services.VistaX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class VistaXSurcosSyncTests
    {
        private string _lote;
        private VxRegistroArchivo _arch;
        private readonly DateTime _t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        [SetUp]
        public void SetUp()
        {
            _lote = Path.Combine(Path.GetTempPath(), "vx_sync_" + Guid.NewGuid().ToString("N"), "LoteA");
            Directory.CreateDirectory(_lote);
            _arch = new VxRegistroArchivo(VxRegistroArchivo.DirDeLote(_lote), "LoteA", tramosPorParte: 2);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(Path.GetDirectoryName(_lote), true); } catch { }
        }

        private void Tramos(int n)
        {
            for (int i = 0; i < n; i++)
            {
                var t = new VxTramo { InicioUtc = _t0, FinUtc = _t0.AddSeconds(5), Lat = -33, Lon = -61, DistM = 10 };
                t.Surcos.Add(new VxTramoSurco { Tren = 1, Bajada = 1, SemM = 4 });
                _arch.Escribir(t);
            }
        }

        [Test]
        public void Sin_carpeta_no_hay_nada_que_subir()
        {
            var s = new VistaXSurcosSync();
            Assert.That(s.Planificar(_lote, "LoteA", _ => false, _t0), Is.Empty);
        }

        [Test]
        public void Subtipos_ruta_y_completas_una_sola_vez()
        {
            Tramos(5);                              // partes 1 y 2 completas, 3 creciendo
            _arch.GuardarResumen(_t0);
            var s = new VistaXSurcosSync();
            var confirmados = new HashSet<string>();

            var a = s.Planificar(_lote, "LoteA", confirmados.Contains, _t0);
            Assert.That(a.Select(e => Path.GetFileName(e.Path)), Is.EqualTo(new[]
                { "vistax_surcos_0001.ndjson", "vistax_surcos_0002.ndjson", "vistax_surcos_0003.ndjson", "resumen.json" }));
            Assert.That(a[0].Subtipo, Is.EqualTo("vistax_surcos"));
            Assert.That(a[0].RutaRel, Is.EqualTo("vistax/surcos/LoteA/vistax_surcos_0001.ndjson"));
            Assert.That(a[3].Subtipo, Is.EqualTo("vistax_resumen"));
            foreach (var e in a) confirmados.Add(e.Path);

            // 1 min después, sin cambios: nada.
            Assert.That(s.Planificar(_lote, "LoteA", confirmados.Contains, _t0.AddMinutes(1)), Is.Empty);
        }

        [Test]
        public void La_parte_que_crece_espera_5_minutos_salvo_al_cerrar_el_lote()
        {
            Tramos(1);
            var s = new VistaXSurcosSync();
            var confirmados = new HashSet<string>();
            foreach (var e in s.Planificar(_lote, "LoteA", confirmados.Contains, _t0)) confirmados.Add(e.Path);

            Tramos(1);                              // la parte 1 se completa (2/2) pero sigue siendo la última
            Assert.That(s.Planificar(_lote, "LoteA", confirmados.Contains, _t0.AddMinutes(2)), Is.Empty);
            var forzado = s.Planificar(_lote, "LoteA", confirmados.Contains, _t0.AddMinutes(2), forzar: true);
            Assert.That(forzado.Select(e => Path.GetFileName(e.Path)), Is.EqualTo(new[] { "vistax_surcos_0001.ndjson" }));
        }

        [Test]
        public void Una_parte_que_se_subio_parcial_vuelve_a_subir_al_completarse()
        {
            Tramos(1);
            var s = new VistaXSurcosSync();
            var confirmados = new HashSet<string>();
            foreach (var e in s.Planificar(_lote, "LoteA", confirmados.Contains, _t0)) confirmados.Add(e.Path);

            Tramos(2);                              // parte 1 completa con otro largo, parte 2 nueva
            var b = s.Planificar(_lote, "LoteA", confirmados.Contains, _t0.AddMinutes(1));
            Assert.That(b.Select(e => Path.GetFileName(e.Path)), Is.EqualTo(new[] { "vistax_surcos_0001.ndjson", "vistax_surcos_0002.ndjson" }),
                "la 1 cambió de largo; la 2 es nueva (sin subida previa no espera)");
        }

        [Test]
        public void Completa_sin_confirmar_se_reintenta()
        {
            Tramos(3);
            var s = new VistaXSurcosSync();
            s.Planificar(_lote, "LoteA", _ => false, _t0);
            var b = s.Planificar(_lote, "LoteA", _ => false, _t0.AddSeconds(30));
            Assert.That(b.Select(e => Path.GetFileName(e.Path)), Does.Contain("vistax_surcos_0001.ndjson"));
        }
    }
}
