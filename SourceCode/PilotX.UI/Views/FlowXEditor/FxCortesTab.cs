// ============================================================================
// FxCortesTab.cs — pantalla "Cortes y secciones".
//
// QUE QUEDO NATIVO: la pestana 3 de pages/flowx.html — cantidad de cortes,
// asignacion automatica contra las secciones de PilotX, el mapa resultante y
// la valvula master.
// QUE SIGUE EN HTML: la misma pestana en pages/flowx.html, para la PWA.
//
// Un corte = una valvula fisica de la barra (una salida del PCA9685). Se
// persiste como pares {cable, seccion_aog} (formato heredado) para no tocar el
// bridge: cuando un corte agrupa varias secciones hay una entrada por seccion
// con el mismo cable, y el bridge hace OR sobre bits[cable-1].
//
// OJO: sin estado de PilotX (num_sections = 0) NO se re-asigna nada — pisar
// cables[] con una lista vacia dejaria la barra sin cortes.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.Controls;

namespace PilotX.Desktop.Views.FlowXEditor;

public sealed class FxCortesTab : FxTab
{
    public FxCortesTab(FxCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();

        var n = C.NodoActual();
        if (n == null)
        {
            Children.Add(FxUi.Sub("Elegí un nodo arriba para configurar sus cortes."));
            return;
        }

        Children.Add(FxUi.Sub("Un corte es una válvula física de la barra. Decí cuántos tenés y PilotX "
                            + "reparte sus secciones entre ellos. Si hay más secciones que cortes, "
                            + "varias secciones comparten un corte."));

        int nCortes = FxCtx.InferNumCortes(n);
        int nSec = C.NumSecAog();

        var g = FxUi.Grilla();

        var stCortes = new AgpStepper(nCortes, AgpStepperModo.Int, 1, 1, FxCtx.MaxCortes);
        stCortes.ValorCambiado += v => CambiarCortes(n, (int)Math.Round(v));
        g.Children.Add(FxUi.Campo("Cantidad de cortes", stCortes, 170));

        g.Children.Add(FxUi.Campo("Secciones de PilotX",
            FxUi.MonoTexto(nSec > 0 ? nSec.ToString(CultureInfo.InvariantCulture)
                               : "— (sin estado de PilotX)"), 170));

        g.Children.Add(FxUi.Campo(" ",
            FxUi.Boton("Asignar automáticamente", () => _ = AsignarAsync(n, stCortes.ValorInt),
                       primario: true), 200));
        Children.Add(g);

        Children.Add(FxUi.Bloque("Mapa de cortes", FxUi.Sub(MapaTexto(n))));

        // Master: -1 salida dedicada, 0 sin master, 1..N ese corte.
        var ops = new List<(int, string)>
        {
            (-1, "Salida dedicada (firmware)"),
            (0,  "Sin master"),
        };
        for (int i = 1; i <= nCortes; i++)
            ops.Add((i, "Corte " + i.ToString(CultureInfo.InvariantCulture)));

        var master = new StackPanel { Spacing = 8 };
        master.Children.Add(FxUi.ComboOpciones(ops, n.MasterCable, v => n.MasterCable = v, 230));
        master.Children.Add(FxUi.Sub("Salida dedicada: la maneja el firmware con su propia salida — "
                                   + "abre si hay cualquier sección abierta. Sin master: no se usa "
                                   + "válvula general. Corte N: ese corte hace de master y no debería "
                                   + "estar asignado a una sección."));
        Children.Add(FxUi.Bloque("Válvula master", master));
    }

    private string MapaTexto(FlowXNodoConfig n)
    {
        var cables = n.Cables;
        if (cables == null || cables.Count == 0)
            return "Sin asignación. Tocá \"Asignar automáticamente\" después de cargar la cantidad de cortes.";

        var porCable = new SortedDictionary<int, List<int>>();
        foreach (var c in cables)
        {
            if (c == null || c.Cable < 1 || c.SeccionAog < 1) continue;
            if (!porCable.TryGetValue(c.Cable, out var l)) { l = new List<int>(); porCable[c.Cable] = l; }
            l.Add(c.SeccionAog);
        }
        if (porCable.Count == 0) return "Sin asignación válida.";

        var partes = new List<string>();
        foreach (var kv in porCable)
        {
            kv.Value.Sort();
            string secs = kv.Value.Count == 1
                ? PilotX.Cockpit.Bars.Traductor.T("sección") + " " + kv.Value[0]
                : PilotX.Cockpit.Bars.Traductor.T("secciones") + " " + string.Join(", ", kv.Value);
            partes.Add(PilotX.Cockpit.Bars.Traductor.T("Corte") + " " + kv.Key + " → " + secs);
        }
        return string.Join("  ·  ", partes);
    }

    /// <summary>Cambio de cantidad de cortes: re-asigna contra las secciones de
    /// PilotX (si se conocen), conserva la master elegida y redibuja. Sin
    /// estado de PilotX solo se redibuja — cables[] queda como estaba.</summary>
    private void CambiarCortes(FlowXNodoConfig n, int nCortes)
    {
        if (nCortes < 1) nCortes = 1;
        if (nCortes > FxCtx.MaxCortes) nCortes = FxCtx.MaxCortes;
        int nSec = C.NumSecAog();
        if (nSec > 0) n.Cables = FxCtx.AutoAsignarCortes(nCortes, nSec);
        if (n.MasterCable > nCortes) n.MasterCable = -1;
        C.RebuildTab?.Invoke();
    }

    private async Task AsignarAsync(FlowXNodoConfig n, int nCortes)
    {
        if (C.Alertar == null) return;
        int nSec = C.NumSecAog();
        if (nSec <= 0)
        {
            await C.Alertar("Falta estado de PilotX",
                "Todavía no hay información de cuántas secciones tiene el implemento. "
              + "Abrí el implemento y volvé a esta pantalla.").ConfigureAwait(true);
            return;
        }
        if (nCortes > FxCtx.MaxCortes)
        {
            await C.Alertar("Demasiados cortes",
                "El nodo FlowX maneja hasta 16 cortes (salidas del PCA9685).").ConfigureAwait(true);
            return;
        }
        n.Cables = FxCtx.AutoAsignarCortes(nCortes, nSec);
        C.RebuildTab?.Invoke();
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
            "Cortes asignados. Tocá Guardar para que quede."), "ok");
    }
}
