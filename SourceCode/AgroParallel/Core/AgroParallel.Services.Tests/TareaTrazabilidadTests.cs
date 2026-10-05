// ============================================================================
// TareaTrazabilidadTests.cs — tarea FINALIZADA bloqueada con snapshot.
//
//   · al iniciar se copia la configuración (implemento, perfil, FlowX,
//     operario): cambiarla después no cambia la tarea;
//   · al finalizar se sella (SHA-256 de la forma canónica): la tarea queda
//     inmutable — ninguna transición sale de "cerrada" — y una edición a mano
//     del Tareas.json se detecta;
//   · Tareas.json viejo (sin los campos nuevos) se sigue leyendo igual;
//   · el informe exportado muestra el snapshot y la integridad.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Tareas;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class TareaSelloTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 1, 8, 0, 0);

        private static TareaSnapshot Snap() => new TareaSnapshot
        {
            Vehiculo = "JD 6120M",
            Implemento = "Pulverizadora 28",
            AnchoM = 28,
            Secciones = 7,
            AnchosSeccionesM = new List<double> { 4, 4, 4, 4, 4, 4, 4 },
            FlowX = new List<TareaSnapshotFlowX>
            {
                new TareaSnapshotFlowX { Nodo = "Barra", Producto = "Glifosato", DosisLha = 100, MeterCal = 650 },
            },
            Operario = "Juan",
        };

        private static Tarea Finalizada()
        {
            var t = TareaReglas.Crear("t1", "La Loma", "Soja", TipoTrabajo.Pulverizacion, "", null, T0, 0, Snap());
            TareaReglas.Cerrar(t, T0.AddHours(2), 150000, out _);
            return t;
        }

        [Test]
        public void Crear_CopiaElSnapshot_NoLoComparte()
        {
            var s = Snap();
            var t = TareaReglas.Crear("t1", "L", "", TipoTrabajo.Pulverizacion, "", null, T0, 0, s);

            s.Implemento = "OTRO";
            s.FlowX[0].DosisLha = 999;
            s.AnchosSeccionesM[0] = 99;

            Assert.That(t.Snapshot, Is.Not.SameAs(s));
            Assert.That(t.Snapshot.Implemento, Is.EqualTo("Pulverizadora 28"));
            Assert.That(t.Snapshot.FlowX[0].DosisLha, Is.EqualTo(100));
            Assert.That(t.Snapshot.AnchosSeccionesM[0], Is.EqualTo(4));
            Assert.That(t.Snapshot.Tomado, Is.EqualTo(T0));
        }

        [Test]
        public void Crear_Siembra_NoGuardaFlowX()
        {
            var t = TareaReglas.Crear("t1", "L", "", TipoTrabajo.Siembra, "", null, T0, 0, Snap());

            Assert.That(t.Snapshot.FlowX, Is.Empty, "FlowX es dosis líquida: en una siembra confundiría");
            Assert.That(t.Snapshot.Implemento, Is.EqualTo("Pulverizadora 28"));
        }

        [Test]
        public void Crear_TomaElTipoDelInsumo()
        {
            var ins = new InsumoDto { Nombre = "Glifo", Tipo = "fitosanitario", DosisLha = 2 };
            var t = TareaReglas.Crear("t1", "L", "", TipoTrabajo.Pulverizacion, "", ins, T0, 0, Snap());

            Assert.That(t.Snapshot.InsumoTipo, Is.EqualTo("fitosanitario"));
        }

        [Test]
        public void Finalizar_Sella()
        {
            var t = Finalizada();

            Assert.That(t.Sello, Has.Length.EqualTo(64));
            Assert.That(t.SelloVersion, Is.EqualTo(1));
            Assert.That(TareaSello.Verificar(t), Is.EqualTo(IntegridadTarea.Ok));
        }

        [Test]
        public void TareaAbierta_NoTieneSello()
        {
            var t = TareaReglas.Crear("t1", "L", "", TipoTrabajo.Siembra, "", null, T0, 0, Snap());

            Assert.That(t.Sello, Is.Null);
            Assert.That(TareaSello.Verificar(t), Is.EqualTo(IntegridadTarea.Abierta));
        }

        [Test]
        public void Finalizada_NoSeReabreNiSePausaNiSeVuelveACerrar()
        {
            var t = Finalizada();
            string sello = t.Sello;

            Assert.That(TareaReglas.Reanudar(t, T0.AddHours(3), 0, out var e1), Is.False);
            Assert.That(e1, Does.Contain("finalizada"));
            Assert.That(TareaReglas.Pausar(t, T0.AddHours(3), 0, out _), Is.False);
            Assert.That(TareaReglas.Cerrar(t, T0.AddHours(3), 0, out _), Is.False);
            Assert.That(TareaReglas.Observar(t, 999999), Is.False);

            Assert.That(t.Sello, Is.EqualTo(sello));
            Assert.That(t.Fin, Is.EqualTo(T0.AddHours(2)));
            Assert.That(TareaSello.Verificar(t), Is.EqualTo(IntegridadTarea.Ok));
        }

        [TestCase("area")]
        [TestCase("dosis_flowx")]
        [TestCase("implemento")]
        [TestCase("operario")]
        [TestCase("notas")]
        [TestCase("fin")]
        [TestCase("tramo")]
        [TestCase("sin_snapshot")]
        public void CualquierCambio_SeDetecta(string campo)
        {
            var t = Finalizada();
            switch (campo)
            {
                case "area": t.AreaAcumuladaM2 += 1; break;
                case "dosis_flowx": t.Snapshot.FlowX[0].DosisLha = 80; break;
                case "implemento": t.Snapshot.Implemento = "Otra"; break;
                case "operario": t.Snapshot.Operario = "Pedro"; break;
                case "notas": t.Notas = "agregado después"; break;
                case "fin": t.Fin = t.Fin.Value.AddMinutes(1); break;
                case "tramo": t.Tramos[0].Inicio = t.Tramos[0].Inicio.AddSeconds(1); break;
                case "sin_snapshot": t.Snapshot = null; break;
            }

            Assert.That(TareaSello.Verificar(t), Is.EqualTo(IntegridadTarea.Alterada));
        }

        [Test]
        public void ReabiertaEditandoElArchivo_SeDetecta()
        {
            var t = Finalizada();
            t.Estado = EstadoTarea.Pausada;

            Assert.That(TareaSello.Verificar(t), Is.EqualTo(IntegridadTarea.Alterada));
        }

        [Test]
        public void FinalizadaSinSello_EsLegado()
        {
            var t = Finalizada();
            t.Sello = null;
            t.SelloVersion = null;

            Assert.That(TareaSello.Verificar(t), Is.EqualTo(IntegridadTarea.SinSello));
        }

        [Test]
        public void SelloSobreviveGuardarYLeer_ConHoraLocal()
        {
            // Producción usa DateTime.Now (Kind Local): el JSON la escribe con
            // zona y al leer vuelve como Local. El sello no puede cambiar.
            var ahora = DateTime.Now;
            var t = TareaReglas.Crear("t1", "L", "", TipoTrabajo.Pulverizacion, "x\ny", null, ahora, 0.1, Snap());
            TareaReglas.Cerrar(t, ahora.AddMinutes(93.123), 12345.678901, out _);

            string dir = Path.Combine(Path.GetTempPath(), "pilotx_sello_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                TareasStore.Guardar(dir, new TareasArchivo { Tareas = { t } });
                var leida = TareasStore.Cargar(dir).Tareas.Single();
                Assert.That(TareaSello.Verificar(leida), Is.EqualTo(IntegridadTarea.Ok));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void TareasJsonViejo_SinCamposNuevos_SeLeeYNoSeInventaSello()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pilotx_sello_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "Tareas.json"),
                    "{\"version\":1,\"tareas\":[{\"id\":\"t1\",\"lote\":\"L\",\"estado\":\"cerrada\","
                    + "\"inicio\":\"2026-09-01T08:00:00\",\"fin\":\"2026-09-01T10:00:00\",\"area_acumulada_m2\":5000,"
                    + "\"tramos\":[{\"inicio\":\"2026-09-01T08:00:00\",\"fin\":\"2026-09-01T10:00:00\"}]}]}");

                var t = TareasStore.Cargar(dir).Tareas.Single();
                Assert.That(t.Snapshot, Is.Null);
                Assert.That(t.Sello, Is.Null);
                Assert.That(TareaSello.Verificar(t), Is.EqualTo(IntegridadTarea.SinSello));

                // Y al reescribir no aparecen campos nuevos vacíos (OrbitX y
                // otras herramientas leen la carpeta del lote).
                TareasStore.Guardar(dir, new TareasArchivo { Tareas = { t } });
                string json = File.ReadAllText(Path.Combine(dir, "Tareas.json"));
                Assert.That(json, Does.Not.Contain("\"snapshot\""));
                Assert.That(json, Does.Not.Contain("\"sello\""));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void Snapshot_ViajaConNombresSnakeCase()
        {
            var t = Finalizada();
            string json = JsonSerializer.Serialize(t);

            Assert.That(json, Does.Contain("\"snapshot\""));
            Assert.That(json, Does.Contain("\"anchos_secciones_m\""));
            Assert.That(json, Does.Contain("\"meter_cal\""));
            Assert.That(json, Does.Contain("\"dosis_lha\""));
            Assert.That(json, Does.Contain("\"sello\""));
        }

        [Test]
        public void Informe_MuestraSnapshotEIntegridad()
        {
            var t = Finalizada();
            string html = TareaInforme.ArmarHtml(t, T0.AddHours(3), null);

            Assert.That(html, Does.Contain("Configuración al iniciar la tarea"));
            Assert.That(html, Does.Contain("JD 6120M"));
            Assert.That(html, Does.Contain("Pulverizadora 28 · 28 m · 7 secciones"));
            Assert.That(html, Does.Contain("Glifosato: 100 L/ha · calibración 650 pulsos/L (nodo Barra)"));
            Assert.That(html, Does.Contain("Juan"));
            Assert.That(html, Does.Contain(t.Sello));
            Assert.That(html, Does.Contain("Sellada (sin cambios)"));
            Assert.That(html, Does.Not.Contain("ATENCIÓN"));
        }

        [Test]
        public void Informe_TareaAlterada_LoDiceEnGrande()
        {
            var t = Finalizada();
            t.Snapshot.FlowX[0].DosisLha = 50;

            string html = TareaInforme.ArmarHtml(t, T0.AddHours(3), null);
            Assert.That(html, Does.Contain("ATENCIÓN"));
            Assert.That(html, Does.Contain("ALTERADA"));
        }

        [Test]
        public void Informe_SinSnapshot_LoAclara()
        {
            var t = TareaReglas.Crear("t1", "L", "", TipoTrabajo.Siembra, "", null, T0, 0);
            TareaReglas.Cerrar(t, T0.AddHours(1), 0, out _);

            string html = TareaInforme.ArmarHtml(t, T0.AddHours(2), null);
            Assert.That(html, Does.Contain("No registrada"));
        }
    }

    public class TareasServiceTrazabilidadTests
    {
        private string _raiz;
        private string _lote;
        private DateTime _ahora;
        private double _area;
        private TareaSnapshot _snapHost;
        private TareasService _svc;

        [SetUp]
        public void SetUp()
        {
            _raiz = Path.Combine(Path.GetTempPath(), "pilotx_traz_" + Path.GetRandomFileName());
            _lote = Path.Combine(_raiz, "Fields", "La Loma");
            Directory.CreateDirectory(_lote);
            _ahora = new DateTime(2026, 10, 1, 8, 0, 0);
            _area = 0;
            _snapHost = new TareaSnapshot
            {
                Vehiculo = "JD 6120M", Implemento = "Pulverizadora 28", AnchoM = 28, Secciones = 7,
                FlowX = new List<TareaSnapshotFlowX> { new TareaSnapshotFlowX { Producto = "Glifosato", DosisLha = 100, MeterCal = 650 } },
            };
            _svc = new TareasService(
                loteDir: () => _lote,
                areaTrabajadaM2: () => _area,
                insumoActivo: () => null,
                cobertura: null,
                aLatLon: null,
                reloj: () => _ahora,
                snapshot: () => _snapHost);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        [Test]
        public void Crear_GuardaSnapshotYOperario()
        {
            var r = _svc.Crear(new TareaCrearPedido { TipoTrabajo = TipoTrabajo.Pulverizacion, Operario = " Juan " });

            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(r.Abierta.ImplementoTexto, Is.EqualTo("Pulverizadora 28 · 28 m · 7 secciones"));
            Assert.That(r.Abierta.Operario, Is.EqualTo("Juan"));
            Assert.That(r.Abierta.Vehiculo, Is.EqualTo("JD 6120M"));

            var t = TareasStore.Cargar(_lote).Tareas.Single();
            Assert.That(t.Snapshot.FlowX.Single().MeterCal, Is.EqualTo(650));
            Assert.That(t.Snapshot.Operario, Is.EqualTo("Juan"));
        }

        [Test]
        public void CambiarLaConfigDespuesDeIniciar_NoTocaLaTarea()
        {
            _svc.Crear(new TareaCrearPedido { TipoTrabajo = TipoTrabajo.Pulverizacion });
            _snapHost.Implemento = "Otra";
            _snapHost.FlowX[0].DosisLha = 60;
            _area = 10000; _ahora = _ahora.AddHours(1);
            _svc.Cerrar();

            var t = TareasStore.Cargar(_lote).Tareas.Single();
            Assert.That(t.Snapshot.Implemento, Is.EqualTo("Pulverizadora 28"));
            Assert.That(t.Snapshot.FlowX[0].DosisLha, Is.EqualTo(100));
        }

        [Test]
        public void SnapshotQueTira_NoFrenaLaTarea()
        {
            var svc = new TareasService(() => _lote, () => 0, () => null, null, null, () => _ahora,
                                        snapshot: () => throw new InvalidOperationException("sin motor"));
            var r = svc.Crear(new TareaCrearPedido { TipoTrabajo = TipoTrabajo.Siembra });

            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(TareasStore.Cargar(_lote).Tareas.Single().Snapshot, Is.Null);
        }

        [Test]
        public void Finalizar_DejaSelloVerificadoEnLaVista()
        {
            _svc.Crear(new TareaCrearPedido { TipoTrabajo = TipoTrabajo.Pulverizacion });
            _area = 10000; _ahora = _ahora.AddHours(1);
            var r = _svc.Cerrar();

            Assert.That(r.Cerradas.Single().Integridad, Is.EqualTo(IntegridadTarea.Ok));
            Assert.That(r.Cerradas.Single().EstadoTexto, Is.EqualTo("Finalizada"));
            Assert.That(r.Cerradas.Single().SelloCorto, Has.Length.EqualTo(12));
        }

        [Test]
        public void Finalizada_NoSeReanuda_YNuevaTareaNoLaToca()
        {
            _svc.Crear(new TareaCrearPedido { TipoTrabajo = TipoTrabajo.Pulverizacion });
            _area = 10000; _ahora = _ahora.AddHours(1);
            _svc.Cerrar();
            string sello = TareasStore.Cargar(_lote).Tareas.Single().Sello;

            var re = _svc.Reanudar();
            Assert.That(re.Ok, Is.False);

            // Otra tarea en el mismo lote reescribe Tareas.json: la finalizada
            // tiene que quedar idéntica (mismo sello, verificado).
            _ahora = _ahora.AddHours(1);
            _svc.Crear(new TareaCrearPedido { TipoTrabajo = TipoTrabajo.Pulverizacion });
            _area = 30000;
            _svc.Pausar();

            var vieja = TareasStore.Cargar(_lote).Tareas.First();
            Assert.That(vieja.Sello, Is.EqualTo(sello));
            Assert.That(TareaSello.Verificar(vieja), Is.EqualTo(IntegridadTarea.Ok));
        }

        [Test]
        public void EdicionAManoDelArchivo_SeVeComoAlterada()
        {
            _svc.Crear(new TareaCrearPedido { TipoTrabajo = TipoTrabajo.Pulverizacion });
            _area = 10000; _ahora = _ahora.AddHours(1);
            _svc.Cerrar();

            string ruta = Path.Combine(_lote, "Tareas.json");
            string json = File.ReadAllText(ruta).Replace("\"dosis_lha\": 100", "\"dosis_lha\": 70");
            File.WriteAllText(ruta, json);

            // Servicio nuevo = PilotX reiniciado: lee de disco.
            var svc2 = new TareasService(() => _lote, () => 0, () => null, null, null, () => _ahora);
            var e = svc2.Estado();
            Assert.That(e.Cerradas.Single().Integridad, Is.EqualTo(IntegridadTarea.Alterada));
        }

        [Test]
        public void Exportar_InformeConSnapshot()
        {
            _svc.Crear(new TareaCrearPedido { TipoTrabajo = TipoTrabajo.Pulverizacion, Operario = "Juan" });
            _area = 10000; _ahora = _ahora.AddHours(1);
            var c = _svc.Cerrar();
            string destino = Path.Combine(_raiz, "USB", "informe.html");

            var r = _svc.Exportar(c.Cerradas.Single().Id, destino);

            Assert.That(r.Ok, Is.True, r.Error);
            string html = File.ReadAllText(destino);
            Assert.That(html, Does.Contain("Pulverizadora 28"));
            Assert.That(html, Does.Contain("Glifosato: 100 L/ha"));
            Assert.That(html, Does.Contain("Juan"));
            Assert.That(html, Does.Contain("Sellada (sin cambios)"));
        }
    }
}
