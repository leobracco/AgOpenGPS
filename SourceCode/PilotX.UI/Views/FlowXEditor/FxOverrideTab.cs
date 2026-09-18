// ============================================================================
// FxOverrideTab.cs — pantalla "Override por sección" + sincronizar al firmware.
//
// QUE QUEDO NATIVO: la pestana 4 de pages/flowx.html — la grilla de 2/3 cables
// por corte y los botones "Sincronizar config al firmware" y "Ping".
// QUE SIGUE EN HTML: la misma pestana en pages/flowx.html, para la PWA.
//
// section_is_3wire viaja SIEMPRE con 10 enteros: mandar menos tiene
// comportamiento indefinido en el firmware. Se muestran solo los cortes reales
// del nodo; los slots que no se ven van como -1 (Global).
//
// El payload de config-push es camelCase (meterCal/is3Wire/invertRelay/
// invertMotor/sectionIs3Wire): es contrato del FIRMWARE, no del wire de PilotX.
// Un campo con otro nombre lo ignora el nodo sin chistar — por eso lo arma el
// cliente a mano en vez de serializar un DTO snake_case.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.FlowXEditor;

public sealed class FxOverrideTab : FxTab
{
    public FxOverrideTab(FxCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();

        var n = C.NodoActual();
        if (n == null)
        {
            Children.Add(FxUi.Sub("Elegí un nodo arriba para configurar sus electroválvulas."));
            return;
        }

        Children.Add(FxUi.Sub("Hasta 10 cortes pueden tener un tipo de electroválvula distinto al "
                            + "general. Global usa la casilla \"3 hilos por defecto\" de Nodo activo. "
                            + "2 cables = válvula motorizada (puente H); 3 cables = mando directo."));

        // El DTO se normaliza acá: si el archivo trae menos de 10 (o basura),
        // queda prolijo antes de que el operario toque nada.
        var arr = FxCtx.NormalizarSec3w(n.SectionIs3Wire);
        n.SectionIs3Wire = arr;

        int nCortes = FxCtx.InferNumCortes(n);
        int visibles = Math.Min(10, Math.Max(1, nCortes));

        var grilla = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < visibles; i++)
        {
            int idx = i;
            var cb = FxUi.ComboOpciones(
                new List<(int, string)> { (-1, "Global"), (0, "2 cables"), (1, "3 cables") },
                arr[idx], v => n.SectionIs3Wire![idx] = v, 130);
            grilla.Children.Add(FxUi.Campo("Corte " + (i + 1).ToString(CultureInfo.InvariantCulture),
                                           cb, 140));
        }
        Children.Add(FxUi.Bloque("Tipo de electroválvula por corte", grilla));

        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(FxUi.Sub("Envía al nodo la configuración que él guarda en su memoria "
                               + "(calibración del caudalímetro, 2/3 hilos, inversión de relés y de "
                               + "motor, y el override por corte)."));
        var acc = FxUi.Fila(8);
        acc.Children.Add(FxUi.Boton("Sincronizar config al firmware", () => _ = SincronizarAsync(n), primario: true));
        acc.Children.Add(FxUi.Boton("Ping", () => _ = PingAsync(n)));
        sp.Children.Add(acc);
        Children.Add(FxUi.Bloque("Sincronizar config al firmware", sp));
    }

    private async Task SincronizarAsync(FlowXNodoConfig n)
    {
        string uid = n.Uid ?? "";
        if (string.IsNullOrEmpty(uid)) return;

        // meterCal canonico = el de la reguladora 1 (canal 0): el firmware tiene
        // un solo Sensor[0] global, no uno por producto.
        var ps = FxCtx.Productos(n);
        double meterCal = ps.Count > 0 ? ps[0].MeterCal : 0;

        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Enviando…"), "");
        var r = await C.Client.PushConfigAsync(uid, meterCal, n.Is3Wire, n.InvertRelay, n.InvertMotor,
                                               FxCtx.NormalizarSec3w(n.SectionIs3Wire), C.Ct)
            .ConfigureAwait(true);
        if (r.Ok)
            C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Enviado a") + " " + (r.Topic ?? ""), "ok");
        else
            C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Error") + ": " + r.Texto(), "err");
    }

    private async Task PingAsync(FlowXNodoConfig n)
    {
        string uid = n.Uid ?? "";
        if (string.IsNullOrEmpty(uid)) return;
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Pingueando…"), "");
        var r = await C.Client.SendCmdAsync(uid, "ping", new { }, C.Ct).ConfigureAwait(true);
        if (r.Ok) C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Ping enviado"), "ok");
        else C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Error") + ": " + r.Texto(), "err");
    }
}
