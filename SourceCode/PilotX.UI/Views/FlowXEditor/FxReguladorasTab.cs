// ============================================================================
// FxReguladorasTab.cs — pantalla "Reguladoras" (los "productos" del DTO).
//
// QUE QUEDO NATIVO: la pestana 2 de pages/flowx.html — la tabla slim de
// reguladoras (id, nombre, tipo, caudalimetro, dosis, badge auto/manual) con
// seleccion por fila y el alta/baja.
// QUE SIGUE EN HTML: la misma tabla en pages/flowx.html, para la PWA.
//
// La tabla es de filas Border tocables (Tapped), no un DataGrid: con guante en
// la pantalla del tractor las celdas de un DataGrid no se aciertan.
//
// Los parametros de PID/actuador NO estan aca: se editan en "Nodo activo" para
// la reguladora seleccionada. Como el nativo edita el MISMO objeto del DTO (no
// reconstruye la lista al guardar, como hacia el JS leyendo el DOM), los campos
// que esta pantalla no muestra no se pueden perder ni saltar de reguladora.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.FlowXEditor;

public sealed class FxReguladorasTab : FxTab
{
    public FxReguladorasTab(FxCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();

        var n = C.NodoActual();
        if (n == null)
        {
            Children.Add(FxUi.Sub("Elegí un nodo arriba para ver sus reguladoras."));
            return;
        }

        Children.Add(FxUi.Sub("Elegí la reguladora tocando su fila — sus parámetros de PID y actuador "
                            + "se editan en Nodo activo › Salida y barra."));

        var ps = FxCtx.Productos(n);
        var lista = new StackPanel { Spacing = 6 };
        if (ps.Count == 0)
        {
            lista.Children.Add(FxUi.Sub("Este nodo no tiene reguladoras. Agregá una con el botón de abajo."));
        }
        else
        {
            for (int i = 0; i < ps.Count; i++) lista.Children.Add(Fila(n, ps, i));
        }
        Children.Add(FxUi.Card(lista));

        var acc = FxUi.Fila(8);
        acc.Children.Add(FxUi.Boton("+ Agregar reguladora", () => _ = AgregarAsync(n)));
        Children.Add(acc);
    }

    private Control Fila(FlowXNodoConfig n, List<FlowXProducto> ps, int idx)
    {
        var p = ps[idx];
        bool sel = idx == C.SelectedProdIdx;

        var g = FxUi.Grilla();

        // Id: el firmware acepta {0,1} — canal 0 = reg. 1, canal 1 = reg. 2.
        var cbId = FxUi.ComboOpciones(
            new List<(int, string)> { (0, "reg. 1 (id 0)"), (1, "reg. 2 (id 1)") },
            p.Id <= 0 ? 0 : 1, v => p.Id = v, 130);
        g.Children.Add(FxUi.Campo("Id", cbId, 140));

        var txtNom = FxUi.Entrada(C.Client, p.Nombre ?? "", false, "Nombre de la reguladora", 150);
        txtNom.TextChanged += (_, __) => p.Nombre = txtNom.Text ?? "";
        g.Children.Add(FxUi.Campo("Nombre", txtNom, 160));

        var cbTipo = FxUi.ComboOpciones(
            new List<(int, string)> { (0, "Válvula"), (1, "Motor") },
            string.Equals(p.Tipo, "motor", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            v => p.Tipo = v == 1 ? "motor" : "valvula", 120);
        g.Children.Add(FxUi.Campo("Tipo", cbTipo, 130));

        var cbFlow = FxUi.ComboOpciones(
            new List<(int, string)> { (0, "1"), (1, "2") },
            p.FlowIndex == 1 ? 1 : 0, v => p.FlowIndex = v, 90);
        ToolTip.SetTip(cbFlow, PilotX.Cockpit.Bars.Traductor.T(
            "Cuál de los 2 caudalímetros lee esta reguladora"));
        g.Children.Add(FxUi.Campo("Caudalímetro", cbFlow, 100));

        g.Children.Add(FxUi.Campo("Dosis (L/ha)",
            FxUi.EntradaNum(C.Client, p.DosisLha, 1, "Dosis (L/ha)",
                            v => p.DosisLha = v < 0 ? 0 : v, 100), 110));

        var estado = FxUi.Fila(6);
        estado.Children.Add(p.ModoManual ? FxUi.Badge("Manual", FxUi.Warn) : FxUi.Badge("Auto", FxUi.Ok));
        g.Children.Add(FxUi.Campo("Modo", estado, 90));

        var acc = FxUi.Fila(6);
        acc.Children.Add(FxUi.Boton("Editar PID", () => Seleccionar(idx, irANodo: true)));
        acc.Children.Add(FxUi.Boton("×", () => Eliminar(n, ps, idx), peligro: true));
        g.Children.Add(FxUi.Campo(" ", acc, 160));

        var fila = new Border
        {
            Background = sel ? FxUi.BgFilaSel : FxUi.BgFila,
            BorderBrush = sel ? FxUi.Verde : FxUi.BordeSuave,
            BorderThickness = new Thickness(sel ? 2 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 4),
            Child = g,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // Tocar la fila (fuera de un control) la selecciona.
        fila.Tapped += (_, e) =>
        {
            if (e.Source is Control src && EsControlDeEdicion(src)) return;
            Seleccionar(idx, irANodo: false);
        };
        return fila;
    }

    private static bool EsControlDeEdicion(Control c)
    {
        for (var v = (Visual?)c; v != null; v = v.GetVisualParent())
            if (v is TextBox || v is ComboBox || v is Button || v is CheckBox) return true;
        return false;
    }

    private void Seleccionar(int idx, bool irANodo)
    {
        C.SelectedProdIdx = idx;
        if (irANodo) C.IrANodoActivo?.Invoke();
        else C.RebuildTab?.Invoke();
    }

    private void Eliminar(FlowXNodoConfig n, List<FlowXProducto> ps, int idx)
    {
        if (idx < 0 || idx >= ps.Count) return;
        ps.RemoveAt(idx);
        if (C.SelectedProdIdx >= ps.Count) C.SelectedProdIdx = Math.Max(0, ps.Count - 1);
        C.RebuildTab?.Invoke();
    }

    private async System.Threading.Tasks.Task AgregarAsync(FlowXNodoConfig n)
    {
        var ps = FxCtx.Productos(n);
        if (ps.Count >= FxCtx.MaxReguladoras)
        {
            if (C.Alertar != null)
                await C.Alertar("Límite del firmware",
                    "El nodo FlowX maneja hasta 2 reguladoras (reg. 1 y reg. 2). "
                  + "Una tercera la ignoraría el nodo.").ConfigureAwait(true);
            return;
        }
        int nextId = 0;
        foreach (var p in ps) if (p.Id >= nextId) nextId = p.Id + 1;
        if (nextId > 1) nextId = 1;
        ps.Add(new FlowXProducto
        {
            Id = nextId,
            Nombre = "Producto " + (ps.Count + 1).ToString(CultureInfo.InvariantCulture),
            FlowIndex = nextId,
        });
        C.SelectedProdIdx = ps.Count - 1;
        C.RebuildTab?.Invoke();
    }
}
