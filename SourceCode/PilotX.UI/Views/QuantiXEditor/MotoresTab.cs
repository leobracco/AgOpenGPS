// ============================================================================
// MotoresTab.cs — tab Motores: cómo está armado cada motor (sensor, motor,
// PWM, PID). Todo junto en un solo lugar.
//
// QUÉ QUEDÓ NATIVO: las cards por nodo con activar/eliminar nodo, el selector
// de motor por chips, ⧉ Copiar a todos, los bloques Sensor/Motor/PID, el techo
// de lectura del sensor y los botones "Guardar y enviar" / "⏱ Medir tope".
// QUÉ SIGUE EN HTML: la misma tab en pages/quantix.html, para la PWA.
//
// REGLA: los nodos SOLO se incorporan por announcement MQTT. No hay alta
// manual y no se inventa una: evita typos y desalineación con el firmware.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.Controls;

namespace PilotX.Desktop.Views.QuantiXEditor;

public sealed class MotoresTab : QxTab
{
    // Tipo de sensor → filtro antirrebote del nodo y PPR típico.
    // Importa más de lo que parece: el filtro le pone techo a lo que el nodo
    // puede leer. Pasado ese techo NO se queda en el máximo: cuenta uno de
    // cada dos y reporta la mitad de las vueltas (banco 2026-08-01).
    private static readonly (string Clave, int PulseMin, int Ppr, string Label)[] SENSORES =
    {
        ("inductivo", 2000, 20,  "Inductivo (pocos pulsos)"),
        ("encoder",   100,  600, "Encoder (cientos de pulsos)"),
    };

    private CancellationTokenSource? _medicionCts;
    private string _uidGirando = "";
    private int _miGirando = -1;

    // Refrescables por tick (pill de estado + fw), por uid.
    private readonly Dictionary<string, (TextBlock Lbl, Ellipse Dot, TextBlock Fw)> _estado = new(StringComparer.Ordinal);

    public MotoresTab(QxEditorCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();
        _estado.Clear();

        Children.Add(QxUi.Sub("Cómo está armado cada motor: qué sensor cuenta las vueltas, qué motor "
                            + "es y entre qué PWM trabaja. Se guarda y se manda al nodo. Lo de medir "
                            + "vive en Calibración y Prueba."));

        var nodos = C.NodosConUid();
        if (nodos.Count == 0)
        {
            Children.Add(QxUi.Sub("No hay nodos QuantiX cargados todavía. Aparecen solos cuando el "
                                + "nodo se anuncia por MQTT."));
            return;
        }

        foreach (var n in nodos)
            Children.Add(CardNodo(n));
    }

    public override void Live()
    {
        foreach (var kv in _estado)
        {
            bool on = C.NodoOnline(kv.Key);
            kv.Value.Lbl.Text = PilotX.Cockpit.Bars.Traductor.T(on ? "en línea" : "fuera de línea");
            kv.Value.Lbl.Foreground = on ? QxUi.Ok : QxUi.Err;
            kv.Value.Dot.Fill = on ? QxUi.Ok : QxUi.Err;
            string fw = C.NodoFirmware(kv.Key);
            kv.Value.Fw.Text = string.IsNullOrEmpty(fw) ? "" : " · fw " + fw;
        }
    }

    public override async Task AlSalirAsync()
    {
        try { _medicionCts?.Cancel(); } catch { }
        _medicionCts = null;
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

        // ---- cabecera del nodo ----
        var head = QxUi.Fila(10);

        var chkNodo = QxUi.Check("", n.Habilitado);
        ToolTip.SetTip(chkNodo, PilotX.Cockpit.Bars.Traductor.T(
            "Nodo activo en este perfil. Destildado no recibe dosis ni config."));
        chkNodo.IsCheckedChanged += (_, __) =>
        {
            n.Habilitado = chkNodo.IsChecked == true;
            _ = C.GuardarAsync();     // se aplica al instante, sin botón aparte
            Rebuild();
        };
        head.Children.Add(chkNodo);

        head.Children.Add(QxUi.Titulo(string.IsNullOrEmpty(n.Nombre) ? "Nodo" : n.Nombre));

        var fwLbl = QxUi.Mono2("");
        var uidSp = QxUi.Fila(0);
        uidSp.Children.Add(QxUi.Mono2(n.Uid));
        uidSp.Children.Add(fwLbl);
        head.Children.Add(uidSp);

        var pill = QxUi.Pill(PilotX.Cockpit.Bars.Traductor.T("fuera de línea"), QxUi.Err, out var pillLbl, out var pillDot);
        head.Children.Add(pill);
        _estado[n.Uid] = (pillLbl, pillDot, fwLbl);

        var del = QxUi.Boton("Eliminar nodo", () => _ = EliminarNodoAsync(n), peligro: true);
        ToolTip.SetTip(del, PilotX.Cockpit.Bars.Traductor.T(
            "Sacar el nodo del perfil y borrar su configuración"));
        head.Children.Add(del);

        sp.Children.Add(head);

        // ---- selector de motor + config del motor activo ----
        var ms = C.MotoresDelNodo(n);
        int mi = C.MotorActivo("motores", ms.Count);
        sp.Children.Add(QxMediciones.SelectorMotores(C, "motores", n, ms, mi, Rebuild, C.Confirmar));
        sp.Children.Add(ConfigMotor(n, ms, mi));

        return QxUi.Card(sp, atenuada: !n.Habilitado);
    }

