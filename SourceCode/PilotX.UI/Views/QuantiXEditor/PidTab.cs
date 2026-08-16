// ============================================================================
// PidTab.cs — tab PID live: afinar el lazo con el motor andando.
//
// QUÉ QUEDÓ NATIVO: los KV en vivo (rpm, dosis real, dosis objetivo, PWM), los
// steppers adaptativos Kp/Ki/Kd, "Aplicar Kp/Ki/Kd" (config PARCIAL que el
// firmware mergea), "⏱ Medir Max Hz" y "🎯 Auto-Tune" con su poll de 50 s.
// QUÉ SIGUE EN HTML: la misma tab en pages/quantix.html, para la PWA.
//
// Los KV se refrescan por tick SIN reconstruir el árbol: un rebuild con el
// dedo sobre el [+] de Kp lo dejaría a mitad de camino.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.Controls;

namespace PilotX.Desktop.Views.QuantiXEditor;

public sealed class PidTab : QxTab
{
    private sealed class Refs
    {
        public string Uid = "";
        public int Mi;
        public TextBlock Rpm = null!, DosisReal = null!, DosisObj = null!, Pwm = null!;
        public TextBlock PillLbl = null!;
        public Ellipse PillDot = null!;
    }

    private readonly List<Refs> _refs = new();
    private CancellationTokenSource? _cts;
    private string _uidGirando = "";
    private int _miGirando = -1;

    public PidTab(QxEditorCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();
        _refs.Clear();

        var nodos = C.NodosConUid();
        if (nodos.Count == 0)
        {
            Children.Add(QxUi.Sub("Cargá primero los nodos en la pestaña Siembra."));
            return;
        }
        foreach (var n in nodos) Children.Add(CardNodo(n));
    }

    public override void Live()
    {
        var ctx = C.AgroCtx();
        foreach (var r in _refs)
        {
            bool on = C.NodoOnline(r.Uid);
            r.PillLbl.Text = PilotX.Cockpit.Bars.Traductor.T(on ? "en línea" : "fuera de línea");
            r.PillLbl.Foreground = on ? QxUi.Ok : QxUi.Err;
            r.PillDot.Fill = on ? QxUi.Ok : QxUi.Err;

            var live = C.LiveMotor(r.Uid, r.Mi);
            var cfg = C.FindMotor(r.Uid, r.Mi);
            if (live == null)
            {
                r.Rpm.Text = "—"; r.DosisReal.Text = "—"; r.DosisObj.Text = "—"; r.Pwm.Text = "—";
                continue;
            }
            r.Rpm.Text = QxAgro.PpsToRpm(cfg, live.PpsReal).ToString("0", CultureInfo.InvariantCulture) + " rpm";
            r.DosisReal.Text = cfg != null ? QxAgro.Label(QxAgro.Units(cfg, live.PpsReal, ctx)) : "—";
            r.DosisObj.Text  = cfg != null ? QxAgro.Label(QxAgro.Units(cfg, live.PpsTarget, ctx)) : "—";
            r.Pwm.Text = live.Pwm.ToString(CultureInfo.InvariantCulture);
        }
    }

