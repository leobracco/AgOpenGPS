// ============================================================================
// StormXLiveServiceTests — pinnea el guard de subtopic de agp/storm/+/status_live.
//
// Reusa FakeNodoRegistry de MqttLiveServiceBaseTests (mismo assembly) para
// emitir mensajes MQTT sin broker real.
// ============================================================================

using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    // Config en memoria: los tests no tocan el disco.
    internal sealed class FakeStormXConfig : IStormXConfigService
    {
        public StormXConfigDto Cfg = new StormXConfigDto();
        public StormXConfigDto Load() => Cfg;
        public void Save(StormXConfigDto dto) { Cfg = dto; }
    }

    [TestFixture]
    public class StormXLiveServiceTests
    {
        private FakeNodoRegistry _fake;
        private FakeStormXConfig _cfg;
        private StormXLiveService _svc;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeNodoRegistry();
            _cfg = new FakeStormXConfig();
            _svc = new StormXLiveService(_fake, _cfg);
            _svc.Start();
        }

        // Dispose(), no Stop(): el service implementa IDisposable y el analyzer
        // NUnit1032 exige que un campo IDisposable se libere en el TearDown.
        // Dispose() llama a Stop() internamente, así que hace lo mismo y además
        // deja contento al analyzer sin suprimirlo para todo el repo.
        [TearDown]
        public void TearDown() => _svc.Dispose();

        // wind_ms = 6.0 supera WindMaxMs (default 5.5): con datos reales el
        // veredicto correcto es "warn" (demasiado viento para pulverizar), no
        // "ok". Elegido a propósito así el test de regresión puede distinguir
        // un "ok" legítimo de un "ok" falso por ceros.
        private const string PayloadOk =
            "{\"wind_ms\":6.0,\"gust_ms\":6.5,\"wind_dir\":200,\"temp_c\":25.0," +
            "\"hum_pct\":55.0,\"press_hpa\":1005.0,\"delta_t_c\":5.0,\"rain_mm\":0.0}";

        [Test]
        public void Payload_valido_se_refleja_en_el_snapshot()
        {
            _fake.Emit("agp/storm/a1b2c3/status_live", PayloadOk);

            var snap = _svc.GetSnapshot();
            Assert.That(snap.Nodos.Count, Is.EqualTo(1));

            var n = snap.Nodos[0];
            Assert.That(n.Uid, Is.EqualTo("a1b2c3"));
            Assert.That(n.Online, Is.True);
            Assert.That(n.WindMs, Is.EqualTo(6.0));
            Assert.That(n.GustMs, Is.EqualTo(6.5));
            Assert.That(n.WindDir, Is.EqualTo(200));
            Assert.That(n.TempC, Is.EqualTo(25.0));
            Assert.That(n.HumPct, Is.EqualTo(55.0));
            Assert.That(n.PressHpa, Is.EqualTo(1005.0));
            Assert.That(n.DeltaTC, Is.EqualTo(5.0));
            Assert.That(n.RainMm, Is.EqualTo(0.0));
            Assert.That(n.Verdict, Is.EqualTo("warn"), "wind_ms 6.0 > wind_max_ms 5.5 default -> warn");
        }

        // REGRESIÓN: el announcement del firmware llega al mismo prefijo de
        // topic, con 4 partes y con "uid" en el payload (MQTT_Custom.cpp:109-119,
        // publica agp/storm/<uid>/announcement RETAINED), así que pasa todos
        // los filtros de la clase base. Sin el guard de subtopic, OnPayload
        // escribía una lectura en ceros sobre la real, y con todo en cero
        // ComputeVerdict devuelve "ok" (los chequeos de mínimo están guardados
        // con "> 0"): la pantalla pasaba de "demasiado viento" a "PULVERIZAR OK"
        // con datos inexistentes. El assert que importa es que el verdict NO
        // se vuelva "ok".
        [Test]
        public void Announcement_no_pisa_la_lectura_de_status_live()
        {
            _fake.Emit("agp/storm/a1b2c3/status_live", PayloadOk);

            _fake.Emit("agp/storm/a1b2c3/announcement",
                "{\"uid\":\"a1b2c3\",\"ip\":\"192.168.1.55\",\"version\":\"1.0.0\"," +
                "\"hw\":\"esp32-s3\",\"device\":\"stormx\",\"type\":\"storm\"}");

            var n = _svc.GetSnapshot().Nodos[0];
            Assert.That(n.WindMs, Is.EqualTo(6.0), "el announcement piso wind_ms");
            Assert.That(n.TempC, Is.EqualTo(25.0), "el announcement piso temp_c");
            Assert.That(n.HumPct, Is.EqualTo(55.0), "el announcement piso hum_pct");
            Assert.That(n.Verdict, Is.EqualTo("warn"), "el announcement convirtio el warn en un falso ok por ceros");
        }
    }
}
