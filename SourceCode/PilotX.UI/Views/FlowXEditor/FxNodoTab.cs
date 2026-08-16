// ============================================================================
// FxNodoTab.cs — pantalla "Nodo activo" del editor de FlowX.
//
// QUE QUEDO NATIVO: la pestana 1 de pages/flowx.html — datos generales del
// nodo, ancho de barra (con "Desde PilotX"), la reguladora en edicion, su PID
// y actuador, las electrovalvulas del nodo y los cuatro flujos de puesta a
// punto: PWM minimo manual, calibrar caudalimetro, auto-tune PID y barrido
// automatico.
// QUE SIGUE EN HTML: la misma pestana en pages/flowx.html, para la PWA.
//
// Los cuatro flujos hacen MOVER la reguladora de verdad. Todos preguntan antes
// (overlay interno del panel, jamas ShowDialog) y los polls son series de GET
// cortos cancelables con el token del panel — un GET largo dejaria la cabina
// esperando 60 s sin poder cerrar.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.Controls;

namespace PilotX.Desktop.Views.FlowXEditor;

public sealed class FxNodoTab : FxTab
{
    private TextBlock? _estadoLbl;
    private Border?    _estadoBadge;
    private TextBlock? _estadoMeta;

    public FxNodoTab(FxCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();
        _estadoLbl = null; _estadoBadge = null; _estadoMeta = null;

        var n = C.NodoActual();
        if (n == null)
        {
            Children.Add(FxUi.Sub("Elegí un nodo arriba. Si no hay ninguno, agregá el que aparezca "
                                + "en la lista de descubiertos: los nodos entran solos cuando se "
                                + "anuncian por MQTT."));
            return;
        }

        Children.Add(BloqueGenerales(n));
        Children.Add(BloqueSalida(n));
        Children.Add(BloqueElectrovalvulas(n));
        Children.Add(FxUi.Sub("Cada reguladora es un caudalímetro más una válvula (o motor) con su "
                            + "propio PID. El firmware soporta 2 reguladoras; esta versión actúa "
                            + "solo sobre la reguladora 1."));
        Live();
    }

    public override void Live()
    {
        var n = C.NodoActual();
        if (n == null || _estadoLbl == null || _estadoBadge == null) return;
        var reg = C.LanDe(n.Uid);
        bool online = reg != null && reg.Online;
        _estadoLbl.Text = PilotX.Cockpit.Bars.Traductor.T(online ? "Online" : "Offline");
        _estadoLbl.Foreground = online ? FxUi.Ok : FxUi.Err;
        _estadoBadge.BorderBrush = online ? FxUi.Ok : FxUi.Err;
        if (_estadoMeta != null)
        {
            string meta = "";
            if (reg != null && !string.IsNullOrEmpty(reg.Firmware)) meta += "· fw " + reg.Firmware + " ";
            if (reg != null && reg.Uptime > 0) meta += "· up " + FxCtx.FmtUptime(reg.Uptime);
            _estadoMeta.Text = meta;
        }
    }

    // =======================================================================
    //  Datos generales
    // =======================================================================

