// ============================================================================
// PruebaTab.cs — tab Prueba: diagnóstico del motor y del sensor.
//
// QUÉ QUEDÓ NATIVO: "Girar X pulsos", la búsqueda de PWM mínimo por rampa y la
// calibración avanzada (PWM min/max, Max Hz, FF gain, Alpha, PID time, slew).
// QUÉ SIGUE EN HTML: la misma tab en pages/quantix.html, para la PWA.
//
// La búsqueda de PWM mínimo se porta TAL CUAL: su rework está expresamente
// diferido (memoria del proyecto pwm_min_search_pending). Acá no se "mejora"
// nada — cambiar la metodología sin confirmarla en banco es cómo se rompe la
// dosificación de una sembradora.
//
// verb=test no tiene meta: la rampa se cancela y manda stop en todo camino de
// salida, incluido cerrar el panel (AlSalirAsync).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.QuantiXEditor;

public sealed class PruebaTab : QxTab
{
    private sealed class Refs
    {
        public string Uid = "";
        public int Mi;
        public TextBlock Rpm = null!, Pwm = null!, Pulsos = null!;
    }

    private readonly List<Refs> _refs = new();
    private CancellationTokenSource? _rampaCts;
    private string _uidGirando = "";
    private int _miGirando = -1;

    public PruebaTab(QxEditorCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();
        _refs.Clear();

        var nodos = C.NodosConUid();
        if (nodos.Count == 0)
        {
            Children.Add(QxUi.Sub("Sin nodos. Configurá primero en Siembra."));
            return;
        }
        foreach (var n in nodos) Children.Add(CardNodo(n));
    }

    public override void Live()
    {
        foreach (var r in _refs)
        {
            var live = C.LiveMotor(r.Uid, r.Mi);
            if (live == null) continue;
            var cfg = C.FindMotor(r.Uid, r.Mi);
            r.Rpm.Text = QxAgro.PpsToRpm(cfg, live.PpsReal).ToString("0", CultureInfo.InvariantCulture) + " rpm";
            r.Pwm.Text = live.Pwm.ToString(CultureInfo.InvariantCulture);
            r.Pulsos.Text = live.Pulsos.ToString("#,0", CultureInfo.InvariantCulture);
        }
    }