    private Control ConfigMotor(QxNodoConfig n, List<QxMotorConfig> ms, int mi)
    {
        var m = ms[mi];
        string tipo = TipoSensorDe(m);
        int ppr = m.DientesEngranaje > 0 ? m.DientesEngranaje : PprDe(tipo);
        int pulseMin = m.PulseMin > 0 ? m.PulseMin : PulseMinDe(tipo);

        var sp = new StackPanel { Spacing = 10, Opacity = m.Habilitado ? 1.0 : 0.6 };

        // ---- cabecera del motor ----
        var mh = QxUi.Fila(10);
        var chk = QxUi.Check(m.Habilitado ? "Motor conectado" : "Sin motor", m.Habilitado);
        chk.IsCheckedChanged += (_, __) =>
        {
            m.Habilitado = chk.IsChecked == true;
            // Tocarlo acá es decisión del operario: si era un placeholder de la
            // UI (nodo sin motores) deja de serlo y a partir de ahora se guarda.
            m.EsPlaceholder = false;
            _ = C.GuardarAsync();
            Rebuild();
        };
        mh.Children.Add(chk);
        var nombre = QxUi.Entrada(C.Client, m.Nombre ?? ("Motor " + (mi + 1)), false, "Nombre del motor", 180);
        nombre.LostFocus += (_, __) =>
        {
            var v = (nombre.Text ?? "").Trim();
            m.Nombre = string.IsNullOrEmpty(v) ? ("Motor " + (mi + 1)) : v;
        };
        mh.Children.Add(nombre);
        sp.Children.Add(mh);

        // ---- Sensor ----
        var stPpr = new AgpStepper(ppr, AgpStepperModo.Int, 1, 1, 4000);
        var stFiltro = new AgpStepper(pulseMin, AgpStepperModo.Int, 10, 20, 20000);
        var techo = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        void RefrescarTecho()
        {
            int rpm = TechoRpm(stFiltro.ValorInt, stPpr.ValorInt);
            techo.Text = PilotX.Cockpit.Bars.Traductor.T("lee hasta ") + rpm + " rpm";
            // Menos de 120 rpm de techo es sospechoso: arriba de ahí el nodo
            // empieza a reportar la mitad de las vueltas sin avisar.
            techo.Foreground = (rpm > 0 && rpm < 120) ? QxUi.Warn : QxUi.TextoMuted;
        }
        stPpr.ValorCambiado += _ => RefrescarTecho();
        stFiltro.ValorCambiado += _ => RefrescarTecho();
        RefrescarTecho();
        ToolTip.SetTip(techo, PilotX.Cockpit.Bars.Traductor.T(
            "Con este filtro el nodo lee hasta esta velocidad"));

        var cbSensor = QxUi.Combo();
        cbSensor.ItemsSource = new List<string>
        {
            PilotX.Cockpit.Bars.Traductor.T(SENSORES[0].Label),
            PilotX.Cockpit.Bars.Traductor.T(SENSORES[1].Label),
        };
        cbSensor.SelectedIndex = tipo == "encoder" ? 1 : 0;
        // Cambiar el tipo PRECARGA ppr + filtro típicos. No guarda solo: el
        // operario confirma con "Guardar y enviar" (puede querer otro PPR).
        cbSensor.SelectionChanged += (_, __) =>
        {
            var def = SENSORES[cbSensor.SelectedIndex == 1 ? 1 : 0];
            stPpr.SetValor(def.Ppr, false);
            stFiltro.SetValor(def.PulseMin, false);
            RefrescarTecho();
        };

        var gSensor = QxUi.Grilla();
        gSensor.Children.Add(Envolver(QxUi.Campo("Tipo", cbSensor)));
        gSensor.Children.Add(Envolver(QxUi.Campo("Pulsos por vuelta", stPpr)));
        gSensor.Children.Add(Envolver(QxUi.Campo("Filtro antirrebote", stFiltro)));
        var spSensor = new StackPanel { Spacing = 6 };
        spSensor.Children.Add(gSensor);
        spSensor.Children.Add(techo);
        sp.Children.Add(QxUi.Bloque(PilotX.Cockpit.Bars.Traductor.T("SENSOR"), spSensor));

        // ---- Motor ----
        var cbMotor = QxUi.Combo();
        cbMotor.ItemsSource = new List<string>
        {
            PilotX.Cockpit.Bars.Traductor.T("Eléctrico"),
            PilotX.Cockpit.Bars.Traductor.T("Hidráulico"),
        };
        cbMotor.SelectedIndex = m.MotorType == 1 ? 1 : 0;
        var stPwmMin = new AgpStepper(m.PwmMin, AgpStepperModo.Int, 10, 0, 4095);
        var stPwmMax = new AgpStepper(m.PwmMax > 0 ? m.PwmMax : 4095, AgpStepperModo.Int, 10, 0, 4095);
        var gMotor = QxUi.Grilla();
        gMotor.Children.Add(Envolver(QxUi.Campo("Tipo", cbMotor)));
        gMotor.Children.Add(Envolver(QxUi.Campo("PWM mínimo", stPwmMin)));
        gMotor.Children.Add(Envolver(QxUi.Campo("PWM máximo", stPwmMax)));
        sp.Children.Add(QxUi.Bloque(PilotX.Cockpit.Bars.Traductor.T("MOTOR"), gMotor));

        // ---- PID ----
        var stKp = new AgpStepper(m.Kp, AgpStepperModo.Pid, 1, 0, 300);
        var stKi = new AgpStepper(m.Ki, AgpStepperModo.Pid, 1, 0, 200);
        var stKd = new AgpStepper(m.Kd, AgpStepperModo.Pid, 1, 0, 50);
        var gPid = QxUi.Grilla();
        gPid.Children.Add(Envolver(QxUi.Campo("Kp", stKp)));
        gPid.Children.Add(Envolver(QxUi.Campo("Ki", stKi)));
        gPid.Children.Add(Envolver(QxUi.Campo("Kd", stKd)));

        var kv = QxUi.GrillaKv();
        var vTope = QxUi.AgregarKv(kv, 0, 0, PilotX.Cockpit.Bars.Traductor.T("Tope del motor (Max Hz)"),
            TextoTope(m.MaxHz, ppr));
        var spPid = new StackPanel { Spacing = 6 };
        spPid.Children.Add(gPid);
        spPid.Children.Add(kv);
        sp.Children.Add(QxUi.Bloque(PilotX.Cockpit.Bars.Traductor.T("PID"), spPid));

        // ---- acciones ----
        var msg = QxUi.Msg();
        Button? btnTope = null;

        void LeerCampos()
        {
            // "Guardar y enviar" sobre este motor también cuenta como tocarlo:
            // si era placeholder, lo configuró a mano y quiere que quede.
            m.EsPlaceholder = false;
            m.SensorTipo = cbSensor.SelectedIndex == 1 ? "encoder" : "inductivo";
            m.MotorType = cbMotor.SelectedIndex == 1 ? 1 : 0;
            m.DientesEngranaje = stPpr.ValorInt;
            m.PulseMin = stFiltro.ValorInt;
            m.PwmMin = stPwmMin.ValorInt;
            m.PwmMax = stPwmMax.ValorInt;
            m.Kp = stKp.Valor;
            m.Ki = stKi.Valor;
            m.Kd = stKd.Valor;
        }

        var acciones = QxUi.Fila();
        acciones.Children.Add(QxUi.Boton("Guardar y enviar", () => _ = GuardarYEnviarAsync(n.Uid, LeerCampos, msg), primario: true));
        btnTope = QxUi.Boton("⏱ Medir tope", () => _ = MedirTopeAsync(n.Uid, mi, msg, btnTope!, vTope, stPpr));
        ToolTip.SetTip(btnTope, PilotX.Cockpit.Bars.Traductor.T(
            "Gira el motor a PWM máximo 4 s y guarda el tope medido"));
        acciones.Children.Add(btnTope);
        acciones.Children.Add(msg);
        sp.Children.Add(acciones);

        return sp;
    }