    private Control BloqueGenerales(FlowXNodoConfig n)
    {
        var g = FxUi.Grilla();

        g.Children.Add(FxUi.Campo("UID", FxUi.MonoTexto(n.Uid ?? "—")));

        var txtNombre = FxUi.Entrada(C.Client, n.Nombre ?? "", false, "Nombre del nodo", 180);
        txtNombre.TextChanged += (_, __) => n.Nombre = txtNombre.Text ?? "";
        g.Children.Add(FxUi.Campo("Nombre", txtNombre, 190));

        _estadoLbl = new TextBlock
        {
            Text = "—", Foreground = FxUi.Dim, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _estadoBadge = new Border
        {
            Background = FxUi.BgFila, BorderBrush = FxUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center, Child = _estadoLbl,
        };
        _estadoMeta = new TextBlock
        {
            Text = "", Foreground = FxUi.TextoDim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var estado = FxUi.Fila(6);
        estado.Children.Add(_estadoBadge);
        estado.Children.Add(_estadoMeta);
        g.Children.Add(FxUi.Campo("Estado", estado, 180));

        var acciones = FxUi.Fila(8);
        acciones.Children.Add(FxUi.Check("Habilitado", n.Habilitado, v => n.Habilitado = v));
        acciones.Children.Add(FxUi.Boton("Eliminar nodo", () => _ = EliminarAsync(n), peligro: true));
        g.Children.Add(FxUi.Campo(" ", acciones, 240));

        return FxUi.Bloque("Datos generales", g);
    }

    private async Task EliminarAsync(FlowXNodoConfig n)
    {
        if (C.Confirmar == null) return;
        bool ok = await C.Confirmar("Eliminar nodo",
            "¿Eliminar nodo " + (n.Uid ?? "?") + " de la configuración?").ConfigureAwait(true);
        if (!ok) return;
        C.Nodos().Remove(n);
        C.CurrentUid = null;
        C.SelectedProdIdx = 0;
        // Solo en memoria: se persiste con "Guardar" (igual que el HTML).
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
            "Nodo sacado de la lista. Tocá Guardar para que quede."), "");
        C.RefrescarSelector?.Invoke();
    }

    // =======================================================================
    //  Salida y barra + PID de la reguladora en edicion
    // =======================================================================