    public override async Task AlSalirAsync()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        if (!string.IsNullOrEmpty(_uidGirando) && _miGirando >= 0)
        {
            try { await C.Client.TestStopAsync(_uidGirando, _miGirando).ConfigureAwait(false); } catch { }
            _uidGirando = ""; _miGirando = -1;
        }
    }

    // =======================================================================

    private Control CardNodo(QxNodoConfig n)
    {
        var sp = new StackPanel { Spacing = 10 };

        var head = QxUi.Fila(10);
        head.Children.Add(QxUi.Titulo(string.IsNullOrEmpty(n.Nombre) ? "Nodo" : n.Nombre));
        head.Children.Add(QxUi.Mono2(n.Uid));
        var pill = QxUi.Pill(PilotX.Cockpit.Bars.Traductor.T("fuera de línea"), QxUi.Err,
                             out var pillLbl, out var pillDot);
        head.Children.Add(pill);
        sp.Children.Add(head);

        var ms = C.MotoresDelNodo(n);
        int mi = C.MotorActivo("pid", ms.Count);
        sp.Children.Add(QxMediciones.SelectorMotores(C, "pid", n, ms, mi, Rebuild, C.Confirmar));

        var m = ms[mi];
        var mc = new StackPanel { Spacing = 10 };
        mc.Children.Add(QxUi.Titulo("M" + mi + " — " + (string.IsNullOrEmpty(m.Nombre) ? "Motor" : m.Nombre)));

        var kv = QxUi.GrillaKv(2);
        var rpm = QxUi.AgregarKv(kv, 0, 0, "rpm", "—");
        var pwm = QxUi.AgregarKv(kv, 0, 2, "PWM", "—");
        var dReal = QxUi.AgregarKv(kv, 1, 0, PilotX.Cockpit.Bars.Traductor.T("Dosis real"), "—");
        var dObj  = QxUi.AgregarKv(kv, 1, 2, PilotX.Cockpit.Bars.Traductor.T("Dosis obj."), "—");
        mc.Children.Add(kv);

        _refs.Add(new Refs
        {
            Uid = n.Uid, Mi = mi, Rpm = rpm, DosisReal = dReal, DosisObj = dObj, Pwm = pwm,
            PillLbl = pillLbl, PillDot = pillDot,
        });

        // Steppers adaptativos: v<1 → 0.01, v<10 → 0.1, v<100 → 1, v≥100 → 5.
        // El paso acompaña al valor actual: se afina Kd=0.5 y Kp=120 con el
        // mismo control.
        var stKp = new AgpStepper(m.Kp, AgpStepperModo.Pid, 1, 0, 300);
        var stKi = new AgpStepper(m.Ki, AgpStepperModo.Pid, 1, 0, 200);
        var stKd = new AgpStepper(m.Kd, AgpStepperModo.Pid, 1, 0, 50);
        var g = QxUi.Grilla();
        g.Children.Add(Envolver(QxUi.Campo("Kp", stKp)));
        g.Children.Add(Envolver(QxUi.Campo("Ki", stKi)));
        g.Children.Add(Envolver(QxUi.Campo("Kd", stKd)));
        mc.Children.Add(g);

        var msg = QxUi.Msg();
        Button? btnMaxHz = null;
        Button? btnAuto = null;

        var acciones = QxUi.Fila();
        acciones.Children.Add(QxUi.Boton("Aplicar Kp/Ki/Kd",
            () => _ = AplicarPidAsync(n.Uid, mi, stKp.Valor, stKi.Valor, stKd.Valor, msg), primario: true));

        btnMaxHz = QxUi.Boton("⏱ Medir Max Hz", () => _ = MedirAsync(n.Uid, mi, msg, btnMaxHz!));
        ToolTip.SetTip(btnMaxHz, PilotX.Cockpit.Bars.Traductor.T(
            "Mide Hz pico con PWM máximo durante 4 s y guarda en max_hz"));
        acciones.Children.Add(btnMaxHz);

        btnAuto = QxUi.Boton("🎯 Auto-Tune", () => _ = AutoTuneAsync(n.Uid, mi, msg, btnAuto!, stKp, stKi, stKd));
        ToolTip.SetTip(btnAuto, PilotX.Cockpit.Bars.Traductor.T("Auto-Tune PID. Tarda hasta 50 s"));
        acciones.Children.Add(btnAuto);

        acciones.Children.Add(msg);
        mc.Children.Add(acciones);

        sp.Children.Add(mc);
        return QxUi.Card(sp);
    }

    private static Control Envolver(Control c)
    {
        c.Margin = new Avalonia.Thickness(0, 0, 12, 8);
        return c;
    }

    // =======================================================================

    private async Task AplicarPidAsync(string uid, int mi, double kp, double ki, double kd, TextBlock msg)
    {
        QxUi.SetMsg(msg, "… enviando", "");
        var m = C.FindMotor(uid, mi);
        // Aplicar PID es escribir de verdad sobre este motor: si era el
        // placeholder de la UI, deja de serlo (si no, el guardado lo purga y
        // los Kp/Ki/Kd se pierden sin avisar).
        if (m != null) { QxEditorCtx.MarcarTocado(m); m.Kp = kp; m.Ki = ki; m.Kd = kd; }

        var r = await C.Client.PushPidAsync(uid, mi, kp, ki, kd).ConfigureAwait(true);
        QxUi.SetMsg(msg, r.Ok ? "✓ aplicado" : r.TextoError(), r.Ok ? "ok" : "err");
        // Persistir motores.json después del push (el orden es el del HTML).
        await C.GuardarAsync().ConfigureAwait(true);
    }

    private async Task MedirAsync(string uid, int mi, TextBlock msg, Button btn)
    {
        // Igual que "Medir tope" de Motores: sube el motor a PWM máximo y lo
        // sostiene 4 s. El aviso estaba solo en el ToolTip — inútil con
        // guantes en táctil.
        bool seguir = C.Confirmar == null || await C.Confirmar("Medir el máximo del motor",
            "El motor va a girar hasta el máximo durante unos 4 segundos para medir "
            + "su tope.\n\nMirá que no haya nadie cerca del dosificador. ¿Arrancamos?")
            .ConfigureAwait(true);
        if (!seguir) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _uidGirando = uid; _miGirando = mi;
        try { await QxMediciones.MedirMaxHzAsync(C, uid, mi, msg, btn, _cts.Token).ConfigureAwait(true); }
        finally { _uidGirando = ""; _miGirando = -1; }
    }

    /// <summary>Manda autotune_start y poolea /autotune cada 1 s hasta 50 s
    /// buscando un resultado POSTERIOR al inicio. Si llega ok, pide
    /// confirmación y aplica Kp/Ki/Kd.</summary>
    private async Task AutoTuneAsync(string uid, int mi, TextBlock msg, Button btn,
                                     AgpStepper stKp, AgpStepper stKi, AgpStepper stKd)
    {
        if (!btn.IsEnabled) return;

        // El Auto-Tune sacude el motor con escalones de PWM hasta 50 s. Lo
        // único que lo avisaba era el ToolTip ("Tarda hasta 50 s"), que en
        // táctil no se ve nunca: un toque y el dosificador arrancaba solo.
        bool seguir = C.Confirmar == null || await C.Confirmar("Arrancar el Auto-Tune",
            "El motor va a arrancar y frenar solo, a fondo, hasta 50 segundos, "
            + "para encontrar el ajuste del control.\n\n"
            + "Mirá que no haya nadie cerca del dosificador. ¿Arrancamos?").ConfigureAwait(true);
        if (!seguir) return;

        btn.IsEnabled = false;
        object? orig = btn.Content;
        btn.Content = PilotX.Cockpit.Bars.Traductor.T("⏳ Tuning…");
        QxUi.SetMsg(msg, "… autotune en curso (hasta 50 s)", "");

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var inicio = DateTime.UtcNow;
        QxAutoTuneResult? result = null;

        try
        {
            var start = await C.Client.AutoTuneStartAsync(uid, mi, ct).ConfigureAwait(true);
            if (!start.Ok)
            {
                QxUi.SetMsg(msg, "✕ no se pudo enviar start: " + (start.Error ?? "fallo"), "err");
                return;
            }

            while ((DateTime.UtcNow - inicio).TotalMilliseconds < 50000)
            {
                await Task.Delay(1000, ct).ConfigureAwait(true);
                var r = await C.Client.GetAutoTuneAsync(uid, ct).ConfigureAwait(true);
                if (r == null) continue;
                if (!DateTime.TryParse(r.ReceivedUtc, CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts)) continue;
                if (ts < inicio.AddSeconds(-1)) continue;
                if (r.MotorId != mi && r.MotorId != 0) continue;
                result = r;
                break;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { QxUi.SetMsg(msg, "✕ " + ex.Message, "err"); return; }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = orig;
        }

        if (result == null)
        {
            QxUi.SetMsg(msg, "✕ timeout — el firmware no respondió en 50s", "err");
            return;
        }
        if (!result.Ok)
        {
            QxUi.SetMsg(msg, "✕ autotune falló — el motor no oscilo lo suficiente", "err");
            return;
        }

        double kp = Math.Round(result.Kp, 1), ki = Math.Round(result.Ki, 1), kd = Math.Round(result.Kd, 1);
        string valores = "Kp = " + kp.ToString("0.0", CultureInfo.InvariantCulture)
                       + "   Ki = " + ki.ToString("0.0", CultureInfo.InvariantCulture)
                       + "   Kd = " + kd.ToString("0.0", CultureInfo.InvariantCulture);
        bool aplicar = C.Confirmar == null
            || await C.Confirmar("Auto-Tune completado", valores + "\n\n¿Aplicar estos valores?").ConfigureAwait(true);
        if (!aplicar)
        {
            QxUi.SetMsg(msg, PilotX.Cockpit.Bars.Traductor.T("Resultado descartado (") + valores + ")", "");
            return;
        }

        stKp.SetValor(kp, false);
        stKi.SetValor(ki, false);
        stKd.SetValor(kd, false);
        await AplicarPidAsync(uid, mi, kp, ki, kd, msg).ConfigureAwait(true);
    }
}
