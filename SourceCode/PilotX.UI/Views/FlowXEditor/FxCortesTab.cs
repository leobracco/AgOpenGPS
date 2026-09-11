// ============================================================================
// FxCortesTab.cs — pantalla "Cortes y secciones".
//
// QUE QUEDO NATIVO: la pestana 3 de pages/flowx.html — cantidad de cortes,
// asignacion automatica contra las secciones de PilotX, ASIGNACION MANUAL
// seccion por seccion, el mapa resultante y la valvula master.
// QUE SIGUE EN HTML: la misma pestana en pages/flowx.html, para la PWA (solo
// tiene el reparto automatico).
//
// Un corte = una valvula fisica de la barra (una salida del nodo: S1, S2...).
// Se persiste como pares {cable, seccion_aog} (formato heredado) para no tocar
// el bridge: cuando un corte agrupa varias secciones hay una entrada por
// seccion con el mismo cable, y el bridge hace OR sobre bits[cable-1].
//
// NADA DE CANTIDADES FIJAS (pedido explicito del usuario, 2026-09-05): la
// pantalla se arma con las secciones que reporte PilotX en ese momento
// (C.NumSecAog(), hoy hasta 64 en el motor) y con los cortes que el operario
// declare. El unico techo es el del protocolo del nodo: el bitmask de
// secciones que viaja al firmware es de 16 bits, asi que hay 16 cortes
// posibles (FxCtx.MaxCortes) — no es una decision de esta pantalla. Si la
// lista de secciones crece, la grilla envuelve y scrollea sola.
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

    /// <summary>Texto del mapa: se refresca en el lugar cuando el operario
    /// toca un combo, sin rebuild — reconstruir el arbol entero le cerraria el
    /// desplegable y le tiraria el scroll en una barra de muchas secciones.</summary>
    private TextBlock? _mapaTxt;

    public override void Rebuild()
    {
        Children.Clear();
        _mapaTxt = null;

        var n = C.NodoActual();
        if (n == null)
        {
            Children.Add(FxUi.Sub("Elegí un nodo arriba para configurar sus cortes."));
            return;
        }

        Children.Add(FxUi.Sub("Un corte es una válvula física de la barra (la salida S1, S2… del nodo). "
                            + "Decí cuántos tenés y asignalos: \"Asignar automáticamente\" los reparte "
                            + "en orden, y abajo podés corregir a mano qué corte abre cada sección. "
                            + "Varias secciones pueden compartir un corte."));

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

        _mapaTxt = FxUi.Sub(MapaTexto(n, nCortes));
        Children.Add(FxUi.Bloque("Mapa de cortes", _mapaTxt));

        Children.Add(FxUi.Bloque("Asignación manual", ArmarManual(n, nCortes, nSec)));

        // Master: -1 salida dedicada, 0 sin master, 1..N ese corte.
        var ops = new List<(int, string)>
        {
            (-1, "Salida dedicada (firmware)"),
            (0,  "Sin master"),
        };
        for (int i = 1; i <= nCortes; i++)
            ops.Add((i, "Corte " + i.ToString(CultureInfo.InvariantCulture)));

        var master = new StackPanel { Spacing = 8 };
        master.Children.Add(FxUi.ComboOpciones(ops, n.MasterCable, v =>
        {
            n.MasterCable = v;
            RefrescarMapa(n, nCortes);   // el mapa avisa si ese corte tiene secciones
        }, 230));
        master.Children.Add(FxUi.Sub("Salida dedicada: la maneja el firmware con su propia salida — "
                                   + "abre si hay cualquier sección abierta. Sin master: no se usa "
                                   + "válvula general. Corte N: ese corte hace de master y no debería "
                                   + "estar asignado a una sección."));
        Children.Add(FxUi.Bloque("Válvula master", master));
    }

    // ------------------------------------------------------------------
    //  Asignación manual — una fila por sección que reporte PilotX
    // ------------------------------------------------------------------

    /// <summary>
    /// Un combo por sección con el corte que la abre (o "Sin asignar"). La
    /// cantidad de filas sale de PilotX en vivo, no de ninguna constante: si el
    /// implemento pasa de 7 a 24 secciones, acá hay 24 combos. Con muchas
    /// secciones la grilla envuelve y el bloque scrollea, así la pantalla de
    /// cabina no se estira sin fin.
    /// </summary>
    private Control ArmarManual(FlowXNodoConfig n, int nCortes, int nSec)
    {
        var col = new StackPanel { Spacing = 8 };

        if (nSec <= 0)
        {
            col.Children.Add(FxUi.Sub("Todavía no hay estado de PilotX (no sé cuántas secciones tiene "
                                    + "el implemento). Abrí el implemento y volvé a esta pantalla."));
            return col;
        }

        col.Children.Add(FxUi.Sub("Elegí a mano qué corte abre cada sección. Dos secciones con el mismo "
                                + "corte quedan agrupadas en esa válvula. \"Sin asignar\" deja la "
                                + "sección sin válvula: no corta nada."));

        var grilla = FxUi.Grilla();
        for (int s = 1; s <= nSec; s++)
        {
            int seccion = s;                       // captura por iteración
            var opsSec = new List<(int, string)> { (0, "Sin asignar") };
            for (int i = 1; i <= nCortes; i++)
                opsSec.Add((i, "Corte " + i.ToString(CultureInfo.InvariantCulture)));

            var combo = FxUi.ComboOpciones(opsSec, CorteDeSeccion(n, seccion), v =>
            {
                AsignarSeccion(n, seccion, v);
                RefrescarMapa(n, nCortes);
            }, 150);

            grilla.Children.Add(FxUi.Campo(
                PilotX.Cockpit.Bars.Traductor.T("Sección") + " " + seccion.ToString(CultureInfo.InvariantCulture),
                combo, 150));
        }

        // Techo de alto en vez de tope de secciones: con pocas no se nota, con
        // muchas aparece el scroll y la pantalla sigue usable.
        col.Children.Add(new ScrollViewer
        {
            MaxHeight = 320,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = grilla,
        });

        return col;
    }

    /// <summary>Corte asignado a esa sección, 0 = ninguno. Si por un JSON
    /// editado a mano quedó en más de un corte, gana el más chico (el mapa lo
    /// muestra igual en los dos).</summary>
    private static int CorteDeSeccion(FlowXNodoConfig n, int seccion)
    {
        var cables = n.Cables;
        if (cables == null) return 0;
        int mejor = 0;
        foreach (var c in cables)
        {
            if (c == null || c.Cable < 1 || c.SeccionAog != seccion) continue;
            if (mejor == 0 || c.Cable < mejor) mejor = c.Cable;
        }
        return mejor;
    }

    /// <summary>Deja la sección colgando de UN corte (o de ninguno con 0).
    /// Limpia de paso las entradas nulas o inválidas que pudiera traer el JSON.</summary>
    private static void AsignarSeccion(FlowXNodoConfig n, int seccion, int corte)
    {
        n.Cables ??= new List<FlowXCableMap>();
        n.Cables.RemoveAll(c => c == null || c.Cable < 1 || c.SeccionAog < 1 || c.SeccionAog == seccion);
        if (corte > 0) n.Cables.Add(new FlowXCableMap { Cable = corte, SeccionAog = seccion });
    }

    private void RefrescarMapa(FlowXNodoConfig n, int nCortes)
    {
        if (_mapaTxt != null) _mapaTxt.Text = MapaTexto(n, nCortes);
    }

    // ------------------------------------------------------------------
    //  Mapa
    // ------------------------------------------------------------------

    private string MapaTexto(FlowXNodoConfig n, int nCortes)
    {
        var cables = n.Cables;
        if (cables == null || cables.Count == 0)
            return "Sin asignación. Tocá \"Asignar automáticamente\" o elegí los cortes a mano abajo.";

        var porCable = new SortedDictionary<int, List<int>>();
        var asignadas = new HashSet<int>();
        foreach (var c in cables)
        {
            if (c == null || c.Cable < 1 || c.SeccionAog < 1) continue;
            if (!porCable.TryGetValue(c.Cable, out var l)) { l = new List<int>(); porCable[c.Cable] = l; }
            l.Add(c.SeccionAog);
            asignadas.Add(c.SeccionAog);
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
        string mapa = string.Join("  ·  ", partes);

        // Avisos: lo que el operario no ve leyendo la lista.
        var avisos = new List<string>();

        int nSec = C.NumSecAog();
        if (nSec > 0)
        {
            var faltan = new List<int>();
            for (int s = 1; s <= nSec; s++) if (!asignadas.Contains(s)) faltan.Add(s);
            if (faltan.Count > 0)
                avisos.Add(PilotX.Cockpit.Bars.Traductor.T("sin corte asignado:") + " "
                         + PilotX.Cockpit.Bars.Traductor.T(faltan.Count == 1 ? "sección" : "secciones")
                         + " " + string.Join(", ", faltan));
        }

        if (n.MasterCable > 0 && porCable.ContainsKey(n.MasterCable))
            avisos.Add(PilotX.Cockpit.Bars.Traductor.T("el corte")
                     + " " + n.MasterCable + " "
                     + PilotX.Cockpit.Bars.Traductor.T("es la master y además tiene secciones asignadas"));

        var libres = new List<int>();
        for (int i = 1; i <= nCortes; i++)
            if (!porCable.ContainsKey(i) && i != n.MasterCable) libres.Add(i);
        if (libres.Count > 0)
            avisos.Add(PilotX.Cockpit.Bars.Traductor.T(libres.Count == 1 ? "corte sin usar:" : "cortes sin usar:")
                     + " " + string.Join(", ", libres));

        return avisos.Count == 0 ? mapa : mapa + "\n⚠ " + string.Join("  ·  ", avisos);
    }

    // ------------------------------------------------------------------
    //  Reparto automático
    // ------------------------------------------------------------------

    /// <summary>Cambio de cantidad de cortes: re-asigna contra las secciones de
    /// PilotX (si se conocen), conserva la master elegida y redibuja. Sin
    /// estado de PilotX solo se redibuja — cables[] queda como estaba.
    /// OJO: pisa la asignación manual, es el precio de cambiar la cantidad de
    /// válvulas (el mapa nuevo se ve al toque y se puede corregir abajo).</summary>
    private void CambiarCortes(FlowXNodoConfig n, int nCortes)
    {
        if (nCortes < 1) nCortes = 1;
        if (nCortes > FxCtx.MaxCortes) nCortes = FxCtx.MaxCortes;
        n.Cortes = nCortes;                  // dato explícito: no se re-infiere
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
                "El nodo FlowX maneja hasta " + FxCtx.MaxCortes
              + " cortes (el bitmask de secciones que viaja al firmware es de 16 bits).").ConfigureAwait(true);
            return;
        }
        n.Cortes = nCortes;
        n.Cables = FxCtx.AutoAsignarCortes(nCortes, nSec);
        C.RebuildTab?.Invoke();
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
            "Cortes asignados. Tocá Guardar para que quede."), "ok");
    }
}
