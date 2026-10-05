// ============================================================================
// VistaXRegistroLoteTests — el registro VistaX por lote de punta a punta con
// fakes: lote abierto + sembrando → tramos en <lote>/VistaX/Surcos/, índices
// ISO por surco, resumen y SHP al cerrar el lote. Sin lote no graba.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.VistaX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class VistaXRegistroLoteTests
    {
        private sealed class EstadoFake : IAogStateProvider
        {
            public AogStateSnapshot Snap = new AogStateSnapshot();
            public AogStateSnapshot GetSnapshot() => Snap;
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

        private sealed class LiveFake : IVistaXLiveService, IVistaXTramosEspaciamiento
        {
            public VistaXLiveSnapshotDto Snap = new VistaXLiveSnapshotDto();
            public int Tomados;
            public List<VxIndicesSurco> ProximoTramo = new List<VxIndicesSurco>();
            public void Start() { }
            public void Stop() { }
            public VistaXLiveSnapshotDto GetSnapshot() => Snap;
            public bool IsRunning => true;
            public void Reload() { }
            public void ForzarMonitoreoManual(bool activo) { }
            public double AjustarReferenciaDensidad(string accion) => 0;
            public void PruebaIniciar(double distanciaM) { }
            public void PruebaCancelar() { }
            public void PruebaReset() { }
            public VistaXPruebaDto PruebaEstado() => new VistaXPruebaDto();
            public List<VxIndicesSurco> TomarTramoEspaciamiento()
            {
                Tomados++;
                var r = ProximoTramo;
                ProximoTramo = new List<VxIndicesSurco>();
                return r;
            }
        }

        private string _fields;
        private EstadoFake _st;
        private LiveFake _live;
        private DateTime _ahora;
        private VistaXRegistroLote _reg;

        [SetUp]
        public void SetUp()
        {
            _fields = Path.Combine(Path.GetTempPath(), "vx_fields_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_fields, "LoteA"));
            Directory.CreateDirectory(Path.Combine(_fields, "LoteB"));
            _st = new EstadoFake();
            _st.Snap.FieldsDirectory = _fields;
            _st.Snap.CurrentFieldDirectory = "LoteA";
            _st.Snap.Latitude = -33;
            _st.Snap.Longitude = -61;
            _st.Snap.ToolEasting = 50;
            _st.Snap.ToolNorthing = 0;
            _live = new LiveFake();
            _live.Snap.MonitoreoActivo = true;
            _live.Snap.Velocidad = 7.2;
            _live.Snap.DistanciaEntreSurcos = 0.52;
            var tren = new VistaXTrenLiveDto { Tren = 1 };
            for (int b = 1; b <= 3; b++)
                tren.Surcos.Add(new VistaXSurcoStateDto { Tren = 1, Bajada = b, Tipo = "semilla", SemM = 4, Estado = "ok" });
            tren.Surcos.Add(new VistaXSurcoStateDto { Tren = 1, Bajada = 4, Tipo = "fertilizante", SemM = 99, Estado = "ok" });
            _live.Snap.Trenes.Add(tren);
            _ahora = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
            _reg = new VistaXRegistroLote(_st, _live, _live, () => _ahora);
        }

        [TearDown]
        public void TearDown()
        {
            _reg.Dispose();
            try { Directory.Delete(_fields, true); } catch { }
        }

        // Avanza 1 s a 2 m/s.
        private void Paso()
        {
            _reg.Tick();
            _ahora = _ahora.AddSeconds(1);
            _st.Snap.ToolNorthing += 2;
        }

        private string DirA => Path.Combine(_fields, "LoteA", "VistaX", "Surcos");

        [Test]
        public void Sembrando_con_lote_graba_tramos_de_10_m_por_surco()
        {
            _live.ProximoTramo.Add(new VxIndicesSurco { Tren = 1, Bajada = 2, Indices = new VxIndicesEspaciamiento
                { NEspacios = 40, DoblesPct = 2.5, FallasPct = 5, SingulacionPct = 92.5, CvPct = 19 } });
            for (int i = 0; i < 6; i++) Paso();

            var tramos = VxRegistroArchivo.LeerTramos(DirA).ToList();
            Assert.That(tramos.Count, Is.EqualTo(1));
            var t = tramos[0];
            Assert.That(t.DistM, Is.EqualTo(10).Within(1e-9));
            Assert.That(t.Surcos.Select(s => s.Bajada), Is.EqualTo(new[] { 1, 2, 3 }), "el fertilizante no va");
            Assert.That(t.Surcos[0].SemM, Is.EqualTo(4).Within(1e-9));
            Assert.That(t.Surcos[1].Esp.SingulacionPct, Is.EqualTo(92.5).Within(1e-9));
            Assert.That(t.Surcos[0].Esp, Is.Null);
            Assert.That(t.Surcos[0].OffM, Is.EqualTo(-0.52).Within(1e-9));
        }

        [Test]
        public void Sin_lote_no_graba_nada()
        {
            _st.Snap.CurrentFieldDirectory = "";
            for (int i = 0; i < 20; i++) Paso();
            Assert.That(Directory.Exists(Path.Combine(_fields, "LoteA", "VistaX")), Is.False);
            Assert.That(_reg.CarpetaActual, Is.Null);
        }

        [Test]
        public void Sin_sembrar_no_graba_y_vacia_el_espaciamiento_suelto()
        {
            _live.Snap.MonitoreoActivo = false;
            for (int i = 0; i < 20; i++) Paso();
            Assert.That(Directory.Exists(DirA), Is.False);
            Assert.That(_live.Tomados, Is.GreaterThan(0));
        }

        [Test]
        public void Cambiar_de_lote_cierra_el_anterior_con_resumen_y_shp()
        {
            for (int i = 0; i < 14; i++) Paso();   // 1 tramo + 6 m en curso
            _st.Snap.CurrentFieldDirectory = "LoteB";
            Paso();

            Assert.That(VxRegistroArchivo.LeerTramos(DirA).Count(), Is.EqualTo(3), "dos tramos llenos + el pedazo final");
            Assert.That(File.Exists(Path.Combine(DirA, "resumen.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(DirA, "vistax_surcos.shp")), Is.True);
            var res = VxResumenLote.FromJson(File.ReadAllText(Path.Combine(DirA, "resumen.json")));
            Assert.That(res.Lote, Is.EqualTo("LoteA"));
            Assert.That(res.Surcos.Count, Is.EqualTo(3));
            Assert.That(_reg.CarpetaActual, Does.Contain("LoteB"));
        }

        [Test]
        public void Stop_cierra_el_lote_abierto()
        {
            for (int i = 0; i < 8; i++) Paso();
            _reg.Stop();
            Assert.That(File.Exists(Path.Combine(DirA, "resumen.json")), Is.True);
        }

        [Test]
        public void ArmarMuestra_toma_la_herramienta_y_descarta_secciones_cortadas()
        {
            _live.Snap.Trenes[0].Surcos[0].SeccionCortada = true;
            _st.Snap.ToolHeading = 1.0;
            var m = VistaXRegistroLote.ArmarMuestra(_st.Snap, _live.Snap, _ahora);
            Assert.That(m.Surcos.Count, Is.EqualTo(3));
            Assert.That(m.Surcos[0].Valida, Is.False);
            Assert.That(m.Surcos[1].Valida, Is.True);
            Assert.That(m.RumboRad, Is.EqualTo(1.0));
            Assert.That(m.E, Is.EqualTo(50));
        }
    }
}
