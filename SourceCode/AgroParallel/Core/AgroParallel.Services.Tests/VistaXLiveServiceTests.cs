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

        // ── Espaciamiento (firmware v3.1, campo "dt") ─────────────────────

        // Solo la velocidad: el resto del provider no lo usa VistaXLive.
        private sealed class FakeState : IAogStateProvider
        {
            public double Vel = 8;
            public AogStateSnapshot GetSnapshot() => new AogStateSnapshot { AvgSpeed = Vel };
            public AllSettingsSnapshot GetAllSettings() => null;
            public EventLogSnapshot GetEventLog() => null;
            public XteGraphSample GetXteGraphSample() => null;
            public HeadingGraphSample GetHeadingGraphSample() => null;
            public SteerGraphSample GetSteerGraphSample() => null;
            public CorrectionGraphSample GetCorrectionGraphSample() => null;
            public ShiftPosSnapshot GetShiftPos() => null;
            public SimCoordsSnapshot GetSimCoords() => null;
            public SectionColorsSnapshot GetSectionColors() => null;
            public DisplayColorsSnapshot GetDisplayColors() => null;
            public double GetShapeFieldDose(string fieldName) => 0;
            public ShapeSnapshot GetShape() => null;
            public ShapeFieldsSnapshot GetShapeFields() => null;
        }

        private static VistaXLiveService ServicioSembrando(FakeNodoRegistry fake, FakeVistaXConfig cfg)
        {
            cfg.Cfg.MetodoInicio = "manual";
            cfg.Imp.Setup.DensidadObjetivo = 5;            // maíz: Xref = 20 cm
            cfg.Imp.MapeoSensores.Add(new VistaXSensorConfigDto
            {
                Uid = "VX-1", Cable = 1, Bajada = 1, SurcoDesde = 1, Tipo = "semilla", Tren = 1,
            });
            var svc = new VistaXLiveService(fake, cfg, state: new FakeState());
            svc.Start();
            svc.ForzarMonitoreoManual(true);
            return svc;
        }

        private static VistaXSurcoStateDto Surco1(VistaXLiveService svc)
        {
            foreach (var t in svc.GetSnapshot().Trenes)
                foreach (var s in t.Surcos)
                    if (s.Uid == "VX-1" && s.Cable == 1) return s;
            return null;
        }

        private static string Tel(string dts) =>
            "{\"schema\":\"agp.vistax.telemetry/2\",\"uid\":\"VX-1\",\"sensores\":[" +
            "{\"cable\":1,\"valor\":11.1,\"raw\":3,\"acum\":100,\"dt\":[" + dts + "]}]}";

        [Test]
        public void Telemetria_v2_calcula_singulacion_y_respeta_el_asentamiento()
        {
            var fake = new FakeNodoRegistry();
            using (var svc = ServicioSembrando(fake, new FakeVistaXConfig()))
            {
                // 8 km/h y 20 cm → 90 ms = 900 unidades de 0,1 ms.
                var limpio = string.Join(",", System.Linq.Enumerable.Repeat("900", 15));

                fake.Emit("vistax/VX-1/telemetria", Tel(limpio));
                var s0 = Surco1(svc);                       // arranca la siembra acá
                Assert.That(s0.NEspacios, Is.EqualTo(0), "los primeros 2 s no cuentan");

                System.Threading.Thread.Sleep(2200);
                for (int i = 0; i < 10; i++)
                    fake.Emit("vistax/VX-1/telemetria", Tel(limpio));
                // Un hueco (parada) y un doble.
                fake.Emit("vistax/VX-1/telemetria", Tel("65535,300"));

                var s = Surco1(svc);
                Assert.That(s.NEspacios, Is.EqualTo(151));
                Assert.That(s.DoblesPct, Is.EqualTo(0.7).Within(0.05));
                Assert.That(s.FallasPct, Is.EqualTo(0));
                Assert.That(s.Singulacion, Is.EqualTo(99.3).Within(0.05));
                Assert.That(s.EspaciamientoLote.NEspacios, Is.EqualTo(151));
                Assert.That(s.SingulacionBaja, Is.False);
                Assert.That(s.Valor, Is.EqualTo(11.1), "los campos viejos siguen iguales");
            }
        }

        [Test]
        public void Telemetria_vieja_sin_dt_deja_los_indices_en_cero()
        {
            var fake = new FakeNodoRegistry();
            using (var svc = ServicioSembrando(fake, new FakeVistaXConfig()))
            {
                fake.Emit("vistax/VX-1/telemetria",
                    "{\"uid\":\"VX-1\",\"sensores\":[{\"cable\":1,\"valor\":11.1,\"raw\":3,\"acum\":100}]}");
                var s = Surco1(svc);
                Assert.That(s.NEspacios, Is.EqualTo(0));
                Assert.That(s.Singulacion, Is.EqualTo(0));
                Assert.That(s.EspaciamientoLote, Is.Null);
            }
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
