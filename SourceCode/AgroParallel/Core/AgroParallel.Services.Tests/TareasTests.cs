// ============================================================================
// TareasTests.cs — "Tareas de trabajo": la tarea asociada al lote abierto
// (cultivo, tipo de trabajo, notas, inicio/fin, área, insumo/dosis), sus
// transiciones (pausar / reanudar / cerrar), la persistencia en Tareas.json de
// la carpeta del lote y el export (informe HTML + SHP de la cobertura).
//
// Lo que más importa cuidar acá es el ÁREA: el contador del lote
// (WorkedAreaTotalM2) es del LOTE, no de la tarea. La tarea suma solo lo que se
// trabajó mientras estuvo activa, y no puede perder ni inventar hectáreas si
// el operario borra el pintado o PilotX se reinicia a mitad de jornada.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgroParallel.Models;
using AgroParallel.Services.Tareas;
using NetTopologySuite.IO.Esri;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class TareaReglasTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 1, 8, 0, 0);

        private static Tarea Nueva(double areaInicial = 0)
        {
            return TareaReglas.Crear("t1", "Las de atras", "Soja", TipoTrabajo.Siembra,
                "Lote húmedo al fondo", null, T0, areaInicial);
        }

        [Test]
        public void Crear_QuedaActivaConUnTramoAbierto()
        {
            var t = Nueva();

            Assert.That(t.Estado, Is.EqualTo(EstadoTarea.Activa));
            Assert.That(t.Lote, Is.EqualTo("Las de atras"));
            Assert.That(t.Cultivo, Is.EqualTo("Soja"));
            Assert.That(t.TipoTrabajo, Is.EqualTo(TipoTrabajo.Siembra));
            Assert.That(t.Inicio, Is.EqualTo(T0));
            Assert.That(t.Fin, Is.Null);
            Assert.That(t.Tramos, Has.Count.EqualTo(1));
            Assert.That(t.Tramos[0].Fin, Is.Null);
        }

        [Test]
        public void Crear_TipoDesconocido_CaeEnOtro()
        {
            var t = TareaReglas.Crear("t1", "L", "", "rolado", "", null, T0, 0);
            Assert.That(t.TipoTrabajo, Is.EqualTo(TipoTrabajo.Otro));
        }

        [Test]
        public void Crear_ConInsumoActivo_CopiaNombreYDosis()
        {
            var insumo = new InsumoDto { Nombre = "Soja DM 46i17", Tipo = "semilla", Cultivo = "soja",
                                         DosisKgha = 80, DosisUnidad = "kg_ha" };
            var t = TareaReglas.Crear("t1", "L", "", TipoTrabajo.Siembra, "", insumo, T0, 0);

            Assert.That(t.InsumoNombre, Is.EqualTo("Soja DM 46i17"));
            Assert.That(t.Dosis, Is.EqualTo(80));
            Assert.That(t.DosisUnidad, Is.EqualTo("kg_ha"));
            // Sin cultivo tipeado, se toma el del insumo.
            Assert.That(t.Cultivo, Is.EqualTo("soja"));
        }

        [Test]
        public void Crear_InsumoFitosanitario_UsaLaDosisEnLitros()
        {
            var insumo = new InsumoDto { Nombre = "Glifosato", Tipo = "fitosanitario",
                                         DosisKgha = 0, DosisLha = 2.5 };
            var t = TareaReglas.Crear("t1", "L", "Soja", TipoTrabajo.Pulverizacion, "", insumo, T0, 0);

            Assert.That(t.Dosis, Is.EqualTo(2.5));
            Assert.That(t.DosisUnidad, Is.EqualTo("l_ha"));
        }

        [Test]
        public void Area_SoloCuentaLoTrabajadoDesdeQueArranco()
        {
            // El lote ya tenía 10 ha pintadas de antes: no son de esta tarea.
            var t = Nueva(areaInicial: 100000);

            Assert.That(TareaReglas.AreaTrabajadaM2(t, 100000), Is.EqualTo(0));
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 130000), Is.EqualTo(30000));
        }

        [Test]
        public void Pausar_CongelaElArea()
        {
            var t = Nueva();
            Assert.That(TareaReglas.Pausar(t, T0.AddHours(1), 20000, out _), Is.True);

            Assert.That(t.Estado, Is.EqualTo(EstadoTarea.Pausada));
            // Lo que se pinte en pausa (p. ej. repasar una cabecera) no suma.
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 50000), Is.EqualTo(20000));
            Assert.That(t.Tramos[0].Fin, Is.EqualTo(T0.AddHours(1)));
        }

        [Test]
        public void Reanudar_SumaDesdeElAreaDelMomento()
        {
            var t = Nueva();
            TareaReglas.Pausar(t, T0.AddHours(1), 20000, out _);
            Assert.That(TareaReglas.Reanudar(t, T0.AddHours(2), 25000, out _), Is.True);

            Assert.That(t.Estado, Is.EqualTo(EstadoTarea.Activa));
            Assert.That(t.Tramos, Has.Count.EqualTo(2));
            // 20000 de antes + (40000 - 25000) de ahora; los 5000 de la pausa no.
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 40000), Is.EqualTo(35000));
        }

        [Test]
        public void Cerrar_DesdeActiva_FijaFinYArea()
        {
            var t = Nueva();
            Assert.That(TareaReglas.Cerrar(t, T0.AddHours(3), 42000, out _), Is.True);

            Assert.That(t.Estado, Is.EqualTo(EstadoTarea.Cerrada));
            Assert.That(t.Fin, Is.EqualTo(T0.AddHours(3)));
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 99999), Is.EqualTo(42000));
            Assert.That(t.Tramos.All(x => x.Fin != null), Is.True);
        }

        [Test]
        public void Cerrar_DesdePausada_NoSumaLoDeLaPausa()
        {
            var t = Nueva();
            TareaReglas.Pausar(t, T0.AddHours(1), 10000, out _);
            TareaReglas.Cerrar(t, T0.AddHours(2), 30000, out _);

            Assert.That(TareaReglas.AreaTrabajadaM2(t, 30000), Is.EqualTo(10000));
            Assert.That(t.Fin, Is.EqualTo(T0.AddHours(2)));
        }

        [Test]
        public void TransicionesInvalidas_SeRechazanConMotivo()
        {
            var t = Nueva();
            Assert.That(TareaReglas.Reanudar(t, T0, 0, out string e1), Is.False);
            Assert.That(e1, Is.Not.Empty);

            TareaReglas.Cerrar(t, T0.AddHours(1), 0, out _);
            Assert.That(TareaReglas.Pausar(t, T0.AddHours(2), 0, out string e2), Is.False);
            Assert.That(e2, Is.Not.Empty);
            Assert.That(TareaReglas.Cerrar(t, T0.AddHours(2), 0, out string e3), Is.False);
            Assert.That(e3, Is.Not.Empty);
            // El rechazo no toca la tarea.
            Assert.That(t.Fin, Is.EqualTo(T0.AddHours(1)));
        }

        [Test]
        public void BorrarPintado_ConTareaActiva_NoPierdeLoYaTrabajado()
        {
            var t = Nueva();
            TareaReglas.Observar(t, 30000);
            // El operario borra el pintado: el contador del lote vuelve a 0.
            TareaReglas.Observar(t, 0);
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 0), Is.EqualTo(30000));

            // Y lo que trabaje después se sigue sumando desde ahí.
            TareaReglas.Observar(t, 5000);
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 5000), Is.EqualTo(35000));
        }

        [Test]
        public void AreaQueBaja_SinObservar_NuncaDaNegativo()
        {
            var t = Nueva(areaInicial: 50000);
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 10000), Is.EqualTo(0));
        }

        [Test]
        public void TiempoEfectivo_DescuentaLasPausas()
        {
            var t = Nueva();
            TareaReglas.Pausar(t, T0.AddMinutes(90), 0, out _);
            TareaReglas.Reanudar(t, T0.AddMinutes(150), 0, out _);

            Assert.That(TareaReglas.TiempoEfectivo(t, T0.AddMinutes(180)),
                        Is.EqualTo(TimeSpan.FromMinutes(120)));
        }

        [Test]
        public void Abierta_DevuelveLaNoCerrada()
        {
            var a = Nueva();
            TareaReglas.Cerrar(a, T0.AddHours(1), 0, out _);
            var b = TareaReglas.Crear("t2", "L", "", TipoTrabajo.Otro, "", null, T0.AddHours(2), 0);
            TareaReglas.Pausar(b, T0.AddHours(3), 0, out _);

            Assert.That(TareaReglas.Abierta(new List<Tarea> { a, b }), Is.SameAs(b));
            Assert.That(TareaReglas.Abierta(new List<Tarea> { a }), Is.Null);
        }
    }

    public class TareaFormatoTests
    {
        [TestCase(0, "0,00 ha")]
        [TestCase(12345.6, "1,23 ha")]
        [TestCase(1234567, "123,46 ha")]
        public void Hectareas(double m2, string esperado)
        {
            Assert.That(TareaFormato.Hectareas(m2), Is.EqualTo(esperado));
        }

        [TestCase(0, "0 min")]
        [TestCase(45, "45 min")]
        [TestCase(125, "2 h 05 min")]
        public void Duracion(int minutos, string esperado)
        {
            Assert.That(TareaFormato.Duracion(TimeSpan.FromMinutes(minutos)), Is.EqualTo(esperado));
        }

        [TestCase(80, "kg_ha", "80 kg/ha")]
        [TestCase(2.5, "l_ha", "2,5 L/ha")]
        [TestCase(75000, "sem_ha", "75.000 sem/ha")]
        [TestCase(14, "sem_m", "14 sem/m")]
        [TestCase(0, "kg_ha", "")]
        public void Dosis(double valor, string unidad, string esperado)
        {
            Assert.That(TareaFormato.Dosis(valor, unidad), Is.EqualTo(esperado));
        }

        [Test]
        public void EtiquetasDeTipo_EnCastellano()
        {
            Assert.That(TipoTrabajo.Etiqueta(TipoTrabajo.Pulverizacion), Is.EqualTo("Pulverización"));
            Assert.That(TipoTrabajo.Etiqueta(TipoTrabajo.Fertilizacion), Is.EqualTo("Fertilización"));
            Assert.That(TipoTrabajo.Etiqueta("cualquiera"), Is.EqualTo("Otro"));
        }
    }

    public class TareaInformeTests
    {
        private static Tarea Cerrada()
        {
            var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
            var insumo = new InsumoDto { Nombre = "Urea <granulada>", Tipo = "fertilizante", DosisKgha = 100 };
            var t = TareaReglas.Crear("t1", "Lote & Cía", "Maíz", TipoTrabajo.Fertilizacion,
                "Ojo con el bajo <norte>", insumo, t0, 0);
            TareaReglas.Cerrar(t, t0.AddMinutes(150), 123456, out _);
            return t;
        }

        [Test]
        public void Informe_TraeLosDatosDeLaTarea()
        {
            string html = TareaInforme.ArmarHtml(Cerrada(), new DateTime(2026, 10, 1, 12, 0, 0), "x_cobertura.shp");

            Assert.That(html, Does.Contain("Fertilización"));
            Assert.That(html, Does.Contain("Maíz"));
            Assert.That(html, Does.Contain("12,35 ha"));
            Assert.That(html, Does.Contain("100 kg/ha"));
            Assert.That(html, Does.Contain("01/10/2026 08:00"));
            Assert.That(html, Does.Contain("01/10/2026 10:30"));
            Assert.That(html, Does.Contain("2 h 30 min"));
            Assert.That(html, Does.Contain("x_cobertura.shp"));
            // Producto estimado = dosis × área: 100 kg/ha × 12,35 ha.
            Assert.That(html, Does.Contain("1.235 kg"));
            Assert.That(html, Does.Contain("PilotX"));
        }

        [Test]
        public void Informe_EscapaElTextoDelOperario()
        {
            string html = TareaInforme.ArmarHtml(Cerrada(), DateTime.Now, null);

            Assert.That(html, Does.Contain("Lote &amp; Cía"));
            Assert.That(html, Does.Contain("Ojo con el bajo &lt;norte&gt;"));
            Assert.That(html, Does.Contain("Urea &lt;granulada&gt;"));
            Assert.That(html, Does.Not.Contain("<norte>"));
        }

        [Test]
        public void Informe_EsImprimible()
        {
            string html = TareaInforme.ArmarHtml(Cerrada(), DateTime.Now, null);
            Assert.That(html, Does.StartWith("<!DOCTYPE html>"));
            Assert.That(html, Does.Contain("@media print"));
            Assert.That(html, Does.Contain("charset=\"utf-8\""));
        }
    }

    public class TareasStoreTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pilotx_tareas_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void SinArchivo_DevuelveListaVacia()
        {
            var a = TareasStore.Cargar(_dir);
            Assert.That(a.Tareas, Is.Empty);
            // Leer NO crea nada en la carpeta del lote.
            Assert.That(Directory.GetFiles(_dir), Is.Empty);
        }

        [Test]
        public void GuardarYCargar_IdaYVuelta()
        {
            var t = TareaReglas.Crear("t1", "L", "Trigo", TipoTrabajo.Cosecha, "notas", null,
                new DateTime(2026, 10, 1, 8, 0, 0), 500);
            TareaReglas.Pausar(t, new DateTime(2026, 10, 1, 9, 0, 0), 2500, out _);
            var a = new TareasArchivo();
            a.Tareas.Add(t);

            TareasStore.Guardar(_dir, a);
            var leida = TareasStore.Cargar(_dir).Tareas.Single();

            Assert.That(File.Exists(Path.Combine(_dir, "Tareas.json")), Is.True);
            Assert.That(leida.Id, Is.EqualTo("t1"));
            Assert.That(leida.Cultivo, Is.EqualTo("Trigo"));
            Assert.That(leida.TipoTrabajo, Is.EqualTo(TipoTrabajo.Cosecha));
            Assert.That(leida.Estado, Is.EqualTo(EstadoTarea.Pausada));
            Assert.That(leida.Inicio, Is.EqualTo(new DateTime(2026, 10, 1, 8, 0, 0)));
            Assert.That(leida.Tramos[0].Fin, Is.EqualTo(new DateTime(2026, 10, 1, 9, 0, 0)));
            Assert.That(TareaReglas.AreaTrabajadaM2(leida, 0), Is.EqualTo(2000));
        }

        [Test]
        public void Guardar_NoTocaLosArchivosDelLote()
        {
            string sections = Path.Combine(_dir, "Sections.txt");
            File.WriteAllText(sections, "original");

            TareasStore.Guardar(_dir, new TareasArchivo());

            Assert.That(File.ReadAllText(sections), Is.EqualTo("original"));
        }

        [Test]
        public void ArchivoRoto_NoTiraYDevuelveVacio()
        {
            File.WriteAllText(Path.Combine(_dir, "Tareas.json"), "{ esto no es json");
            Assert.That(TareasStore.Cargar(_dir).Tareas, Is.Empty);
        }
    }

    public class TareaCoberturaShpTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pilotx_tareashp_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        // Plano local trivial para el test: 1 m = 0,00001°.
        private static double[] AGrados(double e, double n) => new[] { -34.0 + n * 1e-5, -60.0 + e * 1e-5 };

        private static CoverageStrip Tira(params double[] en)
        {
            var s = new CoverageStrip { Vertices = new List<CoverageVertex>() };
            for (int i = 0; i < en.Length; i += 2) s.Vertices.Add(new CoverageVertex(en[i], en[i + 1]));
            return s;
        }

        [Test]
        public void TiraDeTriangulos_SeVuelvePoligonoConSuArea()
        {
            // Un tramo recto de 10 m de ancho x 20 m de largo, como lo pinta
            // una sección: pares izquierda/derecha a medida que avanza.
            var cob = new CoverageSnapshot
            {
                Sections = new List<CoverageSection>
                {
                    new CoverageSection { Index = 0, Strips = new List<CoverageStrip>
                    {
                        Tira(0, 0, 10, 0, 0, 10, 10, 10, 0, 20, 10, 20),
                        Tira(),                       // parche vacío: se saltea
                    } }
                }
            };
            string shp = Path.Combine(_dir, "t_cobertura.shp");

            int n = TareaCoberturaShp.Exportar(cob, AGrados, shp, "t1");

            Assert.That(n, Is.EqualTo(1));
            Assert.That(File.Exists(shp), Is.True);
            Assert.That(File.Exists(Path.ChangeExtension(shp, ".shx")), Is.True);
            Assert.That(File.Exists(Path.ChangeExtension(shp, ".dbf")), Is.True);
            Assert.That(File.ReadAllText(Path.ChangeExtension(shp, ".prj")), Does.Contain("WGS_1984"));

            var feats = Shapefile.ReadAllFeatures(shp);
            Assert.That(feats.Length, Is.EqualTo(1));
            Assert.That(Convert.ToDouble(feats[0].Attributes["area_m2"]), Is.EqualTo(200).Within(0.01));
            Assert.That(feats[0].Attributes["tarea"]?.ToString(), Is.EqualTo("t1"));
            // El contorno cubre los 4 vértices extremos (en grados).
            var env = feats[0].Geometry.EnvelopeInternal;
            Assert.That(env.MinX, Is.EqualTo(-60.0).Within(1e-9));
            Assert.That(env.MaxX, Is.EqualTo(-60.0 + 10e-5).Within(1e-9));
            Assert.That(env.MaxY, Is.EqualTo(-34.0 + 20e-5).Within(1e-9));
        }

        [Test]
        public void SinCobertura_NoEscribeNada()
        {
            string shp = Path.Combine(_dir, "vacio.shp");
            int n = TareaCoberturaShp.Exportar(new CoverageSnapshot { Sections = new List<CoverageSection>() },
                AGrados, shp, "t1");

            Assert.That(n, Is.EqualTo(0));
            Assert.That(File.Exists(shp), Is.False);
        }
    }

    public class TareasServiceTests
    {
        private string _lote;
        private string _export;
        private double _area;
        private DateTime _ahora;
        private InsumoDto _insumo;
        private bool _loteAbierto;
        private TareasService _svc;

        [SetUp]
        public void SetUp()
        {
            string raiz = Path.Combine(Path.GetTempPath(), "pilotx_tareasvc_" + Path.GetRandomFileName());
            _lote = Path.Combine(raiz, "Fields", "La Loma");
            _export = Path.Combine(raiz, "USB");
            Directory.CreateDirectory(_lote);
            Directory.CreateDirectory(_export);
            _area = 0;
            _ahora = new DateTime(2026, 10, 1, 8, 0, 0);
            _insumo = null;
            _loteAbierto = true;
            _svc = new TareasService(
                loteDir: () => _loteAbierto ? _lote : null,
                areaTrabajadaM2: () => _area,
                insumoActivo: () => _insumo,
                cobertura: () => new CoverageSnapshot
                {
                    Sections = new List<CoverageSection>
                    {
                        new CoverageSection { Strips = new List<CoverageStrip>
                        {
                            new CoverageStrip { Vertices = new List<CoverageVertex>
                            {
                                new CoverageVertex(0, 0), new CoverageVertex(10, 0),
                                new CoverageVertex(0, 10), new CoverageVertex(10, 10),
                            } }
                        } }
                    }
                },
                aLatLon: (e, n) => new[] { -34.0 + n * 1e-5, -60.0 + e * 1e-5 },
                reloj: () => _ahora);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_lote)), true); } catch { }
        }

        private TareaCrearPedido Pedido(string cultivo = "Soja", string tipo = TipoTrabajo.Siembra)
            => new TareaCrearPedido { Cultivo = cultivo, TipoTrabajo = tipo, Notas = "" };

        [Test]
        public void SinLote_NoDejaCrear()
        {
            _loteAbierto = false;
            var r = _svc.Crear(Pedido());

            Assert.That(r.Ok, Is.False);
            Assert.That(r.HayLote, Is.False);
            Assert.That(r.Error, Is.Not.Empty);
        }

        [Test]
        public void Crear_PersisteEnLaCarpetaDelLote()
        {
            var r = _svc.Crear(Pedido());

            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(r.Lote, Is.EqualTo("La Loma"));
            Assert.That(r.Abierta, Is.Not.Null);
            Assert.That(r.Abierta.Estado, Is.EqualTo(EstadoTarea.Activa));
            Assert.That(File.Exists(Path.Combine(_lote, "Tareas.json")), Is.True);
        }

        [Test]
        public void SoloUnaTareaAbiertaPorLote()
        {
            _svc.Crear(Pedido());
            var r = _svc.Crear(Pedido("Maíz"));

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Does.Contain("abierta"));
        }

        [Test]
        public void CicloCompleto_AreaYTiempoEnElEstado()
        {
            _svc.Crear(Pedido());
            _area = 20000; _ahora = _ahora.AddHours(1);
            _svc.Pausar();
            _area = 25000; _ahora = _ahora.AddHours(1);
            _svc.Reanudar();
            _area = 40000; _ahora = _ahora.AddHours(1);

            var vivo = _svc.Estado();
            Assert.That(vivo.Abierta.AreaHa, Is.EqualTo(3.5).Within(1e-9));
            Assert.That(vivo.Abierta.AreaTexto, Is.EqualTo("3,50 ha"));
            Assert.That(vivo.Abierta.DuracionTexto, Is.EqualTo("2 h 00 min"));

            var cerrada = _svc.Cerrar();
            Assert.That(cerrada.Ok, Is.True);
            Assert.That(cerrada.Abierta, Is.Null);
            Assert.That(cerrada.Cerradas, Has.Count.EqualTo(1));
            Assert.That(cerrada.Cerradas[0].AreaHa, Is.EqualTo(3.5).Within(1e-9));
        }

        [Test]
        public void CerrarElLote_PausaLaTareaActiva()
        {
            _svc.Crear(Pedido());
            _area = 10000;
            _svc.AntesDeCerrarLote();

            var t = TareasStore.Cargar(_lote).Tareas.Single();
            Assert.That(t.Estado, Is.EqualTo(EstadoTarea.Pausada));
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 0), Is.EqualTo(10000));
        }

        [Test]
        public void CerrarElLote_ConElAreaVolviendoACero_NoPierdeNiInventaHectareas()
        {
            // El motor pone el área del lote en cero al cerrarlo (después de
            // AntesDeCerrarLote) y la reconstruye desde Sections.txt al
            // reabrirlo. La tarea tiene que sumar lo mismo que si nada pasara.
            _svc.Crear(Pedido());
            _area = 10000; _svc.Tick();

            _svc.AntesDeCerrarLote();          // lote todavía abierto, área vigente
            _area = 0; _loteAbierto = false;   // CloseField: contadores a cero
            _svc.Tick();                       // sin lote: no toca nada

            _area = 0; _loteAbierto = true;    // reabre: arranca en cero...
            _svc.Tick();
            _area = 10000; _svc.Tick();        // ...y CargarCobertura lo reconstruye

            Assert.That(_svc.Estado().Abierta.AreaHa, Is.EqualTo(1.0).Within(1e-9),
                "en pausa: lo trabajado antes de cerrar");

            _svc.Reanudar();
            _area = 25000;
            var r = _svc.Cerrar();
            Assert.That(r.Cerradas[0].AreaHa, Is.EqualTo(2.5).Within(1e-9),
                "1 ha antes de cerrar + 1,5 ha después de reanudar");
        }

        [Test]
        public void Tick_DetectaBorrarPintadoYLoPersiste()
        {
            _svc.Crear(Pedido());
            _area = 30000; _svc.Tick();
            _area = 0; _svc.Tick();       // borraron el pintado
            _area = 5000;

            Assert.That(_svc.Estado().Abierta.AreaHa, Is.EqualTo(3.5).Within(1e-9));
            // Y quedó en disco: un reinicio de PilotX no lo pierde.
            var t = TareasStore.Cargar(_lote).Tareas.Single();
            Assert.That(TareaReglas.AreaTrabajadaM2(t, 5000), Is.EqualTo(35000));
        }

        [Test]
        public void Crear_TomaElInsumoActivo()
        {
            _insumo = new InsumoDto { Nombre = "Maíz DK 72-10", Tipo = "semilla", Cultivo = "maíz",
                                      DosisKgha = 75000, DosisUnidad = "sem_ha" };
            var r = _svc.Crear(Pedido(cultivo: ""));

            Assert.That(r.Abierta.Cultivo, Is.EqualTo("maíz"));
            Assert.That(r.Abierta.Insumo, Is.EqualTo("Maíz DK 72-10"));
            Assert.That(r.Abierta.DosisTexto, Is.EqualTo("75.000 sem/ha"));
        }

        [Test]
        public void Exportar_TareaAbierta_SeRechaza()
        {
            var r = _svc.Crear(Pedido());
            var ex = _svc.Exportar(r.Abierta.Id, Path.Combine(_export, "x.html"));

            Assert.That(ex.Ok, Is.False);
            Assert.That(ex.Error, Is.Not.Empty);
        }

        [Test]
        public void Exportar_TareaCerrada_DejaInformeYShpEnElDestino()
        {
            var r = _svc.Crear(Pedido());
            _area = 100; _ahora = _ahora.AddHours(1);
            _svc.Cerrar();

            var ex = _svc.Exportar(r.Abierta.Id, Path.Combine(_export, "Tarea La Loma.html"));

            Assert.That(ex.Ok, Is.True, ex.Error);
            Assert.That(File.Exists(Path.Combine(_export, "Tarea La Loma.html")), Is.True);
            Assert.That(File.Exists(Path.Combine(_export, "Tarea La Loma_cobertura.shp")), Is.True);
            Assert.That(File.Exists(Path.Combine(_export, "Tarea La Loma_cobertura.dbf")), Is.True);
            Assert.That(ex.Archivos, Has.Count.GreaterThanOrEqualTo(5));
            // El export no deja nada nuevo en la carpeta del lote (solo Tareas.json y su .bak).
            Assert.That(Directory.GetFiles(_lote).Select(Path.GetFileName)
                        .All(f => f.StartsWith("Tareas.json", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void Exportar_IdInexistente_Rechaza()
        {
            var ex = _svc.Exportar("no-existe", Path.Combine(_export, "x.html"));
            Assert.That(ex.Ok, Is.False);
        }
    }
}
