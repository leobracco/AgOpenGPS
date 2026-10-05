// ============================================================================
// MqttLiveServiceBaseTests — pinnea el ritual común de los live services MQTT:
// filtrado por prefijo de topic, parse JSON defensivo, extracción de uid y
// subtopic, limpieza en Stop() y helpers ReadDouble/ReadBool.
//
// Usa un fake en memoria de INodoRegistryService (sin broker real) y una
// subclase mínima TestLiveService que acumula lo que le llega a OnPayload.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.Common;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    // ── Fake en memoria del registry MQTT ───────────────────────────────────
    internal sealed class FakeNodoRegistry : INodoRegistryService
    {
        /// <summary>Filtros registrados vía SubscribeAsync (para asserts).</summary>
        public List<string> SubscribedFilters { get; } = new List<string>();

        public void Start(string brokerAddress, int brokerPort) { }
        public void Stop() { }
        public IReadOnlyList<NodoStatus> GetAll() => new List<NodoStatus>();

#pragma warning disable CS0067 // eventos no usados por el fake
        public event EventHandler Changed;
#pragma warning restore CS0067
        public event EventHandler<MqttMessageReceivedEventArgs> MessageReceived;

        public Task<bool> PublishAsync(string topic, string payload, bool retain)
            => Task.FromResult(true);

        public Task<CmdAckResult> PublishCmdAsync(
            string topic, string uid, IDictionary<string, object> payload,
            int ttlMs = 5000, string source = "pilotx")
            => Task.FromResult(new CmdAckResult { Ok = true, Status = "ok" });

        public Task<bool> SubscribeAsync(string topicFilter)
        {
            SubscribedFilters.Add(topicFilter);
            return Task.FromResult(true);
        }

        public NodoDiagInfo GetDiagnostic() => null;
        public Task<bool> SetWildcardCaptureAsync(bool on) => Task.FromResult(true);
        public Task<bool> ReconnectAsync() => Task.FromResult(true);
        public AgpConfigSyncTracker ConfigSync => null;

        /// <summary>Simula la llegada de un mensaje MQTT crudo.</summary>
        public void Emit(string topic, string payload)
            => MessageReceived?.Invoke(this, new MqttMessageReceivedEventArgs(topic, payload));
    }

    // ── Subclase mínima bajo test ────────────────────────────────────────────
    internal sealed class TestLiveService : MqttLiveServiceBase<object>
    {
        public List<(string Uid, string Subtopic)> Recibidos { get; } = new List<(string, string)>();

        protected override string TopicPrefix => "agp/flow/";
        protected override string[] Subscriptions => new[] { "agp/flow/+/status_live" };
        protected override int MinParts => 4;

        public TestLiveService(INodoRegistryService nodos) : base(nodos) { }

        protected override void OnPayload(string uid, string subtopic, string[] topicParts, JsonElement root)
        {
            Recibidos.Add((uid, subtopic));
            lock (_lock) _readings[uid] = new object();
        }

        public int ReadingsCount
        {
            get { lock (_lock) return _readings.Count; }
        }

        // Wrappers públicos para testear los helpers protected static de la base
        public static double CallReadDouble(JsonElement root, params string[] keys) => ReadDouble(root, keys);
        public static bool CallReadBool(JsonElement root, params string[] keys) => ReadBool(root, keys);
    }

    [TestFixture]
    public class MqttLiveServiceBaseTests
    {
        private FakeNodoRegistry _fake;
        private TestLiveService _svc;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeNodoRegistry();
            _svc = new TestLiveService(_fake);
        }

        [TearDown]
        public void TearDown() => _svc.Stop();

        private static JsonElement Json(string s)
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.Clone();
        }

        [Test]
        public void TopicAjeno_SeIgnora()
        {
            _svc.Start();
            _fake.Emit("agp/otro/X1/status_live", "{}");
            Assert.That(_svc.Recibidos, Is.Empty);
        }

        [Test]
        public void PayloadInvalido_NoExplota()
        {
            _svc.Start();
            // La base parsea con try/catch: JSON basura no debe propagar excepción
            Assert.DoesNotThrow(() => _fake.Emit("agp/flow/X1/status_live", "no-json"));
            Assert.That(_svc.Recibidos, Is.Empty);
        }

        [Test]
        public void UidYSubtopic_SeExtraenBien()
        {
            _svc.Start();
            _fake.Emit("agp/flow/AB12/status_live", "{}");
            Assert.That(_svc.Recibidos, Has.Count.EqualTo(1));
            Assert.That(_svc.Recibidos[0].Uid, Is.EqualTo("AB12"));
            Assert.That(_svc.Recibidos[0].Subtopic, Is.EqualTo("status_live"));
        }

        [Test]
        public void Stop_LimpiaReadings()
        {
            _svc.Start();
            _fake.Emit("agp/flow/AB12/status_live", "{}");
            Assert.That(_svc.ReadingsCount, Is.EqualTo(1));

            _svc.Stop();
            Assert.That(_svc.ReadingsCount, Is.Zero);

            // Después de Stop el handler quedó desregistrado: no procesa más
            _fake.Emit("agp/flow/AB12/status_live", "{}");
            Assert.That(_svc.Recibidos, Has.Count.EqualTo(1));
        }

        [Test]
        public void Start_SuscribeFiltros()
        {
            _svc.Start();
            Assert.That(_fake.SubscribedFilters, Does.Contain("agp/flow/+/status_live"));
        }

        [Test]
        public void ReadDouble_MultiKey_Fallback()
        {
            var root = Json("{\"b\":2.5}");
            // "a" no existe → cae al segundo key "b"
            Assert.That(TestLiveService.CallReadDouble(root, "a", "b"), Is.EqualTo(2.5));
            // Ningún key presente → 0
            Assert.That(TestLiveService.CallReadDouble(root, "x", "y"), Is.Zero);
        }

        [Test]
        public void ReadBool_AceptaStringYNumero()
        {
            Assert.That(TestLiveService.CallReadBool(Json("{\"x\":1}"), "x"), Is.True);
            Assert.That(TestLiveService.CallReadBool(Json("{\"x\":\"ok\"}"), "x"), Is.True);
            // OJO: para strings el código devuelve el resultado de comparar contra
            // "true"/"ok"/"1" — cualquier otro string (incluido "false") da false.
            // Pinneamos ese comportamiento real.
            Assert.That(TestLiveService.CallReadBool(Json("{\"x\":\"false\"}"), "x"), Is.False);
            Assert.That(TestLiveService.CallReadBool(Json("{\"x\":0}"), "x"), Is.False);
        }
    }
}
