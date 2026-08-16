// ============================================================================
// FxNodosTab.cs — pantalla "Nodos en red" del editor de FlowX.
//
// QUE QUEDO NATIVO: la card "Nodos FlowX" de pages/flowx.html — la fila por
// nodo visto por MQTT (uid, ip, firmware, uptime), los avisos de reinicio y
// crashes, el banner de safe-mode con "Limpiar safe-mode" y el Ping por fila.
// QUE SIGUE EN HTML: la misma card en pages/flowx.html, para la PWA.
//
// El monitor (FlowXPanel) lista los nodos de la CONFIG con su caudal; esta
// pantalla lista los que estan publicando en la RED, que es lo que hace falta
// cuando un nodo no aparece o quedo en safe-mode.
//
// Los nodos NO se agregan a mano: entran solos por announcement MQTT y de ahi
// se importan con "+ Agregar descubierto" (regla del repo — evita typos de UID).
// ============================================================================

using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.FlowXEditor;

public sealed class FxNodosTab : FxTab
{
    private int _pintados = -1;

    public FxNodosTab(FxCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();
        _pintados = C.Lan.Count;

        if (C.Lan.Count == 0)
        {
            Children.Add(FxUi.Sub("No hay nodos FlowX en la red. Los controladores aparecen solos "
                                + "cuando se anuncian en el broker MQTT de la pantalla."));
            return;
        }

        foreach (var l in C.Lan) Children.Add(Fila(l));
    }

    /// <summary>Solo se rearma cuando cambia la cantidad de nodos vistos: un
    /// rebuild por tick tiraria el dedo del operario del botón.</summary>
    public override void Live()
    {
        if (C.Lan.Count != _pintados) Rebuild();
    }

    private Control Fila(FlowXNodoLan l)
    {
        var sp = new StackPanel { Spacing = 8 };

        var head = FxUi.Fila(8);
        head.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 10, Height = 10,
            Fill = l.Online ? FxUi.Ok : FxUi.Dim,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = l.Uid ?? "—", Foreground = FxUi.Texto, FontFamily = FxUi.Mono,
            FontSize = 13, FontWeight = Avalonia.Media.FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        string meta = l.Ip ?? "";
        meta += (meta.Length > 0 ? " · " : "") + "fw " + (string.IsNullOrEmpty(l.Firmware) ? "?" : l.Firmware);
        if (l.Uptime > 0) meta += " · up " + FxCtx.FmtUptime(l.Uptime);
        head.Children.Add(new TextBlock
        {
            Text = meta, Foreground = FxUi.TextoDim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (!string.IsNullOrEmpty(l.BootReason)
            && !string.Equals(l.BootReason, "poweron", StringComparison.OrdinalIgnoreCase))
        {
            var b = FxUi.Badge(l.BootReason!, FxUi.Warn);
            ToolTip.SetTip(b, PilotX.Cockpit.Bars.Traductor.T("Última razón de reinicio"));
            head.Children.Add(b);
        }
        if (l.CrashCount > 0)
        {
            var b = FxUi.Badge(l.CrashCount.ToString(CultureInfo.InvariantCulture) + " "
                             + PilotX.Cockpit.Bars.Traductor.T("caídas"), FxUi.Warn);
            ToolTip.SetTip(b, PilotX.Cockpit.Bars.Traductor.T("Caídas seguidas registradas en el nodo"));
            head.Children.Add(b);
        }
        if (l.SafeMode) head.Children.Add(FxUi.Badge("MODO SEGURO", FxUi.Err));

        head.Children.Add(FxUi.Boton("Ping", () => _ = PingAsync(l)));
        sp.Children.Add(head);

        if (l.SafeMode)
        {
            var banner = new StackPanel { Spacing = 6 };
            banner.Children.Add(FxUi.Titulo("Nodo en modo seguro"));
            banner.Children.Add(FxUi.Sub("Solo acepta ping y limpiar el modo seguro hasta que lo "
                                       + "reinicies."));
            banner.Children.Add(FxUi.Boton("Limpiar modo seguro", () => _ = LimpiarAsync(l)));
            sp.Children.Add(new Border
            {
                Background = FxUi.BgSuave, BorderBrush = FxUi.Err,
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10),
                Child = banner,
            });
        }

        return FxUi.Card(sp);
    }

    private async Task PingAsync(FlowXNodoLan l)
    {
        if (string.IsNullOrEmpty(l.Uid)) return;
        var r = await C.Client.SendCmdAsync(l.Uid!, "ping", new { }, C.Ct).ConfigureAwait(true);
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(r.Ok ? "Ping enviado" : "Error")
                         + (r.Ok ? "" : ": " + r.Texto()), r.Ok ? "ok" : "err");
    }

    private async Task LimpiarAsync(FlowXNodoLan l)
    {
        if (string.IsNullOrEmpty(l.Uid) || C.Confirmar == null) return;
        bool ok = await C.Confirmar("Limpiar modo seguro",
            "Se resetea el contador de caídas del nodo " + l.Uid + " y vuelve a operación normal.\n\n"
          + "¿Continuar?").ConfigureAwait(true);
        if (!ok) return;
        var r = await C.Client.SendCmdAsync(l.Uid!, "clear_safe_mode", new { }, C.Ct).ConfigureAwait(true);
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(r.Ok ? "Enviado al nodo" : "Error")
                         + (r.Ok ? "" : ": " + r.Texto()), r.Ok ? "ok" : "err");
    }
}
