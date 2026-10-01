// ============================================================================
// TiposBanderaTests.cs — el catalogo de puntos de interes (tipo de bandera).
//
// Lo que se protege aca:
//  · las banderas VIEJAS (sin tipo) siguen siendo banderas comunes: tipo vacio,
//    null o un codigo que no conocemos caen en "Otro", nunca en una excepcion;
//  · "Otro" se guarda como vacio, asi un lote con banderas comunes sigue
//    escribiendo el Flags.txt byte a byte igual que antes;
//  · el codigo que viaja al archivo es estable y no rompe el CSV (sin comas ni
//    espacios): los nombres de pantalla se pueden cambiar, los codigos no.
// ============================================================================

using System.Linq;
using AgroParallel.Cabina;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class TiposBanderaTests
    {
        [Test]
        public void Catalogo_TieneLosSieteTiposEnOrdenDeBotones()
        {
            var codigos = TiposBandera.Todos.Select(t => t.Codigo).ToArray();
            Assert.That(codigos, Is.EqualTo(new[]
            {
                TiposBandera.Arbol, TiposBandera.Molino, TiposBandera.Agua,
                TiposBandera.Tanque, TiposBandera.Casa, TiposBandera.Piedra,
                TiposBandera.Otro,
            }));
        }

        [Test]
        public void Codigos_SonEstablesYSegurosParaElCsv()
        {
            Assert.That(TiposBandera.Arbol, Is.EqualTo("arbol"));
            Assert.That(TiposBandera.Molino, Is.EqualTo("molino"));
            Assert.That(TiposBandera.Agua, Is.EqualTo("agua"));
            Assert.That(TiposBandera.Tanque, Is.EqualTo("tanque"));
            Assert.That(TiposBandera.Casa, Is.EqualTo("casa"));
            Assert.That(TiposBandera.Piedra, Is.EqualTo("piedra"));
            Assert.That(TiposBandera.Otro, Is.EqualTo("otro"));

            foreach (var t in TiposBandera.Todos)
            {
                Assert.That(t.Codigo, Does.Match("^[a-z]+$"), t.Codigo);
                Assert.That(t.Nombre, Is.Not.Empty, t.Codigo);
                Assert.That(t.ColorHex, Does.Match("^#[0-9A-F]{6}$"), t.Codigo);
                Assert.That(t.ColorLegado, Is.InRange(0, 2), t.Codigo);
            }
        }

        [Test]
        public void NombresDePantalla_EnCastellano()
        {
            Assert.That(TiposBandera.De("arbol").Nombre, Is.EqualTo("Árbol / monte"));
            Assert.That(TiposBandera.De("molino").Nombre, Is.EqualTo("Molino / estructura"));
            Assert.That(TiposBandera.De("agua").Nombre, Is.EqualTo("Laguna / bañado / canal"));
            Assert.That(TiposBandera.De("tanque").Nombre, Is.EqualTo("Tanque de agua"));
            Assert.That(TiposBandera.De("casa").Nombre, Is.EqualTo("Casa / galpón"));
            Assert.That(TiposBandera.De("piedra").Nombre, Is.EqualTo("Piedra / obstáculo"));
            Assert.That(TiposBandera.De("otro").Nombre, Is.EqualTo("Otro"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("ovni")]
        [TestCase("otro")]
        public void De_SinTipoODesconocido_EsOtro(string codigo)
        {
            var t = TiposBandera.De(codigo);
            Assert.That(t.Codigo, Is.EqualTo(TiposBandera.Otro));
            Assert.That(TiposBandera.EsComun(codigo), Is.True);
        }

        [TestCase("ARBOL", "arbol")]
        [TestCase(" Piedra ", "piedra")]
        [TestCase("agua", "agua")]
        public void Normalizar_AceptaMayusculasYEspacios(string entrada, string esperado)
        {
            Assert.That(TiposBandera.Normalizar(entrada), Is.EqualTo(esperado));
            Assert.That(TiposBandera.EsComun(entrada), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("otro")]
        [TestCase("OTRO")]
        [TestCase("tipo,raro")]
        [TestCase("ovni")]
        public void Normalizar_OtroYDesconocidoSeGuardanVacios(string entrada)
        {
            // Vacio = bandera comun: el archivo queda igual que con la version
            // anterior y un lector viejo no ve nada distinto.
            Assert.That(TiposBandera.Normalizar(entrada), Is.EqualTo(""));
        }

        [Test]
        public void ColorLegado_LoQueVeUnLectorQueNoConoceTipos()
        {
            // Un PilotX viejo (o AOG) solo dibuja rojo/verde/amarillo: que al
            // menos el arbol salga verde y la piedra roja.
            Assert.That(TiposBandera.De("arbol").ColorLegado, Is.EqualTo(1));
            Assert.That(TiposBandera.De("piedra").ColorLegado, Is.EqualTo(0));
            Assert.That(TiposBandera.De("molino").ColorLegado, Is.EqualTo(2));
        }

        [Test]
        public void Letras_UnaPorTipoYDistintas_OtroSinLetra()
        {
            var conLetra = TiposBandera.Todos.Where(t => t.Codigo != TiposBandera.Otro).ToList();
            foreach (var t in conLetra)
                Assert.That(t.Letra, Has.Length.EqualTo(1), t.Codigo);
            Assert.That(conLetra.Select(t => t.Letra).Distinct().Count(), Is.EqualTo(conLetra.Count));
            Assert.That(TiposBandera.De("otro").Letra, Is.EqualTo(""));
        }

        [Test]
        public void TiposConIcono_TienenColorDistintoEntreSi()
        {
            var colores = TiposBandera.Todos
                .Where(t => t.Codigo != TiposBandera.Otro)
                .Select(t => t.ColorHex)
                .ToList();
            Assert.That(colores.Distinct().Count(), Is.EqualTo(colores.Count));
        }
    }
}
