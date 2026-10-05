// ============================================================================
// KbBotSoporteTests.cs — guardian de la base de conocimiento del bot de soporte.
//
// El bot del cloud (OrbitX services/soporte-bot.js) le contesta al operario con
// lo que dice docs/bot/kb-pilotx.md. Esa KB se escribe A MANO, y hasta el
// 2026-09-19 vivia SOLO en el droplet: sin historia, sin respaldo, y sin nada
// que avisara cuando quedaba vieja. El resultado medido ese dia: el bot no
// sabia que existia la dosificacion — "dosis" aparecia 0 veces — ni el grafico
// de PID, ni el chat de soporte. A un operario que pregunte por eso le contesta
// cualquier cosa, o peor, se lo inventa con seguridad.
//
// Este test es la conexion entre el repo y el bot: cuando se agrega algo que el
// operario ve y nadie lo escribio en la KB, el build lo canta. NO valida que lo
// escrito sea correcto (eso no lo puede saber un test) — valida que ALGUIEN lo
// haya escrito.
//
// Si un test de aca falla, la respuesta NO es sacar el termino de la lista: es
// escribir esa parte de la KB. La lista es corta y curada a proposito; sumar
// algo aca es decir "el bot tiene que saber de esto".
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class KbBotSoporteTests
    {
        /// <summary>Lo que el bot TIENE que conocer, con los terminos que lo
        /// delatan. Cada entrada es "de que tema" -> "palabras que aparecerian si
        /// estuviera escrito".</summary>
        private static readonly Dictionary<string, string[]> TemasQueElBotDebeSaber =
            new Dictionary<string, string[]>
            {
                // Guiado y direccion: el nucleo, ya cubierto.
                { "Piloto / autoguiado",   new[] { "piloto", "autosteer" } },
                { "Lotes",                 new[] { "lote" } },
                { "Guias / pasadas",       new[] { "guia", "pasada" } },
                { "Codigos de error",      new[] { "AGP-" } },

                // Dosificacion: el agujero encontrado el 2026-09-19.
                { "QuantiX / dosificacion", new[] { "quantix" } },
                { "Dosis",                  new[] { "dosis" } },
                { "Calibracion de motores", new[] { "calibra" } },

                // Los demas productos X-*: el operario los ve en la pantalla.
                { "VistaX",  new[] { "vistax" } },
                { "FlowX",   new[] { "flowx" } },
                { "SectionX / secciones", new[] { "seccion" } },

                // Soporte: si el bot no sabe como se le habla, mal empezamos.
                { "Chat de soporte", new[] { "soporte" } },
            };

        /// <summary>Sube desde el binario hasta encontrar la raiz del repo.</summary>
        private static string RaizRepo()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "docs", "bot"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static string LeerKb()
        {
            string raiz = RaizRepo();
            if (raiz == null) return null;
            string p = Path.Combine(raiz, "docs", "bot", "kb-pilotx.md");
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        [Test]
        public void La_kb_del_bot_esta_versionada_en_el_repo()
        {
            // Vivia solo en el droplet: sin respaldo y sin historia de cambios.
            string kb = LeerKb();
            Assert.That(kb, Is.Not.Null,
                "Falta docs/bot/kb-pilotx.md. Es lo que el bot de soporte le contesta al operario: " +
                "tiene que estar versionado, no solo en el servidor.");
            Assert.That(kb.Length, Is.GreaterThan(5000), "La KB quedo sospechosamente corta.");
        }

        [Test]
        public void El_bot_conoce_todo_lo_que_el_operario_ve()
        {
            string kb = LeerKb();
            if (kb == null) Assert.Ignore("Sin la KB en el repo este test no aplica (ver el test de arriba).");

            string kbMin = kb.ToLowerInvariant();
            var faltan = new List<string>();

            foreach (var tema in TemasQueElBotDebeSaber)
            {
                bool cubierto = false;
                foreach (string termino in tema.Value)
                {
                    if (kbMin.Contains(termino.ToLowerInvariant())) { cubierto = true; break; }
                }
                if (!cubierto) faltan.Add(tema.Key);
            }

            Assert.That(faltan, Is.Empty,
                "El bot de soporte no sabe nada de: " + string.Join(", ", faltan.ToArray()) +
                ". Escribi esa parte en docs/bot/kb-pilotx.md — si no, a un operario que pregunte " +
                "le va a contestar cualquier cosa. NO saques el tema de la lista para que pase el test.");
        }

        [Test]
        public void La_kb_no_promete_acciones_remotas_que_estan_apagadas()
        {
            // Las acciones que mueven la maquina salieron del catalogo remoto el
            // 2026-09-19 (no habia confirmacion del operario). Si la KB le dice al
            // bot que puede moverlas, el bot se lo va a ofrecer al operario y no
            // va a pasar nada: peor que no ofrecerlo.
            string kb = LeerKb();
            if (kb == null) Assert.Ignore("Sin la KB en el repo este test no aplica.");

            string kbMin = kb.ToLowerInvariant();
            Assert.That(kbMin.Contains("flowx_pwm"), Is.False,
                "La KB menciona flowx_pwm, que ya no esta en el catalogo remoto salvo habilitacion local.");
            Assert.That(kbMin.Contains("secciones_manual"), Is.False,
                "La KB menciona secciones_manual, que ya no esta en el catalogo remoto.");
        }
    }
}
