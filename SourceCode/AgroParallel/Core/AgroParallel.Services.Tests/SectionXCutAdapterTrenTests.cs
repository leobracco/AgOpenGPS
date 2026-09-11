// Distancia de tren derivada del implemento (Task 4). El adapter viejo
// resolvía "delantero/trasero" solo con cable.Tren + nodo.DistanciaEntreTrenes
// (un config por nodo, editable en dos lugares distintos del implemento real).
// Ahora, si hay un ImplementoProvider con trenes útiles para el surco del
// cable, esa distancia manda — el campo cable.Tren queda obsoleto y se ignora.
// Sin implemento (o sin trenes derivables), el comportamiento tiene que ser
// bit a bit idéntico al de antes: por eso el segundo test no es solo "no
// explota", sino que reproduce el cálculo viejo a mano y lo compara.
//
// PositionHistory no tiene seam propio: se alimenta con Record() real usando
// posiciones sintéticas (mismo approach que SiembraStateMachineTests con
// snapshots fabricados a mano) para que GetSectionsAtDistanceBack devuelva un
// patrón distinto al estado actual y así distinguir "delantero" de "atrasado".

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AgroParallel.Common;
using AgroParallel.Cut;
using AgroParallel.Models;
using AgroParallel.SectionX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class SectionXCutAdapterTrenTests
    {
        // secB (índice 1 = false) es el estado ACTUAL (lo que ve snap.SectionOnRequest).
        // secA (índice 1 = true) es el estado de 2.5m atrás. Cualquier cable que
        // termine leyendo secA en vez de secB prueba que se usó el patrón atrasado.
        private static readonly bool[] SecA = { false, true };
        private static readonly bool[] SecB = { false, false };

        /// <summary>Historial con un cambio de patrón a mitad de camino: a 5m
        /// recorridos, "2.5m atrás" cae exactamente sobre el tramo con SecA.</summary>
        private static PositionHistory HistConCambioA2_5m()
        {
            var hist = new PositionHistory();
            hist.Record(0, 0, SecA); // dist acumulada 0
            hist.Record(1, 0, SecA); // 1
            hist.Record(2, 0, SecA); // 2
            hist.Record(3, 0, SecB); // 3
            hist.Record(4, 0, SecB); // 4
            hist.Record(5, 0, SecB); // 5 (estado "actual")
            return hist;
        }

        private static AogStateSnapshot SnapCon(bool[] secciones)
        {
            return new AogStateSnapshot { SectionOnRequest = secciones };
        }

        private static ImplementoDto ImplDosTrenes()
        {
            var impl = new ImplementoDto();
            impl.Trenes.Add(new TrenDto { Id = 1, Nombre = "Delantero", DistanciaM = 0 });
            impl.Trenes.Add(new TrenDto { Id = 2, Nombre = "Trasero", DistanciaM = 2.5 });
            impl.Surcos.Add(new SurcoDto { Numero = 2, TrenId = 2, SeccionPilotX = 2 });
            return impl;
        }

        private static SectionXConfig ConfigUnCableUnNodo(int cableTrenViejo, double distanciaEntreTrenesNodo)
        {
            var cfg = new SectionXConfig();
            var nodo = new SxNodoConfig
            {
                Uid = "TEST01",
                Habilitado = true,
                DistanciaEntreTrenes = distanciaEntreTrenesNodo,
            };
            nodo.Cables.Add(new SxCableMap { Cable = 1, SeccionAOG = 2, Tren = cableTrenViejo });
            cfg.Nodos.Add(nodo);
            return cfg;
        }

        private static SectionXConfig ConfigCableSeccion(int seccionAOG, int cableTrenViejo, double distanciaEntreTrenesNodo)
        {
            var cfg = new SectionXConfig();
            var nodo = new SxNodoConfig
            {
                Uid = "TEST01",
                Habilitado = true,
                DistanciaEntreTrenes = distanciaEntreTrenesNodo,
            };
            nodo.Cables.Add(new SxCableMap { Cable = 1, SeccionAOG = seccionAOG, Tren = cableTrenViejo });
            cfg.Nodos.Add(nodo);
            return cfg;
        }

        // secIdx = SeccionAOG(7) - 1 = 6. Solo ese índice difiere entre "actual" y
        // "2.5m atrás" (arrays de ancho 8 porque SeccionAOG=7 no entra en SecA/SecB).
        private static readonly bool[] SecA7 = { false, false, false, false, false, false, true, false };
        private static readonly bool[] SecB7 = { false, false, false, false, false, false, false, false };

        private static PositionHistory HistConCambioA2_5m_Ancho8()
        {
            var hist = new PositionHistory();
            hist.Record(0, 0, SecA7);
            hist.Record(1, 0, SecA7);
            hist.Record(2, 0, SecA7);
            hist.Record(3, 0, SecB7);
            hist.Record(4, 0, SecB7);
            hist.Record(5, 0, SecB7);
            return hist;
        }

        /// <summary>Migración VistaX típica: 3 surcos (19,20,21) comparten una sola
        /// sección PilotX (7). Ningún Numero coincide con la sección — a propósito,
        /// para que un bug que confunda "surco" con "sección" no pueda acertar
        /// por casualidad numérica.</summary>
        private static ImplementoDto ImplTresSurcosUnaSeccion()
        {
            var impl = new ImplementoDto();
            impl.Trenes.Add(new TrenDto { Id = 1, Nombre = "Delantero", DistanciaM = 0 });
            impl.Trenes.Add(new TrenDto { Id = 2, Nombre = "Trasero", DistanciaM = 2.5 });
            impl.Surcos.Add(new SurcoDto { Numero = 19, TrenId = 2, SeccionPilotX = 7 });
            impl.Surcos.Add(new SurcoDto { Numero = 20, TrenId = 2, SeccionPilotX = 7 });
            impl.Surcos.Add(new SurcoDto { Numero = 21, TrenId = 2, SeccionPilotX = 7 });
            return impl;
        }

        /// <summary>Una sección (3) con dos surcos que quedaron mal cableados en
        /// trenes distintos (error de configuración real posible).</summary>
        private static ImplementoDto ImplSeccionConDosTrenes()
        {
            var impl = new ImplementoDto();
            impl.Trenes.Add(new TrenDto { Id = 1, Nombre = "Delantero", DistanciaM = 0 });
            impl.Trenes.Add(new TrenDto { Id = 2, Nombre = "Trasero", DistanciaM = 2.5 });
            impl.Surcos.Add(new SurcoDto { Numero = 5, TrenId = 1, SeccionPilotX = 3 });
            impl.Surcos.Add(new SurcoDto { Numero = 6, TrenId = 2, SeccionPilotX = 3 });
            return impl;
        }

        /// <summary>Listener descartable para contar cuántas veces se logueó un WARN
        /// con cierto texto — sin esto no hay forma de verificar "once" desde el test,
        /// AgpLog solo escribe a Trace.</summary>
        private sealed class ContadorDeLog : TraceListener
        {
            public readonly List<string> Lineas = new List<string>();
            public override void Write(string message) { }
            public override void WriteLine(string message) { Lineas.Add(message); }
        }

        [Test]
        public void ConImplementoDosTrenes_CableDeSurcoTrasero_UsaSeccionesRetrasadas()
        {
            // cable.Tren=0 (viejo: "delantero", usaría SecB tal cual) pero el
            // implemento dice que el surco 2 es del tren trasero (2.5m). El
            // implemento tiene que ganarle al campo viejo del cable.
            var cfg = ConfigUnCableUnNodo(cableTrenViejo: 0, distanciaEntreTrenesNodo: 0);
            var adapter = new SectionXCutAdapter(cfg) { ImplementoProvider = ImplDosTrenes };
            var hist = HistConCambioA2_5m();
            var snap = SnapCon(SecB);

            var cmds = new List<CutCommand>(adapter.ComputePublishes(snap, hist));

            Assert.That(cmds, Has.Count.EqualTo(1));
            // Bits[0] = cable 1 = seccion 2 de PilotX (indice 1). Si hubiera ganado el
            // comportamiento viejo (cable.Tren=0 => SecB) esto daria 0.
            Assert.That(cmds[0].Bits[0], Is.EqualTo(1),
                "el implemento (tren trasero, 2.5m) tiene que ganarle al cable.Tren=0 viejo");
        }

        [Test]
        public void SinTrenesEnImplemento_UsaFallbackPorNodo()
        {
            // ImplementoProvider devuelve un implemento SIN trenes útiles (default,
            // 0 o 1 tren) => TrenResolver.Resolver da null => cae al fallback viejo:
            // cable.Tren=0 => distancia 0 => SecAOG (SecB) tal cual, sin desfasar.
            var cfg = ConfigUnCableUnNodo(cableTrenViejo: 0, distanciaEntreTrenesNodo: 2.5);
            var adapter = new SectionXCutAdapter(cfg) { ImplementoProvider = () => new ImplementoDto() };
            var hist = HistConCambioA2_5m();
            var snap = SnapCon(SecB);

            var cmds = new List<CutCommand>(adapter.ComputePublishes(snap, hist));

            Assert.That(cmds, Has.Count.EqualTo(1));
            Assert.That(cmds[0].Bits[0], Is.EqualTo(0),
                "sin trenes derivables, cable.Tren=0 tiene que seguir leyendo SecAOG tal cual (fallback viejo)");
        }

        [Test]
        public void SinImplementoProvider_ComportamientoIdenticoAlFallback()
        {
            // Sin provider seteado (el caso real hoy, antes de que el caller lo
            // conecte): debe ser bit a bit igual al comportamiento pre-task.
            // cable.Tren=1 (trasero) + nodo.DistanciaEntreTrenes=2.5 => usa SecA.
            var cfg = ConfigUnCableUnNodo(cableTrenViejo: 1, distanciaEntreTrenesNodo: 2.5);
            var adapter = new SectionXCutAdapter(cfg); // ImplementoProvider queda null
            var hist = HistConCambioA2_5m();
            var snap = SnapCon(SecB);

            var cmds = new List<CutCommand>(adapter.ComputePublishes(snap, hist));

            Assert.That(cmds, Has.Count.EqualTo(1));
            Assert.That(cmds[0].Bits[0], Is.EqualTo(1),
                "sin provider, cable.Tren=1 + DistanciaEntreTrenes del nodo tiene que seguir andando como antes");
        }

        [Test]
        public void SeccionConVariosSurcos_NumeroDistintoDeSeccionPilotX_UsaTrenDeLosSurcosMapeados()
        {
            // Bug de review: el adapter pasaba SeccionAOG (número de SECCIÓN) donde
            // TrenResolver espera números de SURCO. Acá la sección 7 cubre los
            // surcos 19/20/21 (migración VistaX típica) — ninguno coincide con "7",
            // así que un resolver que confunda ambos espacios no encuentra nada y
            // cae al fallback viejo (cable.Tren=0 => sin retraso) en vez de usar el
            // tren trasero (2.5m) que el implemento realmente asigna a esos surcos.
            var cfg = ConfigCableSeccion(seccionAOG: 7, cableTrenViejo: 0, distanciaEntreTrenesNodo: 2.5);
            var adapter = new SectionXCutAdapter(cfg) { ImplementoProvider = ImplTresSurcosUnaSeccion };
            var hist = HistConCambioA2_5m_Ancho8();
            var snap = SnapCon(SecB7);

            var cmds = new List<CutCommand>(adapter.ComputePublishes(snap, hist));

            Assert.That(cmds, Has.Count.EqualTo(1));
            Assert.That(cmds[0].Bits[0], Is.EqualTo(1),
                "la sección 7 mapea a los surcos 19/20/21 (tren trasero, 2.5m): tiene que usar el patrón retrasado, " +
                "no el estado actual (eso pasaría si el resolver confundiera SeccionAOG con número de surco)");
        }

        [Test]
        public void SeccionCruzaDosTrenes_UsaTrenDelPrimerSurco_NoExplotaYElWarnEsUnaVezPorArranque()
        {
            // La sección 3 tiene dos surcos mal cableados en trenes distintos
            // (delantero y trasero). Con el mapeo sección->surcos arreglado esto es
            // alcanzable en producción (antes del fix, nunca se llegaba a pedir más
            // de un surco por sección) — por eso hace falta el flag "once" en el
            // warn, igual que los Info de arriba.
            var cfg = ConfigCableSeccion(seccionAOG: 3, cableTrenViejo: 0, distanciaEntreTrenesNodo: 2.5);
            var adapter = new SectionXCutAdapter(cfg) { ImplementoProvider = ImplSeccionConDosTrenes };
            var secAOG3 = new[] { false, false, true };
            var snap = SnapCon(secAOG3);

            var listener = new ContadorDeLog();
            Trace.Listeners.Add(listener);
            List<CutCommand> cmds1;
            List<CutCommand> cmds2;
            try
            {
                // Dos ticks (no explota en ninguno) para poder verificar que el
                // warn no se repite en el segundo.
                cmds1 = new List<CutCommand>(adapter.ComputePublishes(snap, null));
                cmds2 = new List<CutCommand>(adapter.ComputePublishes(snap, null));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }

            // Conflicto: se usa el tren del primer surco pedido (Numero=5, delantero,
            // 0m) => sin retraso => bits[0] == estado actual (secAOG3[2] == true).
            Assert.That(cmds1[0].Bits[0], Is.EqualTo(1),
                "en conflicto, tiene que ganar el tren del PRIMER surco (delantero, sin retraso)");
            Assert.That(cmds2[0].Bits[0], Is.EqualTo(1));

            int warns = listener.Lineas.Count(l => l.Contains("trenes distintos"));
            Assert.That(warns, Is.EqualTo(1),
                "el warn de conflicto tiene que loguearse una sola vez por arranque, no en cada tick (spam a tick rate)");
        }
    }
}
