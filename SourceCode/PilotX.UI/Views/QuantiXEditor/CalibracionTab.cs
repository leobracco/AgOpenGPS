// ============================================================================
// CalibracionTab.cs — tab Calibración: cuánto entrega el dosificador.
//
// QUÉ QUEDÓ NATIVO: el flujo completo (configurar vueltas+PWM → Iniciar →
// pesar/contar por surco → Calcular → Guardar), con la detección de meta
// client-side y los dos caminos de cuenta (sem/vuelta y MeterCal).
// QUÉ SIGUE EN HTML: la misma tab en pages/quantix.html, para la PWA.
//
// CONTRATO FIRMWARE: en 'cal start' el nodo RESETEA su contador a 0. Por eso
// el punto de partida es 0 y NO el contador previo — snapshotear el previo
// daba Δ=6 con 616 acumulados (reporte 2026-08-10). El guard de 1,5 s evita
// "alcanzar la meta" con el contador viejo, en la ventana entre el envío del
// start y el reset del nodo.
//
// "Pulsos de la corrida" es un INPUT EDITABLE a propósito: pesar y contar
// lleva minutos y el panel puede cerrarse en el medio. Con el campo editable
// el operario carga el número que ve en "Pulsos contados" y calcula igual.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.Controls;

namespace PilotX.Desktop.Views.QuantiXEditor;

public sealed class CalibracionTab : QxTab
{
    private sealed class CalState
    {
        public long? StartPulsos;      // null = no hay corrida viva
        public long? EndPulsos;
        public DateTime StartTs;
        public int Meta;
        public int Vueltas;
        public int Ppr;
        public int Pwm;
        public double? SemVueltaCalc;
        public double? MeterCalCalc;
    }

    private sealed class Refs
    {
        public string Uid = "";
        public int Mi;
        public int Ppr = 20;
        public TextBlock PulsosContados = null!;
        public TextBlock PwmActual = null!;
        public TextBlock VueltasReales = null!;
        public TextBox PulsosRun = null!;
        public TextBox? VueltasRun;
        public TextBlock Msg = null!;
    }

    // El estado sobrevive al cambio de motor/tab: la tab se conserva viva en
    // el panel. Clave "uid|mi".
    private readonly Dictionary<string, CalState> _cal = new(StringComparer.Ordinal);
    private readonly List<Refs> _refs = new();
    private readonly List<TextBox> _surcos = new();
    private string _uidGirando = "";
    private int _miGirando = -1;

    public CalibracionTab(QxEditorCtx c) : base(c) { }

    private CalState Estado(string uid, int mi)
    {
        string k = uid + "|" + mi;
        if (!_cal.TryGetValue(k, out var st)) _cal[k] = st = new CalState();
        return st;
    }

    public override void Rebuild()
    {
        Children.Clear();
        _refs.Clear();
        _surcos.Clear();

        var nodos = C.NodosConUid();
        if (nodos.Count == 0)
        {
            Children.Add(QxUi.Sub("Sin nodos para calibrar. Configurá primero en Siembra."));
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
            long pul = live.Pulsos;
            r.PulsosContados.Text = pul.ToString("#,0", CultureInfo.InvariantCulture);
            r.PwmActual.Text = live.Pwm.ToString(CultureInfo.InvariantCulture) + " / 4095";

            var st = Estado(r.Uid, r.Mi);
            int ppr = r.Ppr > 0 ? r.Ppr : 1;
            if (st.StartPulsos != null)
            {
                long delta = (st.EndPulsos ?? pul) - st.StartPulsos.Value;
                // Con el campo enfocado NO se pisa: el operario lo está
                // corrigiendo a mano y reescribirlo cada tick le manda el
                // cursor al final (la versión nativa del "árbol bajo el dedo").
                if (!r.PulsosRun.IsFocused)
                    r.PulsosRun.Text = delta.ToString(CultureInfo.InvariantCulture);
                r.VueltasReales.Text = (delta / (double)ppr).ToString("0.00", CultureInfo.InvariantCulture);

                // El firmware gira hasta la meta y frena solo. La telemetría no
                // expone un flag de calibración, así que la meta se detecta acá.
                if (st.Meta > 0 && st.EndPulsos == null && delta >= st.Meta
                    && (DateTime.UtcNow - st.StartTs).TotalMilliseconds > 1500)
                {
                    st.EndPulsos = pul;
                    QxUi.SetMsg(r.Msg, "✓ Meta alcanzada — pesá los surcos y apretá Calcular", "ok");
                    _uidGirando = ""; _miGirando = -1;
                }
            }
            else
            {
                // Sin corrida viva NO se pisa el input: puede tener un valor
                // que el operario cargó a mano tras perder el estado.
                int manual = QxUi.LeerInt(r.PulsosRun, 0);
                r.VueltasReales.Text = (manual > 0 && ppr > 0)
                    ? (manual / (double)ppr).ToString("0.00", CultureInfo.InvariantCulture) : "—";
            }
        }
    }

