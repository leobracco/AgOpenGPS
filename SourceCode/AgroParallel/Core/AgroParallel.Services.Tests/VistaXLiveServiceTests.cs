// ============================================================================
// VistaXLiveServiceTests — pinnea el guard de subtopic agregado en OnPayload.
//
// Reusa FakeNodoRegistry de MqttLiveServiceBaseTests (mismo assembly) para
// emitir mensajes MQTT sin broker real. Las dependencias opcionales del
// constructor (insumos, state, sections, implemento central, quantixCfg) se
// pasan en null: OnStart() las declara opcionales y solo loguea su ausencia.
// ============================================================================

using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    // Config en memoria: los tests no tocan el disco.
    internal sealed class FakeVistaXConfig : IVistaXConfigService
    {
        public VistaXConfigDto Cfg = new VistaXConfigDto();
        public VistaXImplementoDto Imp = new VistaXImplementoDto();

        public VistaXConfigDto GetConfig() => Cfg;
        public void SaveConfig(VistaXConfigDto dto) => Cfg = dto;
        public VistaXImplementoDto GetImplemento() => Imp;
        public void SaveImplemento(VistaXImplementoDto dto) => Imp = dto;
        public string GetImplementoPath() => null;
    }

    [TestFixture]
    public class VistaXLiveServiceTests
    {
        private FakeNodoRegistry _fake;
        private FakeVistaXConfig _cfg;
        private VistaXLiveService _svc;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeNodoRegistry();
            _cfg = new FakeVistaXConfig();
            _svc = new VistaXLiveService(_fake, _cfg);
            _svc.Start();
        }

        // Dispose(), no Stop(): el service implementa IDisposable y el analyzer
        // NUnit1032 exige que un campo IDisposable se libere en el TearDown.
        [TearDown]
        public void TearDown() => _svc.Dispose();

        // REGRESIÓN: sin el guard de subtopic, cualquier mensaje que matchee
        // prefijo "vistax/" y tenga ≥3 partes (p.ej. el heartbeat que publica
        // el firmware por vistax/nodos/heartbeat) se parseaba como telemetría
        // y pisaba la lectura con ceros + un LastTs fresco. Acá no hace falta
        // que la telemetría esté mapeada a un tren: al no matchear
        // MapeoSensores (vacío en este test) aparece igual en el tren
        // diagnóstico "(sin mapear)" (Tren=99), que alcanza para verificar
        // que el valor sobrevive al heartbeat.
        [Test]
        public void Heartbeat_no_pisa_la_lectura_de_telemetria()
        {
            _fake.Emit("vistax/nodos/telemetria",
                "{\"uid\":\"nodo1\",\"sensores\":[{\"cable\":1,\"valor\":5.0}]}");

            var trenAntes = FindTren99(_svc);
            Assert.That(trenAntes, Is.Not.Null, "la telemetria valida deberia crear el tren diagnostico");
            Assert.That(trenAntes.Surcos[0].Valor, Is.EqualTo(5.0));

            _fake.Emit("vistax/nodos/heartbeat", "{\"uid\":\"nodo1\"}");

            var trenDespues = FindTren99(_svc);
            Assert.That(trenDespues, Is.Not.Null);
            Assert.That(trenDespues.Surcos[0].Valor, Is.EqualTo(5.0), "el heartbeat piso el valor de telemetria");
        }

        // Mismo bug, versión "no crea de la nada": un heartbeat de un UID que
        // todavía no mando telemetria no debe generar ninguna lectura.
        [Test]
        public void Heartbeat_de_un_nodo_nuevo_no_crea_lectura()
        {
            _fake.Emit("vistax/nodos/heartbeat", "{\"uid\":\"nodoNuevo\"}");

            Assert.That(FindTren99(_svc), Is.Null, "el heartbeat no deberia generar ninguna lectura");
        }

        private static VistaXTrenLiveDto FindTren99(VistaXLiveService svc)
        {
            var snap = svc.GetSnapshot();
            foreach (var t in snap.Trenes)
                if (t.Tren == 99) return t;
            return null;
        }
    }
}
