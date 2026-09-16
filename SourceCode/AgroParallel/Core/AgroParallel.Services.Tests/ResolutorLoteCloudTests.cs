// ============================================================================
// ResolutorLoteCloudTests.cs — un lote que baja de OrbitX NUNCA pisa el lindero
// de un lote que hizo el operario (reporte 2026-09-12: "creé un lote y levantó
// un lindero que creé con OrbitX"). El marcador .orbitx es lo que distingue el
// lote espejo del cloud del lote local; sin él, "guardar aparte" degeneraría en
// (OrbitX 2), (OrbitX 3)… en cada ciclo de sync.
// ============================================================================

using System.IO;
using AgroParallel.Services.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class ResolutorLoteCloudTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "pilotx_lotes_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        // Crea una carpeta de lote "del operario": tiene Field.txt pero NO .orbitx.
        private string CrearLoteLocal(string nombre)
        {
            string dir = Path.Combine(_root, nombre);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Field.txt"), "$FieldDir\n");
            return dir;
        }

        [Test]
        public void LoteNuevo_SeCreaConElNombrePedido()
        {
            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.Crear));
            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12"));
            Assert.That(d.Directorio, Is.EqualTo(Path.Combine(_root, "Lote 12")));
        }

        [Test]
        public void LoteDelOperarioConEseNombre_NoSeTocaYVaConSufijo()
        {
            CrearLoteLocal("Lote 12");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.Crear));
            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12 (OrbitX)"));
        }

        [Test]
        public void EspejoConMismoSha_NoSeReescribe()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Boundary.txt"), "$Boundary\n");
            ResolutorLoteCloud.EscribirMarcador(dir, "Lote 12", "sha-a");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.SinCambios));
            Assert.That(d.Directorio, Is.EqualTo(dir));
        }

        // El marcador dice "mismo SHA" pero el Boundary.txt del espejo
        // desapareció (borrado a mano, sync anterior interrumpido, etc.): sin
        // este chequeo, "SinCambios" dejaría el lote sin lindero PARA SIEMPRE,
        // porque ni un re-push del cloud lo trae de vuelta (el SHA no cambia).
        [Test]
        public void EspejoConMismoShaPeroSinBoundaryTxt_SeReescribe()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            ResolutorLoteCloud.EscribirMarcador(dir, "Lote 12", "sha-a");
            // Sin Boundary.txt: el marcador quedó huérfano.

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.ActualizarEspejo));
            Assert.That(d.Directorio, Is.EqualTo(dir));
        }

        [Test]
        public void EspejoConShaDistinto_SeActualizaEnElMismoDirectorio()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            ResolutorLoteCloud.EscribirMarcador(dir, "Lote 12", "sha-vieja");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-nueva");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.ActualizarEspejo));
            Assert.That(d.Directorio, Is.EqualTo(dir));
        }

        [Test]
        public void NombreYSufijoOcupadosPorElOperario_VaAlSufijoNumerado()
        {
            CrearLoteLocal("Lote 12");
            CrearLoteLocal("Lote 12 (OrbitX)");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.Crear));
            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12 (OrbitX 2)"));
        }

        // La idempotencia es el punto del marcador: sin esto cada ciclo de sync
        // dejaría un lote nuevo.
        [Test]
        public void DosSyncsSeguidosSinCambios_NoDuplicanElLote()
        {
            CrearLoteLocal("Lote 12");

            var primera = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");
            Directory.CreateDirectory(primera.Directorio);
            File.WriteAllText(Path.Combine(primera.Directorio, "Boundary.txt"), "$Boundary\n");
            ResolutorLoteCloud.EscribirMarcador(primera.Directorio, "Lote 12", "sha-a");

            var segunda = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(segunda.Accion, Is.EqualTo(AccionLoteCloud.SinCambios));
            Assert.That(segunda.Directorio, Is.EqualTo(primera.Directorio));
            Assert.That(Directory.GetDirectories(_root).Length, Is.EqualTo(2));
        }

        [Test]
        public void MarcadorDeOtroLoteCloud_NoSeConsideraEspejo()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            ResolutorLoteCloud.EscribirMarcador(dir, "Otro lote", "sha-x");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12 (OrbitX)"));
        }

        [Test]
        public void MarcadorIlegible_SeTrataComoLoteDelOperario()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".orbitx"), "{ esto no es json");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12 (OrbitX)"));
        }

        [Test]
        public void VeinteSufijosOcupados_DevuelveSinLugar()
        {
            CrearLoteLocal("Lote 12");
            CrearLoteLocal("Lote 12 (OrbitX)");
            for (int i = 2; i <= 20; i++) CrearLoteLocal("Lote 12 (OrbitX " + i + ")");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.SinLugar));
        }

        [Test]
        public void ShaEsEstableYDistingueContenido()
        {
            var h1 = ResolutorLoteCloud.CalcularSha("hola");
            var h2 = ResolutorLoteCloud.CalcularSha("hola");
            Assert.That(h1, Is.EqualTo(h2));

            var h3 = ResolutorLoteCloud.CalcularSha("chau");
            Assert.That(h1, Is.Not.EqualTo(h3));
        }

        // Sin literal esperado: los caracteres inválidos de nombre de archivo
        // NO son los mismos en Windows y Linux (en Linux ':' y '*' son válidos)
        // y el repo compila para los dos. Se verifica la propiedad, no la cadena.
        [Test]
        public void NombreConCaracteresInvalidos_SeLimpia()
        {
            string sucio = "Lote" + new string(Path.GetInvalidFileNameChars()) + "12";

            var d = ResolutorLoteCloud.Resolver(_root, sucio, "sha-a");

            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote12"));
            Assert.That(d.NombreCarpeta.IndexOfAny(Path.GetInvalidFileNameChars()), Is.EqualTo(-1));
        }

        // ------------------------------------------------------------------
        // LeerLoteCloud — expuesto para que el borrado de lotes (arreglo del
        // tombstone del espejo) no duplique el parseo del marcador.
        // ------------------------------------------------------------------

        [Test]
        public void LeerLoteCloud_SinMarcador_DevuelveNull()
        {
            string dir = CrearLoteLocal("Campo Norte");

            Assert.That(ResolutorLoteCloud.LeerLoteCloud(dir), Is.Null);
        }

        [Test]
        public void LeerLoteCloud_ConMarcador_DevuelveElNombreCloud()
        {
            string dir = Path.Combine(_root, "Campo Norte (OrbitX)");
            Directory.CreateDirectory(dir);
            ResolutorLoteCloud.EscribirMarcador(dir, "Campo Norte", "sha-a");

            Assert.That(ResolutorLoteCloud.LeerLoteCloud(dir), Is.EqualTo("Campo Norte"));
        }

        [Test]
        public void LeerLoteCloud_MarcadorRoto_DevuelveNull()
        {
            string dir = Path.Combine(_root, "Campo Norte");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".orbitx"), "{ esto no es json");

            Assert.That(ResolutorLoteCloud.LeerLoteCloud(dir), Is.Null);
        }

        // ------------------------------------------------------------------
        // Arreglo 1 (2026-09-16) — EngineLotesService.DeleteFieldAsync exige
        // que "clean" (salida de CleanName, saneo indulgente que sólo saca
        // caracteres inválidos de archivo y hace .Trim()) sea EXACTAMENTE
        // igual al "name" pedido, o rechaza el borrado. Esa comparación vive
        // en PilotX.GuidanceEngine (sin suite de tests propia — no hay
        // AgroParallel.Services referenciándolo desde ahí) así que no es
        // testeable directo desde acá. LimpiarNombre implementa el MISMO
        // criterio de saneo que CleanName (ver su propio comentario), así que
        // estos tests documentan la premisa del arreglo: un nombre con
        // espacio inicial o final SÍ cambia al pasar por el saneo indulgente
        // (por eso hace falta la comparación exacta), y uno normal no cambia
        // (por eso la comparación exacta no rompe el camino feliz). El
        // repro real de EngineLotesService.DeleteFieldAsync está en el
        // reporte de esta ronda.
        // ------------------------------------------------------------------

        [Test]
        public void LimpiarNombre_ConEspacioInicial_DifiereDelNombrePedido()
        {
            string name = " Campo";
            string clean = ResolutorLoteCloud.LimpiarNombre(name);

            Assert.That(clean, Is.EqualTo("Campo"));
            Assert.That(clean, Is.Not.EqualTo(name));
        }

        [Test]
        public void LimpiarNombre_ConEspacioFinal_DifiereDelNombrePedido()
        {
            string name = "Campo ";
            string clean = ResolutorLoteCloud.LimpiarNombre(name);

            Assert.That(clean, Is.EqualTo("Campo"));
            Assert.That(clean, Is.Not.EqualTo(name));
        }

        [Test]
        public void LimpiarNombre_NombreNormal_CoincideConElNombrePedido()
        {
            string name = "Campo Norte";
            string clean = ResolutorLoteCloud.LimpiarNombre(name);

            // El camino feliz no se rompe: un nombre sin espacios de sobra ni
            // caracteres inválidos sale IGUAL al pedido, así que la
            // comparación exacta del arreglo 1 lo deja pasar.
            Assert.That(clean, Is.EqualTo(name));
        }

        // ------------------------------------------------------------------
        // EsCarpetaDeLoteBorrable — guarda REAL de borrado (arreglo 2026-09-16,
        // hallazgo crítico con repro ejecutado). Tabla completa del reporte:
        // "." y "..." resuelven al propio root (Windows colapsa los puntos
        // finales); "Campo.." y "Campo." resuelven a "Campo" pero con hoja
        // distinta del nombre pedido. Todos tienen que rechazarse; un nombre
        // normal tiene que pasar.
        //
        // (QuedaDentroDeRoot, la guarda genérica de contención que precedió a
        // ésta, se sacó del código de producción y de estos tests: desde que
        // la contención real vive acá, quedó sin ningún caller — hallazgo
        // 2026-09-16, limpieza.)
        // ------------------------------------------------------------------

        [Test]
        public void EsCarpetaDeLoteBorrable_NombreNormal_Pasa()
        {
            CrearLoteLocal("Campo Norte");

            bool ok = ResolutorLoteCloud.EsCarpetaDeLoteBorrable(_root, "Campo Norte", out string dir);

            Assert.That(ok, Is.True);
            Assert.That(dir, Is.EqualTo(Path.Combine(_root, "Campo Norte")));
        }

        [Test]
        public void EsCarpetaDeLoteBorrable_Punto_Rechaza()
        {
            // "." resuelve al propio root — borrarlo es borrar TODOS los lotes.
            Assert.That(ResolutorLoteCloud.EsCarpetaDeLoteBorrable(_root, ".", out _), Is.False);
        }

        [Test]
        public void EsCarpetaDeLoteBorrable_TresPuntos_Rechaza()
        {
            // "..." también resuelve al propio root (Windows colapsa los puntos
            // finales de un segmento de ruta).
            Assert.That(ResolutorLoteCloud.EsCarpetaDeLoteBorrable(_root, "...", out _), Is.False);
        }

        [Test]
        public void EsCarpetaDeLoteBorrable_PuntoConEspacios_TrasLimpiarNombreQuedaEnPunto_Rechaza()
        {
            // El controller/adaptador aplica CleanName/LimpiarNombre ANTES de
            // llamar acá (mismo criterio de saneo en las dos clases — ver el
            // comentario de LimpiarNombre), y "  .  " limpia a ".". Se pasa
            // "  .  " por el propio LimpiarNombre para probar la premisa del
            // test (no repetir el caso de "." a secas, ya cubierto arriba).
            string limpio = ResolutorLoteCloud.LimpiarNombre("  .  ");
            Assert.That(limpio, Is.EqualTo("."));

            Assert.That(ResolutorLoteCloud.EsCarpetaDeLoteBorrable(_root, limpio, out _), Is.False);
        }

        [Test]
        public void EsCarpetaDeLoteBorrable_NombreConDosPuntosFinales_Rechaza()
        {
            CrearLoteLocal("Campo");

            // "Campo.." resuelve a "Fields/Campo" (existe), pero la hoja
            // resuelta ("Campo") no coincide con el nombre pedido
            // ("Campo.."): sin este chequeo se borraría "Campo" salteando la
            // guarda del lote abierto, que compara contra el nombre crudo.
            Assert.That(ResolutorLoteCloud.EsCarpetaDeLoteBorrable(_root, "Campo..", out _), Is.False);
        }

        [Test]
        public void EsCarpetaDeLoteBorrable_NombreConUnPuntoFinal_Rechaza()
        {
            CrearLoteLocal("Campo");

            Assert.That(ResolutorLoteCloud.EsCarpetaDeLoteBorrable(_root, "Campo.", out _), Is.False);
        }

        [Test]
        public void EsCarpetaDeLoteBorrable_PuntoPuntoHaciaAfuera_Rechaza()
        {
            Assert.That(ResolutorLoteCloud.EsCarpetaDeLoteBorrable(_root, "..", out _), Is.False);
        }

        // ------------------------------------------------------------------
        // NombresATombstonearTrasBorrar — arreglos 2 y 3: qué nombres hay que
        // avisarle al sync que no vuelvan a bajar tras borrar una carpeta.
        // Función pura para que la decisión quede testeada (antes vivía sólo
        // en EngineLotesService.DeleteFieldAsync, sin ningún test).
        // ------------------------------------------------------------------

        [Test]
        public void NombresATombstonear_CarpetaPropiaSinMarcador_EncolaSoloSuNombre()
        {
            // El operario borró su propio lote, sin marcador .orbitx y sin
            // ningún hermano que reclame ese nombre cloud.
            CrearLoteLocal("Otro Lote"); // hermano irrelevante

            var r = ResolutorLoteCloud.NombresATombstonearTrasBorrar(_root, "Campo Norte", null);

            Assert.That(r, Is.EquivalentTo(new[] { "Campo Norte" }));
        }

        [Test]
        public void NombresATombstonear_CarpetaEraElEspejo_EncolaLosDosNombres()
        {
            // La carpeta borrada era "Campo (OrbitX)" con lote_cloud="Campo":
            // hay que tombstonear los dos, porque el pendiente que baja del
            // cloud llega con ruta_rel="Fields/Campo/…", sin resolver contra
            // el sufijo "(OrbitX)".
            var r = ResolutorLoteCloud.NombresATombstonearTrasBorrar(_root, "Campo (OrbitX)", "Campo");

            Assert.That(r, Is.EquivalentTo(new[] { "Campo (OrbitX)", "Campo" }));
        }

        [Test]
        public void NombresATombstonear_HermanoEspejoSeQuedaConElNombreCloud_NoLoEncola()
        {
            // Caso del arreglo 2: el operario borra SU PROPIO "Campo" (sin
            // marcador), pero queda vivo un hermano "Campo (OrbitX)" cuyo
            // marcador dice lote_cloud="Campo". Tombstonear "Campo" bloquearía
            // para siempre al hermano, que sigue vivo y esperando syncs.
            string espejo = Path.Combine(_root, "Campo (OrbitX)");
            Directory.CreateDirectory(espejo);
            ResolutorLoteCloud.EscribirMarcador(espejo, "Campo", "sha-a");

            var r = ResolutorLoteCloud.NombresATombstonearTrasBorrar(_root, "Campo", null);

            Assert.That(r, Is.Empty);
        }

        [Test]
        public void NombresATombstonear_HermanoEspejoConOtroCloud_NoLoBloquea()
        {
            // El hermano reclama OTRO nombre cloud: no interfiere.
            string espejo = Path.Combine(_root, "Otro (OrbitX)");
            Directory.CreateDirectory(espejo);
            ResolutorLoteCloud.EscribirMarcador(espejo, "Otro", "sha-a");

            var r = ResolutorLoteCloud.NombresATombstonearTrasBorrar(_root, "Campo", null);

            Assert.That(r, Is.EquivalentTo(new[] { "Campo" }));
        }

        [Test]
        public void NombresATombstonear_EraEspejoYHermanoVivoReclamaEseMismoCloud_NoEncolaElCloud()
        {
            // Caso exacto del arreglo 2 (residuo simétrico del arreglo 2 de
            // más arriba, hallazgo 2026-09-16): la carpeta borrada era ella
            // misma un espejo ("X (OrbitX)" con lote_cloud="X"), y además
            // sigue vivo un hermano ("X (OrbitX 2)") que TAMBIÉN reclama
            // lote_cloud="X". Antes del arreglo, la guarda
            // ExisteHermanoQueReclamaEseNombreCloud sólo se aplicaba a la
            // rama de "nombreCarpetaBorrada", no a la de
            // "loteCloudDelPropioMarcador": "X" se encolaba igual y el
            // hermano vivo quedaba congelado esperando un sync que el
            // tombstone bloqueaba — el mismo bug que el arreglo 2 original
            // vino a cerrar, pero en la otra rama. Con la guarda aplicada a
            // las dos ramas, "X" no se encola (el hermano lo sigue
            // necesitando); el nombre propio de la carpeta borrada
            // ("X (OrbitX)") no lo reclama nadie más, así que ESE sí se
            // encola.
            string hermano = Path.Combine(_root, "X (OrbitX 2)");
            Directory.CreateDirectory(hermano);
            ResolutorLoteCloud.EscribirMarcador(hermano, "X", "sha-a");

            var r = ResolutorLoteCloud.NombresATombstonearTrasBorrar(_root, "X (OrbitX)", "X");

            Assert.That(r, Is.EquivalentTo(new[] { "X (OrbitX)" }));
        }
    }
}
