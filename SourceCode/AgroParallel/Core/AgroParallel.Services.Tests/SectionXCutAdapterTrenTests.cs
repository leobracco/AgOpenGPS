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
            // Bits[0] = cable 1 = seccion AOG 2 (indice 1). Si hubiera ganado el
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
    }
}
