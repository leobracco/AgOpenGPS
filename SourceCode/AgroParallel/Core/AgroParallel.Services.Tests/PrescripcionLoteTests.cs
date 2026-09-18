// ============================================================================
// PrescripcionLoteTests.cs — la prescripción activa queda ATADA al lote en
// que se activó (reporte 2026-08-10: "creé un lote nuevo y levantó el shape
// de Las de atras" — y peor, QuantiX dosificaba con ese mapa en el lote
// nuevo). Regla: GetDoseAt devuelve dosis SOLO si el lote abierto coincide
// con el lote de activación; sin lote asociado (estado legacy o activada sin
// lote abierto) no aplica en ninguno.
//
// El service usa estado ESTÁTICO compartido (_active) — los tests van
// [NonParallelizable] y cada uno deja el estado limpio en TearDown.
// ============================================================================

using System;
using System.IO;
using AgroParallel.Services;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [NonParallelizable]
    public class PrescripcionLoteTests
    {
        private string _configRootPrevio;
        private string _dirTemp;
        private string _loteAbierto;

        // GeoJSON mínimo: un cuadrado de 1°x1° centrado en (0.5, 0.5) con DOSIS=42.
        private const string GeoJson = @"{
  ""type"": ""FeatureCollection"",
  ""features"": [
    {
      ""type"": ""Feature"",
      ""properties"": { ""DOSIS"": 42 },
      ""geometry"": {
        ""type"": ""Polygon"",
        ""coordinates"": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]
      }
    }
  ]
}";

        [SetUp]
        public void SetUp()
        {
            _configRootPrevio = AgroParallel.Common.AgpPaths.ConfigRoot;
            _dirTemp = Path.Combine(Path.GetTempPath(), "agp-presc-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dirTemp, "data", "prescripciones"));
            File.WriteAllText(
                Path.Combine(_dirTemp, "data", "prescripciones", "Las de atras.geojson"), GeoJson);
            AgroParallel.Common.AgpPaths.ConfigRoot = _dirTemp;

            _loteAbierto = "";
            PrescripcionService.LoteActualProvider = () => _loteAbierto;
        }

        [TearDown]
        public void TearDown()
        {
            var svc = new PrescripcionService();
            svc.ClearActive();
            PrescripcionService.LoteActualProvider = null;
            AgroParallel.Common.AgpPaths.ConfigRoot = _configRootPrevio;
            try { Directory.Delete(_dirTemp, recursive: true); } catch { }
        }

        [Test]
        public void ActivadaConLoteAbierto_DosificaEnEseLote()
        {
            _loteAbierto = "Las de atras";
            var svc = new PrescripcionService();
            Assert.That(svc.SetActive("las-de-atras", "DOSIS"), Is.True);

            Assert.That(svc.GetActive().Lote, Is.EqualTo("Las de atras"));
            Assert.That(svc.GetDoseAt(0.5, 0.5), Is.EqualTo(42));
        }

        [Test]
        public void EnOtroLote_NoDosifica()
        {
            _loteAbierto = "Las de atras";
            var svc = new PrescripcionService();
            svc.SetActive("las-de-atras", "DOSIS");

            _loteAbierto = "test 123";   // lote nuevo recién creado
            Assert.That(svc.GetDoseAt(0.5, 0.5), Is.EqualTo(0));
        }

        [Test]
        public void SinLoteAbierto_NoDosifica()
        {
            _loteAbierto = "Las de atras";
            var svc = new PrescripcionService();
            svc.SetActive("las-de-atras", "DOSIS");

            _loteAbierto = "";           // lote cerrado
            Assert.That(svc.GetDoseAt(0.5, 0.5), Is.EqualTo(0));
        }

        [Test]
        public void ActivadaSinLoteAbierto_QuedaSinLoteYNoAplicaEnNinguno()
        {
            _loteAbierto = "";
            var svc = new PrescripcionService();
            svc.SetActive("las-de-atras", "DOSIS");

            Assert.That(svc.GetActive().Lote, Is.Empty);

            _loteAbierto = "Las de atras";
            Assert.That(svc.GetDoseAt(0.5, 0.5), Is.EqualTo(0));
        }

        [Test]
        public void LoteCoincideSinImportarMayusculas()
        {
            _loteAbierto = "Las de atras";
            var svc = new PrescripcionService();
            svc.SetActive("las-de-atras", "DOSIS");

            _loteAbierto = "LAS DE ATRAS";
            Assert.That(svc.GetDoseAt(0.5, 0.5), Is.EqualTo(42));
        }

        [Test]
        public void ReActivarConOtroLoteAbierto_ReAta()
        {
            _loteAbierto = "Las de atras";
            var svc = new PrescripcionService();
            svc.SetActive("las-de-atras", "DOSIS");

            _loteAbierto = "Lote nuevo";
            svc.SetActive("las-de-atras", "DOSIS");   // el operario la re-activa acá

            Assert.That(svc.GetActive().Lote, Is.EqualTo("Lote nuevo"));
            Assert.That(svc.GetDoseAt(0.5, 0.5), Is.EqualTo(42));
        }
    }
}