    private Control BloqueSalida(FlowXNodoConfig n)
    {
        var sp = new StackPanel { Spacing = 10 };

        // ---- ancho de barra ----
        var g = FxUi.Grilla();
        var txtAncho = FxUi.EntradaNum(C.Client, n.AnchoBarraM, 2, "Ancho de barra (m)",
                                       v => n.AnchoBarraM = v < 0 ? 0 : v, 120);
        g.Children.Add(FxUi.Campo("Ancho de barra (m)", txtAncho, 140));
        g.Children.Add(FxUi.Campo(" ", FxUi.Boton("Desde PilotX", () => _ = DesdePilotXAsync(n, txtAncho)), 150));
        sp.Children.Add(g);

        double anchoAog = C.AnchoAog();
        sp.Children.Add(FxUi.Sub(anchoAog > 0
            ? "PilotX reporta ancho de implemento: " + FxUi.Num(anchoAog, 2) + " m"
            : "PilotX todavía no reporta ancho de implemento."));

        // ---- reguladora en edicion + modo ----
        var ps = FxCtx.Productos(n);
        var g2 = FxUi.Grilla();

        var opsProd = new List<(int, string)>();
        for (int i = 0; i < ps.Count; i++)
        {
            string nom = string.IsNullOrEmpty(ps[i].Nombre) ? "Reg. " + (i + 1) : ps[i].Nombre!;
            opsProd.Add((i, nom + " (id " + ps[i].Id.ToString(CultureInfo.InvariantCulture) + ")"));
        }
        if (opsProd.Count == 0) opsProd.Add((0, "(sin reguladoras)"));
        var cbProd = FxUi.ComboOpciones(opsProd, C.SelectedProdIdx, i =>
        {
            if (i == C.SelectedProdIdx) return;
            C.SelectedProdIdx = i;
            C.RebuildTab?.Invoke();
        }, 190);
        cbProd.IsEnabled = ps.Count > 0;
        g2.Children.Add(FxUi.Campo("Reguladora a editar", cbProd, 200));

        var p = C.ProductoActual(n);
        if (p != null)
        {
            var cbModo = FxUi.ComboOpciones(
                new List<(int, string)> { (0, "Automático (dosis L/ha)"), (1, "Manual (caudal L/min fijo)") },
                p.ModoManual ? 1 : 0, v => p.ModoManual = v == 1, 220);
            g2.Children.Add(FxUi.Campo("Modo", cbModo, 230));
        }
        sp.Children.Add(g2);

        if (p == null)
        {
            sp.Children.Add(FxUi.Sub("Este nodo no tiene reguladoras cargadas. Agregá una en la "
                                   + "pantalla Reguladoras."));
            return FxUi.Bloque("Salida y barra", sp);
        }

        // ---- PID y actuador ----
        string nombreP = string.IsNullOrEmpty(p.Nombre) ? "reg. " + (C.SelectedProdIdx + 1) : p.Nombre!;
        sp.Children.Add(FxUi.Titulo("PID y actuador — " + nombreP));

        var g3 = FxUi.Grilla();
        g3.Children.Add(FxUi.Campo("Calibración (pulsos/L)",
            FxUi.EntradaNum(C.Client, p.MeterCal, 2, "Calibración (pulsos/L)",
                            v => p.MeterCal = v, 120), 140));

        var stPwmMin = new AgpStepper(p.PwmMin, AgpStepperModo.Int, 5, 0, 4095);
        stPwmMin.ValorCambiado += v => p.PwmMin = (int)Math.Round(v);
        ToolTip.SetTip(stPwmMin, PilotX.Cockpit.Bars.Traductor.T(
            "PWM al que arranca el PID (12 bits, 0..4095)"));
        g3.Children.Add(FxUi.Campo("PWM mín", stPwmMin, 170));

        var stPwmMax = new AgpStepper(p.PwmMax, AgpStepperModo.Int, 5, 0, 4095);
        stPwmMax.ValorCambiado += v => p.PwmMax = (int)Math.Round(v);
        ToolTip.SetTip(stPwmMax, PilotX.Cockpit.Bars.Traductor.T("0 ó ≤ mín = sin techo"));
        g3.Children.Add(FxUi.Campo("PWM máx", stPwmMax, 170));

        var stKp = new AgpStepper(p.Kp, AgpStepperModo.Pid, 1, 0, 1000);
        stKp.ValorCambiado += v => p.Kp = v;
        g3.Children.Add(FxUi.Campo("Kp", stKp, 170));

        var stKi = new AgpStepper(p.Ki, AgpStepperModo.Pid, 1, 0, 1000);
        stKi.ValorCambiado += v => p.Ki = v;
        g3.Children.Add(FxUi.Campo("Ki", stKi, 170));

        var stKd = new AgpStepper(p.Kd, AgpStepperModo.Pid, 1, 0, 1000);
        stKd.ValorCambiado += v => p.Kd = v;
        g3.Children.Add(FxUi.Campo("Kd", stKd, 170));

        g3.Children.Add(FxUi.Campo("Caudal fijo manual (L/min)",
            FxUi.EntradaNum(C.Client, p.ManualLmin, 1, "Caudal fijo manual (L/min)",
                            v => p.ManualLmin = v < 0 ? 0 : v, 120), 150));
        g3.Children.Add(FxUi.Campo("Paso botón ± (L/ha)",
            FxUi.EntradaNum(C.Client, p.PasoLha, 1, "Paso botón ± (L/ha)",
                            v => p.PasoLha = v, 110), 140));
        g3.Children.Add(FxUi.Campo("Paso botón ± (L/min)",
            FxUi.EntradaNum(C.Client, p.PasoLmin, 1, "Paso botón ± (L/min)",
                            v => p.PasoLmin = v, 110), 140));
        sp.Children.Add(g3);

        sp.Children.Add(FxUi.Check("Invertir sentido de esta reguladora", p.InvertMotor,
                                   v => p.InvertMotor = v));

        // ---- acciones que mueven la maquina ----
        var acc = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Control c) { c.Margin = new Thickness(0, 0, 8, 8); acc.Children.Add(c); }
        Add(FxUi.Boton("Detectar PWM mínimo", () => C.AbrirPwmManual?.Invoke(n, C.SelectedProdIdx), primario: true));
        Add(FxUi.Boton("Calibrar caudalímetro", () => _ = CalibrarAsync(n, p)));
        Add(FxUi.Boton("Auto-tune PID", () => _ = AutotuneAsync(n, p)));
        Add(FxUi.Boton("Barrido auto (avanzado)", () => _ = CaracterizarAsync(n, p)));
        sp.Children.Add(acc);