    private static Control Envolver(Control c)
    {
        c.Margin = new Thickness(0, 0, 12, 8);
        return c;
    }

    private static string TextoTope(double maxHz, int ppr)
        => maxHz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz · "
         + Math.Round(maxHz * 60 / (ppr > 0 ? ppr : 1)).ToString("0", CultureInfo.InvariantCulture) + " rpm";

    private async Task GuardarYEnviarAsync(string uid, Action leerCampos, TextBlock msg)
    {
        leerCampos();
        QxUi.SetMsg(msg, "… guardando", "");
        var r = await C.GuardarAsync().ConfigureAwait(true);
        if (!r.Ok) { QxUi.SetMsg(msg, r.TextoError(), "err"); return; }
        var s = await C.Client.SendNodoAsync(uid).ConfigureAwait(true);
        QxUi.SetMsg(msg, s.Ok ? "✓ guardado y enviado al nodo" : "✕ guardado, pero el nodo no contestó",
                    s.Ok ? "ok" : "err");
    }

    private async Task MedirTopeAsync(string uid, int mi, TextBlock msg, Button btn,
                                      TextBlock kvTope, AgpStepper stPpr)
    {
        // Medir el tope arranca el motor por rampa hasta PWM MÁXIMO y lo deja
        // 4 s ahí. El aviso vivía solo en el ToolTip, que con guantes en una
        // pantalla táctil no existe: el operario tocaba y el dosificador
        // arrancaba solo.
        bool seguir = C.Confirmar == null || await C.Confirmar("Medir el tope del motor",
            "El motor va a girar hasta el máximo durante unos 4 segundos para medir "
            + "cuánto da.\n\nMirá que no haya nadie cerca del dosificador. ¿Arrancamos?")
            .ConfigureAwait(true);
        if (!seguir) return;

        _medicionCts?.Cancel();
        _medicionCts = new CancellationTokenSource();
        _uidGirando = uid; _miGirando = mi;
        try
        {
            await QxMediciones.MedirMaxHzAsync(C, uid, mi, msg, btn, _medicionCts.Token).ConfigureAwait(true);
        }
        finally
        {
            _uidGirando = ""; _miGirando = -1;
        }
        var m2 = C.FindMotor(uid, mi);
        if (m2 != null) kvTope.Text = TextoTope(m2.MaxHz, stPpr.ValorInt);
    }