    public override async Task AlSalirAsync()
    {
        try { _rampaCts?.Cancel(); } catch { }
        _rampaCts = null;
        if (!string.IsNullOrEmpty(_uidGirando) && _miGirando >= 0)
        {
            // Puede haber quedado girando por test (rampa) o por cal (girar X
            // pulsos): se mandan los dos stops, best-effort.
            try { await C.Client.TestStopAsync(_uidGirando, _miGirando).ConfigureAwait(false); } catch { }
            try { await C.Client.CalStopAsync(_uidGirando, _miGirando).ConfigureAwait(false); } catch { }
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
        sp.Children.Add(head);

        var ms = C.MotoresDelNodo(n);
        int mi = C.MotorActivo("prueba", ms.Count);
        sp.Children.Add(QxMediciones.SelectorMotores(C, "prueba", n, ms, mi, Rebuild, C.Confirmar));
        sp.Children.Add(CardMotor(n, ms, mi));

        return QxUi.Card(sp);
    }

    private Control CardMotor(QxNodoConfig n, List<QxMotorConfig> ms, int mi)
    {
        var m = ms[mi];
        string uid = n.Uid;
        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(QxUi.Titulo("M" + mi + " — " + (string.IsNullOrEmpty(m.Nombre) ? "Motor" : m.Nombre)));

        // ---- KV live ----
        var kv = QxUi.GrillaKv(2);
        var vRpm = QxUi.AgregarKv(kv, 0, 0, "rpm", "—");
        var vPwm = QxUi.AgregarKv(kv, 0, 2, PilotX.Cockpit.Bars.Traductor.T("PWM actual"), "—");
        var vPul = QxUi.AgregarKv(kv, 1, 0, PilotX.Cockpit.Bars.Traductor.T("Pulsos"), "—");
        var vPwmCfg = QxUi.AgregarKv(kv, 1, 2, PilotX.Cockpit.Bars.Traductor.T("PWM min cfg"),
            m.PwmMin.ToString(CultureInfo.InvariantCulture));
        sp.Children.Add(kv);
        _refs.Add(new Refs { Uid = uid, Mi = mi, Rpm = vRpm, Pwm = vPwm, Pulsos = vPul });

        // ---- Girar X pulsos ----
        var txtPulsos = QxUi.Entrada(C.Client, "500", true, "Pulsos meta", 110);
        var txtPwm = QxUi.Entrada(C.Client, "2000", true, "PWM", 110);
        var msgSpin = QxUi.Msg();
        var gSpin = QxUi.Grilla();
        gSpin.Children.Add(Envolver(QxUi.Campo("Pulsos meta", txtPulsos)));
        gSpin.Children.Add(Envolver(QxUi.Campo("PWM", txtPwm)));
        var btnsSpin = QxUi.Fila();
        btnsSpin.Children.Add(QxUi.Boton("▶ Girar",
            () => _ = GirarAsync(uid, mi, txtPulsos, txtPwm, msgSpin), primario: true));
        btnsSpin.Children.Add(QxUi.Boton("■ Detener", () => _ = DetenerGiroAsync(uid, mi, msgSpin)));
        btnsSpin.Children.Add(msgSpin);
        var spSpin = new StackPanel { Spacing = 6 };
        spSpin.Children.Add(gSpin);
        spSpin.Children.Add(btnsSpin);
        sp.Children.Add(QxUi.Bloque(PilotX.Cockpit.Bars.Traductor.T("GIRAR X PULSOS"), spSpin));

        // ---- Buscar PWM mínimo (rampa) ----
        var txtPaso = QxUi.Entrada(C.Client, "50", true, "Paso PWM", 110);
        var txtInt = QxUi.Entrada(C.Client, "600", true, "Intervalo (ms)", 110);
        var txtHz = QxUi.Entrada(C.Client, "2", true, "Umbral Hz", 110);
        var txtPwmMax = QxUi.Entrada(C.Client, "4095", true, "PWM max", 110);
        var gRampa = QxUi.Grilla();
        gRampa.Children.Add(Envolver(QxUi.Campo("Paso PWM", txtPaso)));
        gRampa.Children.Add(Envolver(QxUi.Campo("Intervalo (ms)", txtInt)));
        gRampa.Children.Add(Envolver(QxUi.Campo("Umbral Hz", txtHz)));
        gRampa.Children.Add(Envolver(QxUi.Campo("PWM max", txtPwmMax)));

        var kvRampa = QxUi.GrillaKv(2);
        var vEstado = QxUi.AgregarKv(kvRampa, 0, 0, PilotX.Cockpit.Bars.Traductor.T("Rampa estado"), "idle");
        var vRampaPwm = QxUi.AgregarKv(kvRampa, 0, 2, PilotX.Cockpit.Bars.Traductor.T("PWM rampa"), "—");

        var msgRampa = QxUi.Msg();
        var btnsRampa = QxUi.Fila();
        btnsRampa.Children.Add(QxUi.Boton("▶ Buscar PWM min", () => _ = RampaAsync(
            uid, mi, txtPaso, txtInt, txtHz, txtPwmMax, vEstado, vRampaPwm, vPwmCfg, msgRampa), primario: true));
        btnsRampa.Children.Add(QxUi.Boton("■ Cancelar", () => _ = CancelarRampaAsync(uid, mi, vEstado, msgRampa)));
        btnsRampa.Children.Add(msgRampa);

        var spRampa = new StackPanel { Spacing = 6 };
        spRampa.Children.Add(gRampa);
        spRampa.Children.Add(kvRampa);
        spRampa.Children.Add(btnsRampa);
        sp.Children.Add(QxUi.Bloque(PilotX.Cockpit.Bars.Traductor.T("BUSCAR PWM MÍNIMO (RAMPA)"), spRampa));

        // ---- Calibración avanzada ----
        var tPwmMin = QxUi.Entrada(C.Client, m.PwmMin.ToString(CultureInfo.InvariantCulture), true, "PWM min", 110);
        var tPwmMax = QxUi.Entrada(C.Client, m.PwmMax.ToString(CultureInfo.InvariantCulture), true, "PWM max", 110);
        var tMaxHz = QxUi.Entrada(C.Client, m.MaxHz.ToString("0.###", CultureInfo.InvariantCulture), true, "Max Hz", 110);
        var tFf = QxUi.Entrada(C.Client, m.FFGain.ToString("0.##", CultureInfo.InvariantCulture), true, "FF gain", 110);
        var tAlpha = QxUi.Entrada(C.Client, m.Alpha.ToString("0.##", CultureInfo.InvariantCulture), true, "Alpha", 110);
        var tPidT = QxUi.Entrada(C.Client, m.PIDTime.ToString(CultureInfo.InvariantCulture), true, "PID time (ms)", 110);
        var tSlew = QxUi.Entrada(C.Client, m.SlewRatePerSec.ToString("0", CultureInfo.InvariantCulture), true, "Slew/s", 110);
        var tRampaDosis = QxUi.Entrada(C.Client, m.TargetSlewHzPerSec.ToString("0", CultureInfo.InvariantCulture), true, "Rampa dosis (Hz/s)", 110);

        var gAv = QxUi.Grilla();
        gAv.Children.Add(Envolver(QxUi.Campo("PWM min", tPwmMin)));
        gAv.Children.Add(Envolver(QxUi.Campo("PWM max", tPwmMax)));
        gAv.Children.Add(Envolver(QxUi.Campo("Max Hz (FF)", tMaxHz)));
        gAv.Children.Add(Envolver(QxUi.Campo("FF gain", tFf)));
        gAv.Children.Add(Envolver(QxUi.Campo("Alpha", tAlpha)));
        gAv.Children.Add(Envolver(QxUi.Campo("PID time (ms)", tPidT)));
        gAv.Children.Add(Envolver(QxUi.Campo("Slew/s", tSlew)));
        gAv.Children.Add(Envolver(QxUi.Campo("Rampa dosis (Hz/s)", tRampaDosis)));

        var msgCal = QxUi.Msg();
        var btnsCal = QxUi.Fila();
        btnsCal.Children.Add(QxUi.Boton("Aplicar calibración", () => _ = AplicarAvanzadaAsync(
            uid, mi, tPwmMin, tPwmMax, tMaxHz, tFf, tAlpha, tPidT, tSlew, tRampaDosis, msgCal), primario: true));
        btnsCal.Children.Add(msgCal);

        var spAv = new StackPanel { Spacing = 6 };
        spAv.Children.Add(gAv);
        spAv.Children.Add(btnsCal);
        sp.Children.Add(QxUi.Bloque(PilotX.Cockpit.Bars.Traductor.T("CALIBRACIÓN AVANZADA"), spAv));

        return sp;
    }

    private static Control Envolver(Control c)
    {
        c.Margin = new Thickness(0, 0, 12, 8);
        return c;
    }

    // =======================================================================

    private async Task GirarAsync(string uid, int mi, TextBox tPulsos, TextBox tPwm, TextBlock msg)
    {
        int pulsos = QxUi.LeerInt(tPulsos, 0);
        int pwm = QxUi.LeerInt(tPwm, 0);
        if (pulsos <= 0 || pwm <= 0) { QxUi.SetMsg(msg, "✕ valores inválidos", "err"); return; }
        QxUi.SetMsg(msg, "… girando " + pulsos + " pulsos a PWM " + pwm, "");
        _uidGirando = uid; _miGirando = mi;
        var r = await C.Client.CalStartAsync(uid, mi, pulsos, pwm).ConfigureAwait(true);
        QxUi.SetMsg(msg, r.Ok ? "✓ enviado" : r.TextoError(), r.Ok ? "ok" : "err");
    }

    private async Task DetenerGiroAsync(string uid, int mi, TextBlock msg)
    {
        var r = await C.Client.CalStopAsync(uid, mi).ConfigureAwait(true);
        _uidGirando = ""; _miGirando = -1;
        QxUi.SetMsg(msg, r.Ok ? "✓ detenido" : r.TextoError(), r.Ok ? "ok" : "err");
    }

    /// <summary>Rampa client-side: suma `paso` cada `intervalo` ms y lee el
    /// pps de la CACHÉ live (igual que el HTML). El primer PWM que produce
    /// pps ≥ umbral es el pwm_min: se guarda y se manda al nodo.</summary>
    private async Task RampaAsync(string uid, int mi, TextBox tPaso, TextBox tInt, TextBox tHz,
                                  TextBox tPwmMax, TextBlock vEstado, TextBlock vRampaPwm,
                                  TextBlock vPwmCfg, TextBlock msg)
    {
        if (_rampaCts != null) return;    // ya hay una rampa corriendo
        int paso = QxUi.LeerInt(tPaso, 50);
        int intervalo = QxUi.LeerInt(tInt, 600);
        double umbral = QxUi.LeerDouble(tHz, 2);
        int pwmMax = QxUi.LeerInt(tPwmMax, 4095);
        if (paso <= 0) paso = 50;
        if (intervalo < 200) intervalo = 200;

        _rampaCts = new CancellationTokenSource();
        var ct = _rampaCts.Token;
        _uidGirando = uid; _miGirando = mi;

        QxUi.SetMsg(msg, "… midiendo", "");
        vEstado.Text = PilotX.Cockpit.Bars.Traductor.T("rampando");
        int pwm = 0;
        bool encontrado = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                pwm += paso;
                if (pwm > pwmMax)
                {
                    QxUi.SetMsg(msg, "✕ No detectó pulsos hasta PWM " + pwmMax, "err");
                    vEstado.Text = PilotX.Cockpit.Bars.Traductor.T("sin pulsos");
                    break;
                }
                vRampaPwm.Text = pwm.ToString(CultureInfo.InvariantCulture);
                try { await C.Client.TestAsync(uid, mi, pwm, ct).ConfigureAwait(true); } catch { }

                await Task.Delay(Math.Max(150, intervalo - 50), ct).ConfigureAwait(true);
                double pps = C.LiveMotor(uid, mi)?.PpsReal ?? 0;
                if (pps >= umbral)
                {
                    encontrado = true;
                    vEstado.Text = PilotX.Cockpit.Bars.Traductor.T("encontrado");
                    QxUi.SetMsg(msg, "✓ PWM mínimo = " + pwm + " (Hz="
                                   + pps.ToString("0.0", CultureInfo.InvariantCulture) + ")", "ok");
                    var m = C.FindMotor(uid, mi);
                    if (m != null)
                    {
                        m.PwmMin = pwm;
                        await C.GuardarAsync(CancellationToken.None).ConfigureAwait(true);
                        await C.Client.SendNodoAsync(uid, CancellationToken.None).ConfigureAwait(true);
                        vPwmCfg.Text = pwm.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                }
                await Task.Delay(50, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Pase lo que pase, el motor para.
            try { await C.Client.TestStopAsync(uid, mi, CancellationToken.None).ConfigureAwait(true); } catch { }
            _rampaCts = null;
            _uidGirando = ""; _miGirando = -1;
            if (!encontrado && vEstado.Text == PilotX.Cockpit.Bars.Traductor.T("rampando"))
                vEstado.Text = "idle";
        }
    }

    private async Task CancelarRampaAsync(string uid, int mi, TextBlock vEstado, TextBlock msg)
    {
        try { _rampaCts?.Cancel(); } catch { }
        vEstado.Text = PilotX.Cockpit.Bars.Traductor.T("cancelado");
        QxUi.SetMsg(msg, "■ cancelado", "");
        try { await C.Client.TestStopAsync(uid, mi).ConfigureAwait(true); } catch { }
        _uidGirando = ""; _miGirando = -1;
    }

    private async Task AplicarAvanzadaAsync(string uid, int mi,
        TextBox tPwmMin, TextBox tPwmMax, TextBox tMaxHz, TextBox tFf, TextBox tAlpha,
        TextBox tPidT, TextBox tSlew, TextBox tRampaDosis, TextBlock msg)
    {
        var m = C.FindMotor(uid, mi);
        if (m == null) { QxUi.SetMsg(msg, "✕ motor no encontrado", "err"); return; }
        QxUi.SetMsg(msg, "… enviando", "");

        m.PwmMin = QxUi.LeerInt(tPwmMin, 0);
        m.PwmMax = QxUi.LeerInt(tPwmMax, 4095);
        m.MaxHz = QxUi.LeerDouble(tMaxHz, 0);
        m.FFGain = QxUi.LeerDouble(tFf, 1.0);
        m.Alpha = QxUi.LeerDouble(tAlpha, 0.4);
        m.PIDTime = QxUi.LeerInt(tPidT, 50);
        m.SlewRatePerSec = QxUi.LeerDouble(tSlew, 0);
        m.TargetSlewHzPerSec = QxUi.LeerDouble(tRampaDosis, 300);

        var g = await C.GuardarAsync().ConfigureAwait(true);
        if (!g.Ok) { QxUi.SetMsg(msg, g.TextoError(), "err"); return; }
        var s = await C.Client.SendNodoAsync(uid).ConfigureAwait(true);
        QxUi.SetMsg(msg, s.Ok ? "✓ aplicado" : s.TextoError(), s.Ok ? "ok" : "err");
    }
}
