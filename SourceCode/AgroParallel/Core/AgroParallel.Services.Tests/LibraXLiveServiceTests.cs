// ============================================================================
// LibraXLiveServiceTests — pinnea el parser de agp/librax/+/status_live.
//
// Reusa FakeNodoRegistry de MqttLiveServiceBaseTests (mismo assembly) para
// emitir mensajes MQTT sin broker real.
// ============================================================================

using System.Threading;
using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    // Config en memoria: los tests no tocan el disco.
    internal sealed class FakeLibraXConfig : ILibraXConfigService
    {
        public LibraXConfigDto Cfg = new LibraXConfigDto();
        public LibraXConfigDto Load() => Cfg;
        public void Save(LibraXConfigDto dto) { Cfg = dto; }
    }

    [TestFixture]
    public class LibraXLiveServiceTests
    {
        private FakeNodoRegistry _fake;
        private FakeLibraXConfig _cfg;
        private LibraXLiveService _svc;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeNodoRegistry();
            _cfg = new FakeLibraXConfig();
            _svc = new LibraXLiveService(_fake, _cfg);
            _svc.Start();
        }

        [TearDown]
        public void TearDown() => _svc.Stop();

        private const string PayloadOk =
            "{\"ratio\":412,\"paddle_hz\":7,\"rpm\":420,\"moist_mv\":1832," +
            "\"sensor_ok\":true,\"noise\":3,\"up\":12345}";

        [Test]
        public void Start_suscribe_el_filtro_de_status_live()
        {
            Assert.That(_fake.SubscribedFilters, Does.Contain("agp/librax/+/status_live"));
        }

        [Test]
        public void Payload_valido_se_refleja_en_el_snapshot()
        {
            _fake.Emit("agp/librax/a1b2c3/status_live", PayloadOk);

            var snap = _svc.GetSnapshot();
            Assert.That(snap.Nodos.Count, Is.EqualTo(1));

            var n = snap.Nodos[0];
            Assert.That(n.Uid, Is.EqualTo("a1b2c3"));
            Assert.That(n.Online, Is.True);
            Assert.That(n.RatioPermil, Is.EqualTo(412));
            Assert.That(n.RatioPct, Is.EqualTo(41.2).Within(0.01));
            Assert.That(n.PaddleHz, Is.EqualTo(7));
            Assert.That(n.Rpm, Is.EqualTo(420));
            Assert.That(n.MoistMv, Is.EqualTo(1832));
            Assert.That(n.SensorOk, Is.True);
            Assert.That(n.Noise, Is.EqualTo(3));
            Assert.That(n.UptimeS, Is.EqualTo(12345));
        }

        [Test]
        public void Uid_sale_del_topic_cuando_el_payload_no_lo_trae()
        {
            _fake.Emit("agp/librax/deadbe/status_live", PayloadOk);
            Assert.That(_svc.GetSnapshot().Nodos[0].Uid, Is.EqualTo("deadbe"));
        }

        [Test]
        public void Campos_faltantes_no_rompen_el_parse()
        {
            _fake.Emit("agp/librax/a1b2c3/status_live", "{\"ratio\":500}");

            var n = _svc.GetSnapshot().Nodos[0];
            Assert.That(n.RatioPermil, Is.EqualTo(500));
            Assert.That(n.Rpm, Is.EqualTo(0));
            Assert.That(n.SensorOk, Is.False);
        }

        [Test]
        public void Json_malformado_se_descarta_sin_tirar_el_service()
        {
            _fake.Emit("agp/librax/a1b2c3/status_live", "{esto no es json");

            Assert.That(_svc.IsRunning, Is.True);
            Assert.That(_svc.GetSnapshot().Nodos.Count, Is.EqualTo(0));
        }

        [Test]
        public void Topics_de_otro_producto_se_ignoran()
        {
            _fake.Emit("agp/storm/a1b2c3/status_live", PayloadOk);
            Assert.That(_svc.GetSnapshot().Nodos.Count, Is.EqualTo(0));
        }

        [Test]
        public void Nodo_sin_publicar_pasa_a_offline_por_timeout()
        {
            _cfg.Cfg.TimeoutMs = 50;
            _svc.Reload();

            _fake.Emit("agp/librax/a1b2c3/status_live", PayloadOk);
            Assert.That(_svc.GetSnapshot().Nodos[0].Online, Is.True);

            Thread.Sleep(120);
            Assert.That(_svc.GetSnapshot().Nodos[0].Online, Is.False);
        }

        [Test]
        public void Un_nodo_configurado_aparece_aunque_nunca_haya_publicado()
        {
            _cfg.Cfg.Nodos.Add(new LibraXNodoConfigDto { Uid = "noria1", Nombre = "Noria principal" });
            _svc.Reload();

            var snap = _svc.GetSnapshot();
            Assert.That(snap.Nodos.Count, Is.EqualTo(1));
            Assert.That(snap.Nodos[0].Nombre, Is.EqualTo("Noria principal"));
            Assert.That(snap.Nodos[0].Online, Is.False);
        }

        [Test]
        public void Stop_limpia_las_lecturas()
        {
            _fake.Emit("agp/librax/a1b2c3/status_live", PayloadOk);
            _svc.Stop();
            Assert.That(_svc.GetSnapshot().Nodos.Count, Is.EqualTo(0));
            Assert.That(_svc.IsRunning, Is.False);
        }
    }
}