        return FxUi.Bloque("Salida y barra", sp);
    }

    private async Task DesdePilotXAsync(FlowXNodoConfig n, TextBox txt)
    {
        double w = C.AnchoAog();
        if (!(w > 0))
        {
            if (C.Alertar != null)
                await C.Alertar("Sin ancho disponible",
                    "PilotX no reporta ancho de implemento todavía. Configurá el implemento primero.")
                    .ConfigureAwait(true);
            return;
        }
        n.AnchoBarraM = w;
        txt.Text = FxUi.Num(w, 2);
    }

    private Control BloqueElectrovalvulas(FlowXNodoConfig n)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(FxUi.Check("Electroválvulas 3 hilos por defecto (destildar = 2 hilos)",
                                   n.Is3Wire, v => n.Is3Wire = v));
        sp.Children.Add(FxUi.Check("Invertir electroválvulas (NA/NC)",
                                   n.InvertRelay, v => n.InvertRelay = v));
        sp.Children.Add(FxUi.Check("Invertir motor válvula reg. 1 (si abre/cierra al revés)",
                                   n.InvertMotor, v => n.InvertMotor = v));
        return FxUi.Bloque("Electroválvulas", sp);
    }

    // =======================================================================
    //  Calibrar caudalimetro
    // =======================================================================

    private async Task CalibrarAsync(FlowXNodoConfig n, FlowXProducto p)
    {
        if (C.Pedir == null || C.Confirmar == null || C.Alertar == null) return;
        string uid = n.Uid ?? "";
        if (string.IsNullOrEmpty(uid)) return;

        string? volStr = await C.Pedir("Calibración del caudalímetro",
            "Vamos a contar pulsos del caudalímetro mientras pasa un volumen conocido. "
          + "Ingresá el volumen exacto que vas a verter (litros):", "1").ConfigureAwait(true);
        if (volStr == null) return;

        double vol;
        if (!double.TryParse((volStr ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out vol) || !(vol > 0))
        {
            await C.Alertar("Volumen inválido", "El valor ingresado no es un número positivo.")
                .ConfigureAwait(true);
            return;
        }

        var start = await C.Client.SendCmdAsync(uid, "calibrar_start",
            new { producto_id = p.Id, vol_l = vol, pwm = 2048 }, C.Ct).ConfigureAwait(true);
        if (!start.Ok)
        {
            await C.Alertar("Error de inicio", "No se pudo iniciar la calibración: " + start.Texto())
                .ConfigureAwait(true);
            return;
        }

        string nombreP = string.IsNullOrEmpty(p.Nombre) ? p.Id.ToString(CultureInfo.InvariantCulture) : p.Nombre!;
        bool seguir = await C.Confirmar("Calibración en curso",
            "Calibración iniciada en la reguladora \"" + nombreP + "\". Vertí exactamente "
          + FxUi.Num(vol, 2) + " L por el caudalímetro. Cuando termines tocá Aceptar para detener "
          + "y leer los pulsos. Cancelar = abortar.").ConfigureAwait(true);

        // En los dos casos hay que frenar la cuenta en el nodo.
        await C.Client.SendCmdAsync(uid, "calibrar_stop", new { producto_id = p.Id }, C.Ct)
            .ConfigureAwait(true);
        if (!seguir) return;

        var r = await C.Client.PollResultAsync(uid, "calibrar", 8000, C.Ct).ConfigureAwait(true);
        if (r == null)
        {
            await C.Alertar("Calibración fallida", "Se agotó la espera de respuesta del nodo.")
                .ConfigureAwait(true);
            return;
        }
        if (!FxJson.Bool(r.Value, "ok"))
        {
            await C.Alertar("Error del nodo",
                "El firmware devolvió error: " + FxJson.Str(r.Value, "error", "desconocido"))
                .ConfigureAwait(true);
            return;
        }
        double pulsos = FxJson.Num(r.Value, "pulsos") ?? 0;
        if (pulsos <= 0)
        {
            await C.Alertar("Sin pulsos",
                "No se detectaron pulsos. Revisá el cableado del caudalímetro y reintentá.")
                .ConfigureAwait(true);
            return;
        }

        double meterCal = pulsos / vol;
        bool aplicar = await C.Confirmar("Resultado de calibración",
            "· Pulsos: " + FxUi.Int(pulsos) + "\n"
          + "· Volumen: " + FxUi.Num(vol, 2) + " L\n"
          + "· Calibración calculada: " + FxUi.Num(meterCal, 2) + " pulsos/L\n\n"
          + "¿Aplicar a la reguladora \"" + nombreP + "\"?").ConfigureAwait(true);
        if (!aplicar) return;

        p.MeterCal = Math.Round(meterCal * 100.0) / 100.0;
        C.RebuildTab?.Invoke();
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
            "Calibración aplicada. Tocá Guardar para que quede."), "ok");
    }

    // =======================================================================
    //  Auto-tune PID
    // =======================================================================

    private async Task AutotuneAsync(FlowXNodoConfig n, FlowXProducto p)
    {
        if (C.Confirmar == null || C.Alertar == null) return;
        string uid = n.Uid ?? "";
        if (string.IsNullOrEmpty(uid)) return;
        string nombreP = string.IsNullOrEmpty(p.Nombre) ? p.Id.ToString(CultureInfo.InvariantCulture) : p.Nombre!;

        bool ok = await C.Confirmar("Auto-tune PID",
            "El firmware va a aplicar escalones al motor de la bomba durante unos 30 s. "
          + "Asegurate de que el sistema esté presurizado y la barra cerrada.\n\n"
          + "Reguladora: " + nombreP + "\n\n¿Continuar?").ConfigureAwait(true);
        if (!ok) return;

        var start = await C.Client.SendCmdAsync(uid, "autotune_start",
            new { producto_id = p.Id, setpoint_hz = 5.0, pwm_high = 4095, pwm_low = 200 }, C.Ct)
            .ConfigureAwait(true);
        if (!start.Ok)
        {
            await C.Alertar("Error de inicio", "No se pudo iniciar el auto-tune: " + start.Texto())
                .ConfigureAwait(true);
            return;
        }

        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Auto-tune en curso…"), "");
        var r = await C.Client.PollResultAsync(uid, "autotune", 45000, C.Ct).ConfigureAwait(true);
        C.Estado?.Invoke("", "");
        if (r == null)
        {
            await C.Alertar("Auto-tune falló", "Se agotó la espera de respuesta del nodo.")
                .ConfigureAwait(true);
            return;
        }
        if (!FxJson.Bool(r.Value, "ok"))
        {
            await C.Alertar("Auto-tune falló",
                FxJson.Str(r.Value, "error", "Sin oscilación detectada.")).ConfigureAwait(true);
            return;
        }

        double kp = FxJson.Num(r.Value, "kp") ?? 0;
        double ki = FxJson.Num(r.Value, "ki") ?? 0;
        double kd = FxJson.Num(r.Value, "kd") ?? 0;
        double? ku = FxJson.Num(r.Value, "ku");
        double? tu = FxJson.Num(r.Value, "tu_ms");
        string msg = "· Kp = " + FxUi.Num(kp, 3) + "\n· Ki = " + FxUi.Num(ki, 3)
                   + "\n· Kd = " + FxUi.Num(kd, 3);
        if (ku.HasValue && ku.Value != 0) msg += "\n· Ku = " + FxUi.Num(ku.Value, 3);
        if (tu.HasValue && tu.Value != 0) msg += "\n· Tu = " + FxUi.Int(tu.Value) + " ms";
        msg += "\n\n¿Aplicar a la reguladora \"" + nombreP + "\"?";

        bool aplicar = await C.Confirmar("Auto-tune OK", msg).ConfigureAwait(true);
        if (!aplicar) return;
        p.Kp = kp; p.Ki = ki; p.Kd = kd;
        C.RebuildTab?.Invoke();
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
            "PID aplicado. Tocá Guardar para que quede."), "ok");
    }

    // =======================================================================
    //  Barrido automatico (caracterizacion)
    // =======================================================================

    private async Task CaracterizarAsync(FlowXNodoConfig n, FlowXProducto p)
    {
        if (C.Confirmar == null || C.Alertar == null) return;
        string uid = n.Uid ?? "";
        if (string.IsNullOrEmpty(uid)) return;
        string nombreP = string.IsNullOrEmpty(p.Nombre) ? p.Id.ToString(CultureInfo.InvariantCulture) : p.Nombre!;

        bool ok = await C.Confirmar("Detectar PWM mínimo",
            "El nodo va a barrer el PWM desde 0 hasta 4095 midiendo el caudal. Así descubre el PWM "
          + "real al que arranca la bomba y el caudal máximo.\n\n"
          + "Requisitos: bomba cebada con agua circulando, llave general (master) abierta y al menos "
          + "una sección abierta.\n\nReguladora: " + nombreP + "\n\n¿Empezar?").ConfigureAwait(true);
        if (!ok) return;

        // Sin este DELETE se lee una corrida VIEJA como si fuera nueva y se
        // aplica un pwm_min falso. Un fallo de limpieza no bloquea el start.
        await C.Client.ClearCaracterizarAsync(uid, C.Ct).ConfigureAwait(true);

        var start = await C.Client.SendCmdAsync(uid, "caracterizar_start",
            new { producto_id = p.Id }, C.Ct).ConfigureAwait(true);
        if (!start.Ok)
        {
            await C.Alertar("Error de inicio",
                "No se pudo iniciar la caracterización: " + start.Texto()).ConfigureAwait(true);
            return;
        }

        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Barrido en curso…"), "");
        var r = await C.Client.PollResultAsync(uid, "caracterizar", 60000, C.Ct).ConfigureAwait(true);
        C.Estado?.Invoke("", "");
        if (r == null)
        {
            await C.Alertar("Caracterización falló", "Se agotó la espera de respuesta del nodo.")
                .ConfigureAwait(true);
            return;
        }
        if (!FxJson.Bool(r.Value, "ok"))
        {
            await C.Alertar("Caracterización falló",
                FxJson.Str(r.Value, "error", "El firmware no completó el barrido.")).ConfigureAwait(true);
            return;
        }

        double? pwmMin  = FxJson.Num(r.Value, "pwm_min");
        double? pwmEst  = FxJson.Num(r.Value, "pwm_min_estable");
        double? hzMax   = FxJson.Num(r.Value, "hz_max");
        double? lminMax = FxJson.Num(r.Value, "lmin_max");
        string nd = PilotX.Cockpit.Bars.Traductor.T("n/d");
        string msg =
            "· PWM de arranque: " + (pwmMin.HasValue ? FxUi.Int(pwmMin.Value) : nd) + "\n"
          + "· PWM mínimo estable: " + (pwmEst.HasValue ? FxUi.Int(pwmEst.Value) : nd) + "\n"
          + "· Hz máximo: " + (hzMax.HasValue ? FxUi.Num(hzMax.Value, 1) : nd) + "\n"
          + "· L/min máximo: " + (lminMax.HasValue ? FxUi.Num(lminMax.Value, 2) : nd) + "\n\n"
          + "¿Aplicar el PWM mínimo estable como PWM mín de la reguladora \"" + nombreP + "\"?";

        bool aplicar = await C.Confirmar("Resultado del barrido", msg).ConfigureAwait(true);
        if (!aplicar || !pwmEst.HasValue || !(pwmEst.Value > 0)) return;
        p.PwmMin = (int)Math.Round(pwmEst.Value);
        C.RebuildTab?.Invoke();
        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
            "PWM mínimo aplicado. Tocá Guardar para que quede."), "ok");
    }
}

/// <summary>Lectura tolerante del JSON crudo que emite el firmware (los
/// resultados de calibrar/autotune/caracterizar no tienen DTO tipado: la curva
/// del barrido viaja inline sin re-serializar).</summary>
public static class FxJson
{
    public static double? Num(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    public static bool Bool(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (!e.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind == JsonValueKind.True;
    }

    public static string Str(JsonElement e, string prop, string fallback)
    {
        if (e.ValueKind != JsonValueKind.Object) return fallback;
        if (!e.TryGetProperty(prop, out var v)) return fallback;
        if (v.ValueKind != JsonValueKind.String) return fallback;
        var s = v.GetString();
        return string.IsNullOrEmpty(s) ? fallback : s!;
    }
}