    public override async Task AlSalirAsync()
    {
        if (!string.IsNullOrEmpty(_uidGirando) && _miGirando >= 0)
        {
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

        sp.Children.Add(QxUi.Sub("1) Configurá vueltas a girar y PWM. 2) Apretá Iniciar: el motor gira "
                               + "hasta llegar a la meta de pulsos y para solo. 3) Pesá/contá el producto "
                               + "recolectado por surco. 4) Apretá Calcular: promedia los surcos y calcula "
                               + "MeterCal = pulsos / unidades."));

        var ms = C.MotoresDelNodo(n);
        int mi = C.MotorActivo("calibrar", ms.Count);
        sp.Children.Add(QxMediciones.SelectorMotores(C, "calibrar", n, ms, mi, Rebuild, C.Confirmar));
        sp.Children.Add(CardMotor(n, ms, mi));

        return QxUi.Card(sp);
    }

    private Control CardMotor(QxNodoConfig n, List<QxMotorConfig> ms, int mi)
    {
        var m = ms[mi];
        bool esSem = string.Equals(m.UnidadDosis, "sem_m", StringComparison.Ordinal);
        // PPR y sensor son config del fierro: se editan en Motores. Acá se
        // usan para la cuenta y se muestran, nada más.
        int ppr = m.DientesEngranaje > 0 ? m.DientesEngranaje
                : (string.Equals(m.SensorTipo, "encoder", StringComparison.Ordinal) ? 600 : 20);
        int pwmDef = (int)Math.Round(((m.PwmMin > 0 ? m.PwmMin : 600) + (m.PwmMax > 0 ? m.PwmMax : 4095)) / 2.0);

        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(QxUi.Titulo("M" + mi + " — " + (string.IsNullOrEmpty(m.Nombre) ? "Motor" : m.Nombre)
                                  + " · " + PilotX.Cockpit.Bars.Traductor.T(esSem ? "semilla (sem/m)" : "masa (kg/ha)")));

        // ---- parámetros ----
        var stVueltas = new AgpStepper(10, AgpStepperModo.Int, 1, 1, 100);
        var stPwm = new AgpStepper(pwmDef, AgpStepperModo.Int, 10, 0, 4095);
        var stSurcos = new AgpStepper(6, AgpStepperModo.Int, 1, 1, 20);

        var gParam = QxUi.Grilla();
        gParam.Children.Add(Envolver(QxUi.Campo("Vueltas a girar", stVueltas)));
        gParam.Children.Add(Envolver(QxUi.Campo("PWM", stPwm)));
        gParam.Children.Add(Envolver(QxUi.Campo("Cantidad de surcos", stSurcos)));
        sp.Children.Add(gParam);

        var kv = QxUi.GrillaKv();
        QxUi.AgregarKv(kv, 0, 0, PilotX.Cockpit.Bars.Traductor.T("Pulsos por vuelta"),
            ppr + PilotX.Cockpit.Bars.Traductor.T(" · se configura en Motores"));
        var vMeta = QxUi.AgregarKv(kv, 1, 0, PilotX.Cockpit.Bars.Traductor.T("Meta total"),
            (10 * ppr).ToString("#,0", CultureInfo.InvariantCulture) + " " + PilotX.Cockpit.Bars.Traductor.T("pulsos"));
        QxUi.AgregarKv(kv, 2, 0,
            PilotX.Cockpit.Bars.Traductor.T(esSem ? "Sem/vuelta actual" : "MeterCal actual"),
            (esSem ? m.SemillasVuelta : m.MeterCal).ToString("0.####", CultureInfo.InvariantCulture));
        sp.Children.Add(kv);

        stVueltas.ValorCambiado += _ => vMeta.Text =
            (stVueltas.ValorInt * ppr).ToString("#,0", CultureInfo.InvariantCulture) + " "
            + PilotX.Cockpit.Bars.Traductor.T("pulsos");

        // ---- estado en vivo ----
        var msg = QxUi.Msg();
        var kvLive = QxUi.GrillaKv();
        var vPulsos = QxUi.AgregarKv(kvLive, 0, 0, PilotX.Cockpit.Bars.Traductor.T("Pulsos contados"), "—");

        var txtPulsosRun = QxUi.Entrada(C.Client, "", true, "Pulsos de la corrida", 110);
        var filaRun = QxUi.Fila(8);
        filaRun.Children.Add(txtPulsosRun);
        filaRun.Children.Add(QxUi.Sub("se completa solo al Iniciar · editable"));
        AgregarFila(kvLive, 1, PilotX.Cockpit.Bars.Traductor.T("Pulsos de la corrida"), filaRun);

        TextBox? txtVueltasRun = null;
        int filaSig = 2;
        if (esSem)
        {
            // Para semilla las VUELTAS mandan (sem/vuelta = semillas ÷ vueltas):
            // el firmware gira exactamente lo comandado, así que esto se
            // precarga en Iniciar y no depende de la telemetría de pulsos.
            txtVueltasRun = QxUi.Entrada(C.Client, "", true, "Vueltas de la corrida", 110);
            var f2 = QxUi.Fila(8);
            f2.Children.Add(txtVueltasRun);
            f2.Children.Add(QxUi.Sub("las que giró la placa · manda esta cuenta"));
            AgregarFila(kvLive, filaSig, PilotX.Cockpit.Bars.Traductor.T("Vueltas de la corrida"), f2);
            filaSig++;
        }
        var vVueltasReales = QxUi.AgregarKv(kvLive, filaSig, 0, PilotX.Cockpit.Bars.Traductor.T("Vueltas reales"), "—");
        var vPwmActual = QxUi.AgregarKv(kvLive, filaSig + 1, 0, PilotX.Cockpit.Bars.Traductor.T("PWM actual"), "—");

        _refs.Add(new Refs
        {
            Uid = n.Uid, Mi = mi, Ppr = ppr,
            PulsosContados = vPulsos, PwmActual = vPwmActual, VueltasReales = vVueltasReales,
            PulsosRun = txtPulsosRun, VueltasRun = txtVueltasRun, Msg = msg,
        });

        // ---- botones de control del motor ----
        var ctrl = QxUi.Fila();
        ctrl.Children.Add(QxUi.Boton("▶ Iniciar",
            () => _ = IniciarAsync(n.Uid, mi, stVueltas.ValorInt, ppr, stPwm.ValorInt, txtVueltasRun, msg), primario: true));
        ctrl.Children.Add(QxUi.Boton("■ Detener", () => _ = DetenerAsync(n.Uid, mi, msg)));
        ctrl.Children.Add(QxUi.Boton("⟲ Reset", () => _ = ResetAsync(n.Uid, mi, txtPulsosRun, txtVueltasRun, msg)));
        ctrl.Children.Add(msg);
        sp.Children.Add(ctrl);

        sp.Children.Add(kvLive);

        // ---- resultado por surco ----
        sp.Children.Add(QxUi.Titulo("Resultado por surco"));
        sp.Children.Add(QxUi.Sub(esSem
            ? "Contá las semillas caídas en cada surco. Promediar varios mejora la precisión."
            : "Ingresá gramos medidos en cada surco. Promediar varios mejora la precisión."));

        var boxSurcos = QxUi.Grilla();
        sp.Children.Add(boxSurcos);
        var inputs = new List<TextBox>();
        void RearmarSurcos()
        {
            // Se conserva lo que el operario ya tipeó.
            var previos = new List<string>();
            foreach (var t in inputs) previos.Add(t.Text ?? "");
            boxSurcos.Children.Clear();
            inputs.Clear();
            int cant = stSurcos.ValorInt;
            for (int i = 0; i < cant; i++)
            {
                var t = QxUi.Entrada(C.Client, i < previos.Count ? previos[i] : "", true,
                                   "Surco " + (i + 1), 110);
                inputs.Add(t);
                boxSurcos.Children.Add(Envolver(QxUi.Campo(
                    PilotX.Cockpit.Bars.Traductor.T("Surco ") + (i + 1) + " (g / semillas / L)", t)));
            }
        }
        stSurcos.ValorCambiado += _ => RearmarSurcos();
        RearmarSurcos();

        // ---- calcular / aplicar ----
        var resMsg = QxUi.Msg();
        var cajaRes = QxUi.GrillaKv();
        var vProm = QxUi.AgregarKv(cajaRes, 0, 0, PilotX.Cockpit.Bars.Traductor.T("Promedio por surco"), "—");
        var vUpp  = QxUi.AgregarKv(cajaRes, 1, 0, PilotX.Cockpit.Bars.Traductor.T("Unidades / pulso"), "—");
        var vNew  = QxUi.AgregarKv(cajaRes, 2, 0,
            PilotX.Cockpit.Bars.Traductor.T(esSem ? "Sem/vuelta calculado" : "MeterCal calculado"), "—");
        cajaRes.IsVisible = false;

        Button? btnApply = null;
        var accs = QxUi.Fila();
        accs.Children.Add(QxUi.Boton("✓ Calcular", () =>
        {
            Calcular(n.Uid, mi, esSem, ppr, inputs, txtPulsosRun, txtVueltasRun,
                     vProm, vUpp, vNew, cajaRes, resMsg, btnApply);
        }, primario: true));
        btnApply = QxUi.Boton(esSem ? "💾 Guardar sem/vuelta" : "💾 Guardar MeterCal",
            () => _ = AplicarAsync(n.Uid, mi, ppr, resMsg));
        btnApply.IsEnabled = false;
        accs.Children.Add(btnApply);
        accs.Children.Add(resMsg);
        sp.Children.Add(accs);
        sp.Children.Add(cajaRes);

        return sp;
    }

    private static Control Envolver(Control c)
    {
        c.Margin = new Thickness(0, 0, 12, 8);
        return c;
    }

    private static void AgregarFila(Grid kv, int fila, string clave, Control contenido)
    {
        while (kv.RowDefinitions.Count <= fila) kv.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var k = new TextBlock
        {
            Text = clave, Foreground = QxUi.TextoDim, FontSize = 10, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(k, fila); Grid.SetColumn(k, 0);
        Grid.SetRow(contenido, fila); Grid.SetColumn(contenido, 1);
        contenido.Margin = new Thickness(0, 3, 16, 3);
        kv.Children.Add(k); kv.Children.Add(contenido);
    }

    // =======================================================================
    //  Acciones
    // =======================================================================

    private async Task IniciarAsync(string uid, int mi, int vueltas, int ppr, int pwm,
                                    TextBox? txtVueltasRun, TextBlock msg)
    {
        int meta = vueltas * ppr;
        if (meta <= 0) { QxUi.SetMsg(msg, "✕ vueltas/PPR inválidos", "err"); return; }

        var st = Estado(uid, mi);
        st.StartPulsos = 0;          // el nodo resetea su contador en 'cal start'
        st.EndPulsos = null;
        st.StartTs = DateTime.UtcNow;
        st.Vueltas = vueltas; st.Ppr = ppr; st.Pwm = pwm; st.Meta = meta;

        // Lo comandado ES lo girado: el firmware para solo al llegar a la meta.
        if (txtVueltasRun != null) txtVueltasRun.Text = vueltas.ToString(CultureInfo.InvariantCulture);

        QxUi.SetMsg(msg, "… girando hasta " + meta + " pulsos (" + vueltas + " vueltas)", "");
        _uidGirando = uid; _miGirando = mi;
        var r = await C.Client.CalStartAsync(uid, mi, meta, pwm).ConfigureAwait(true);
        if (!r.Ok) QxUi.SetMsg(msg, "✕ no se pudo enviar MQTT", "err");
    }

    private async Task DetenerAsync(string uid, int mi, TextBlock msg)
    {
        // Stop manual: cierre del Δ con el pulso actual.
        var st = Estado(uid, mi);
        var live = C.LiveMotor(uid, mi);
        st.EndPulsos = live?.Pulsos ?? 0;
        try { await C.Client.CalStopAsync(uid, mi).ConfigureAwait(true); } catch { }
        _uidGirando = ""; _miGirando = -1;
        QxUi.SetMsg(msg, "✓ Detenido. Pesá los surcos y apretá Calcular.", "ok");
        Live();
    }

    private async Task ResetAsync(string uid, int mi, TextBox pulsosRun, TextBox? vueltasRun, TextBlock msg)
    {
        _cal[uid + "|" + mi] = new CalState();
        foreach (var r in _refs)
            if (r.Uid == uid && r.Mi == mi) { r.VueltasReales.Text = "—"; }
        pulsosRun.Text = "";
        if (vueltasRun != null) vueltasRun.Text = "";
        QxUi.SetMsg(msg, "", "");
        // Stop preventivo por las dudas que el motor siga girando.
        try { await C.Client.CalStopAsync(uid, mi).ConfigureAwait(true); } catch { }
        _uidGirando = ""; _miGirando = -1;
        Rebuild();
    }

    private void Calcular(string uid, int mi, bool esSem, int ppr, List<TextBox> inputs,
                          TextBox pulsosRun, TextBox? vueltasRun,
                          TextBlock vProm, TextBlock vUpp, TextBlock vNew, Grid cajaRes,
                          TextBlock resMsg, Button? btnApply)
    {
        double suma = 0; int count = 0;
        foreach (var t in inputs)
        {
            double v = QxUi.LeerDouble(t, double.NaN);
            if (!double.IsNaN(v) && v > 0) { suma += v; count++; }
        }
        if (count == 0)
        {
            QxUi.SetMsg(resMsg, "✕ ingresá al menos un surco con valor > 0", "err");
            return;
        }

        var st = Estado(uid, mi);
        // Cascada: (1) el campo editable, (2) el Δ de la corrida viva,
        // (3) SOLO kg/ha, el contador acumulado del nodo, avisando.
        long pulsosTot = QxUi.LeerInt(pulsosRun, 0);
        if (pulsosTot <= 0 && st.StartPulsos != null)
        {
            long? endP = st.EndPulsos;
            if (endP == null)
            {
                var live = C.LiveMotor(uid, mi);
                endP = live?.Pulsos;
            }
            if (endP != null) pulsosTot = endP.Value - st.StartPulsos.Value;
        }

        double promedio = suma / count;
        string nota = "";

        if (!esSem && pulsosTot <= 0)
        {
            var live = C.LiveMotor(uid, mi);
            pulsosTot = live?.Pulsos ?? 0;
            if (pulsosTot > 0)
            {
                nota = " · ⚠ usé el contador total (" + pulsosTot
                     + "): si tenía pulsos de antes de la corrida, Reset y repetí";
                pulsosRun.Text = pulsosTot.ToString(CultureInfo.InvariantCulture);
            }
        }
        if (!esSem && pulsosTot <= 0)
        {
            QxUi.SetMsg(resMsg, "✕ faltan los pulsos de la corrida: apretá Iniciar, o cargalos a mano "
                              + "en \"Pulsos de la corrida\".", "err");
            return;
        }

        cajaRes.IsVisible = true;

        if (esSem)
        {
            // LAS VUELTAS MANDAN: el firmware gira hasta la meta y para solo,
            // así que lo comandado ES lo girado. Derivarlas de la telemetría
            // de pulsos daba cualquier cosa (Δ=6 con PPR 600 → 2488 sem/vuelta,
            // reporte 2026-08-10). Los pulsos quedan como fallback.
            double vueltasReales = 0;
            double vr = QxUi.LeerDouble(vueltasRun, double.NaN);
            if (!double.IsNaN(vr) && vr > 0) vueltasReales = vr;
            else if (pulsosTot > 0 && ppr > 0)
            {
                vueltasReales = pulsosTot / (double)ppr;
                nota = " · ⚠ vueltas derivadas de los pulsos (" + pulsosTot + "/" + ppr
                     + "): revisá que sean las de la corrida";
            }
            if (vueltasReales <= 0)
            {
                QxUi.SetMsg(resMsg, "✕ cargá \"Vueltas de la corrida\" (cuántas vueltas dio la placa) "
                                  + "y volvé a Calcular.", "err");
                return;
            }
            double semVuelta = promedio / vueltasReales;
            double semPorPulso = ppr > 0 ? semVuelta / ppr : 0;
            vProm.Text = promedio.ToString("0.0", CultureInfo.InvariantCulture) + " sem (" + count + " surcos)";
            vUpp.Text  = semPorPulso.ToString("0.0000", CultureInfo.InvariantCulture) + " sem/pulso";
            vNew.Text  = semVuelta.ToString("0.00", CultureInfo.InvariantCulture) + " sem/vuelta";
            st.SemVueltaCalc = semVuelta; st.MeterCalCalc = null;
            QxUi.SetMsg(resMsg, "✓ " + semVuelta.ToString("0.00", CultureInfo.InvariantCulture)
                              + " sem/vuelta (" + promedio.ToString("0.0", CultureInfo.InvariantCulture)
                              + " sem ÷ " + vueltasReales.ToString("0.##", CultureInfo.InvariantCulture)
                              + " vueltas)" + nota, "ok");
        }
        else
        {
            // meter_cal = pulsos por unidad (gramos) → lo que el bridge multiplica.
            double unidadesPorPulso = promedio / pulsosTot;
            double meterCal = pulsosTot / promedio;
            vProm.Text = promedio.ToString("0.00", CultureInfo.InvariantCulture) + " (" + count + " surcos)";
            vUpp.Text  = unidadesPorPulso.ToString("0.0000", CultureInfo.InvariantCulture) + " u/pulso";
            vNew.Text  = meterCal.ToString("0.0000", CultureInfo.InvariantCulture);
            st.MeterCalCalc = meterCal; st.SemVueltaCalc = null;
            QxUi.SetMsg(resMsg, "✓ MeterCal = " + meterCal.ToString("0.0000", CultureInfo.InvariantCulture) + nota, "ok");
        }

        if (btnApply != null) btnApply.IsEnabled = true;
    }

    private async Task AplicarAsync(string uid, int mi, int ppr, TextBlock resMsg)
    {
        var st = Estado(uid, mi);
        bool esSem = st.SemVueltaCalc != null;
        double nuevo = esSem ? st.SemVueltaCalc!.Value : (st.MeterCalCalc ?? 0);
        if (nuevo <= 0) { QxUi.SetMsg(resMsg, "✕ apretá Calcular primero", "err"); return; }

        var m = C.FindMotor(uid, mi);
        if (m == null) { QxUi.SetMsg(resMsg, "✕ no encuentro el motor", "err"); return; }

        // Guardar una calibración es escribir de verdad sobre este motor: si
        // era el placeholder de la UI deja de serlo, o el guardado lo purga y
        // la calibración se pierde en silencio.
        QxEditorCtx.MarcarTocado(m);
        if (esSem) m.SemillasVuelta = Math.Round(nuevo, 2);
        else m.MeterCal = Math.Round(nuevo, 4);
        if (ppr > 0) m.DientesEngranaje = ppr;

        var g = await C.GuardarAsync().ConfigureAwait(true);
        if (!g.Ok) { QxUi.SetMsg(resMsg, g.TextoError(), "err"); return; }
        var s = await C.Client.SendNodoAsync(uid).ConfigureAwait(true);
        string lbl = esSem
            ? nuevo.ToString("0.00", CultureInfo.InvariantCulture) + " sem/vuelta"
            : "MeterCal=" + nuevo.ToString("0.0000", CultureInfo.InvariantCulture);
        QxUi.SetMsg(resMsg, s.Ok ? ("✓ " + lbl + " guardado y enviado") : "✕ guardado pero MQTT falló",
                    s.Ok ? "ok" : "err");
    }
}