    private async Task EliminarNodoAsync(QxNodoConfig n)
    {
        bool ok = C.Confirmar == null || await C.Confirmar("Eliminar nodo",
            "¿Eliminar el nodo " + n.Uid + "? Se borra su configuración de motores y se lo saca del "
            + "perfil. Si el nodo vuelve a anunciarse por MQTT va a reaparecer como pendiente.")
            .ConfigureAwait(true);
        if (!ok) return;

        // Best-effort: si el DELETE falla igual limpiamos la config local.
        try { await C.Client.BorrarNodoAsync(n.Uid).ConfigureAwait(true); } catch { }
        C.Cfg.Nodos.RemoveAll(x => x != null && string.Equals(x.Uid, n.Uid, StringComparison.Ordinal));
        await C.GuardarAsync().ConfigureAwait(true);
        Rebuild();
    }

    // ---- helpers del sensor ----------------------------------------------

    private static string TipoSensorDe(QxMotorConfig m)
    {
        if (!string.IsNullOrEmpty(m.SensorTipo)) return m.SensorTipo!;
        // Config vieja sin el campo: lo deducimos del PPR guardado.
        return m.DientesEngranaje >= 100 ? "encoder" : "inductivo";
    }

    private static int PprDe(string tipo) => tipo == "encoder" ? 600 : 20;
    private static int PulseMinDe(string tipo) => tipo == "encoder" ? 100 : 2000;

    /// <summary>Vueltas por minuto que el nodo puede llegar a leer con ese
    /// filtro y ese PPR.</summary>
    private static int TechoRpm(int pulseMin, int ppr)
    {
        if (pulseMin <= 0 || ppr <= 0) return 0;
        return (int)Math.Round(60000000.0 / ((double)pulseMin * ppr));
    }
}
