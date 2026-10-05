// El merge existe por un bug concreto: el Hub manda solo los tres flags de
// overlays, el POST reemplazaba el objeto entero, y las posiciones de los
// widgets volvian a -1. El operario acomodaba el widget, tocaba un toggle y
// lo perdia. Estos tests fijan la regla: ausente = no tocar.

using System.Text.Json.Serialization;
using AgroParallel.Services;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class AgpJsonMergeTests
    {
        private sealed class Prefs
        {
            [JsonPropertyName("qx_overlay")] public bool QxOverlay { get; set; } = true;
            [JsonPropertyName("vx_overlay")] public bool VxOverlay { get; set; } = true;
            [JsonPropertyName("qx_x")] public int QxX { get; set; } = -1;
            [JsonPropertyName("qx_y")] public int QxY { get; set; } = -1;
            [JsonPropertyName("nombre")] public string Nombre { get; set; } = "sin nombre";
        }

        [Test]
        public void Un_campo_ausente_no_se_toca()
        {
            // El caso real: el Hub manda los flags y nada mas.
            var p = new Prefs { QxX = 640, QxY = 300 };
            AgpJsonMerge.Apply(p, "{\"qx_overlay\":false}");

            Assert.That(p.QxOverlay, Is.False, "el flag que vino si se aplica");
            Assert.That(p.QxX, Is.EqualTo(640), "la posicion que NO vino sobrevive");
            Assert.That(p.QxY, Is.EqualTo(300));
        }

        [Test]
        public void Un_campo_presente_en_su_valor_por_defecto_SI_se_aplica()
        {
            // Es la diferencia con un Deserialize plano: false explicito es una
            // decision del cliente, no "no vino nada".
            var p = new Prefs { QxOverlay = true, QxX = 640 };
            AgpJsonMerge.Apply(p, "{\"qx_overlay\":false,\"qx_x\":-1}");

            Assert.That(p.QxOverlay, Is.False);
            Assert.That(p.QxX, Is.EqualTo(-1), "resetear la posicion a -1 tiene que poder hacerse");
        }

        [Test]
        public void Null_significa_no_tocar()
        {
            var p = new Prefs { Nombre = "Tolva 1" };
            AgpJsonMerge.Apply(p, "{\"nombre\":null}");
            Assert.That(p.Nombre, Is.EqualTo("Tolva 1"));
        }

        [Test]
        public void Devuelve_cuantos_campos_aplico()
        {
            var p = new Prefs();
            Assert.That(AgpJsonMerge.Apply(p, "{\"qx_overlay\":false,\"qx_x\":10}"), Is.EqualTo(2));
            Assert.That(AgpJsonMerge.Apply(p, "{\"campo_que_no_existe\":1}"), Is.Zero,
                "cero permite responder 'no habia nada que aplicar' en vez de guardar defaults");
        }

        [Test]
        public void Un_campo_con_tipo_equivocado_no_arrastra_al_resto()
        {
            // Un body medio malo no puede tirar abajo una config entera.
            var p = new Prefs { QxX = 640 };
            AgpJsonMerge.Apply(p, "{\"qx_x\":\"ochenta\",\"qx_overlay\":false}");

            Assert.That(p.QxX, Is.EqualTo(640), "el campo malo se ignora");
            Assert.That(p.QxOverlay, Is.False, "el campo bueno se aplica igual");
        }

        [Test]
        public void Body_vacio_o_invalido_no_rompe()
        {
            var p = new Prefs { QxX = 640 };
            Assert.That(AgpJsonMerge.Apply(p, "{}"), Is.Zero);
            Assert.That(AgpJsonMerge.Apply(p, ""), Is.Zero);
            Assert.That(AgpJsonMerge.Apply(p, null), Is.Zero);
            Assert.That(p.QxX, Is.EqualTo(640));
        }
    }
}
