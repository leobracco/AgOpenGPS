// ============================================================================
// QxMediciones.cs — flujos que HACEN GIRAR FIERRO de verdad.
//
// QUÉ QUEDÓ NATIVO: "Medir tope / Medir Max Hz" (el pidMaxHzHandler del JS,
// que usan la tab Motores y la tab PID) y el selector de motor por chips.
// QUÉ SIGUE EN HTML: los mismos handlers en quantix.js, para la PWA.
//
// REGLA DURA: verb=test NO tiene meta — el motor gira hasta que alguien le
// manda stop. Todo camino de salida (excepción, cancelación, cierre del panel)
// tiene que terminar en stop. Por eso el finally está SIEMPRE y las tabs
// mandan stop también en AlSalirAsync().
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.QuantiXEditor;

public static class QxMediciones
{
    /// <summary>Sube el motor por rampa hasta PWM 4095, muestrea el pps real
    /// 4 s, para el motor y guarda el pico como max_hz (+ PUT + send).
    ///
    /// ARRANQUE POR RAMPA, no salto directo: un motor parado al que se le tira
    /// el PWM máximo de golpe no arranca. Medido en banco (2026-08-01, mismo
    /// motor, mismo PWM final): salto a 4095 → 134 Hz (13 rpm); rampa
    /// 600→4095 → 672 Hz (67 rpm). Con el tope mal medido el feedforward del
    /// PID arranca mal.</summary>
    public static async Task MedirMaxHzAsync(QxEditorCtx C, string uid, int mi,
                                             TextBlock msg, Button btn, CancellationToken ct)
    {
        if (!btn.IsEnabled) return;
        btn.IsEnabled = false;
        object? textoOrig = btn.Content;
        btn.Content = PilotX.Cockpit.Bars.Traductor.T("⏳ Midiendo…");
        QxUi.SetMsg(msg, "… motor a PWM máximo 4 s", "");

        double peak = 0;
        int pwmMax = 0, pwmMin = 4095, muestras = 0;
        try
        {
            var cfgM = C.FindMotor(uid, mi);
            int pwmIni = Math.Max(400, cfgM?.PwmMin > 0 ? cfgM.PwmMin : 600);
            QxUi.SetMsg(msg, "… subiendo el motor por rampa", "");
            for (int p = pwmIni; p < 4095; p += 250)
            {
                ct.ThrowIfCancellationRequested();
                await C.Client.TestAsync(uid, mi, p, ct).ConfigureAwait(true);
                await Task.Delay(250, ct).ConfigureAwait(true);
            }
            await C.Client.TestAsync(uid, mi, 4095, ct).ConfigureAwait(true);
            QxUi.SetMsg(msg, "… midiendo a PWM máximo", "");

            // 20 muestras × 200 ms = 4 s, pidiendo el live FRESCO cada vez: la
            // caché del loop se refresca cada 500 ms y muestrearla 20 veces
            // devolvía 2 o 3 valores del arranque — así se midieron 133 Hz de
            // un motor que hace 695 (banco 2026-08-01).
            for (int k = 0; k < 20; k++)
            {
                await Task.Delay(200, ct).ConfigureAwait(true);
                var m = await C.Client.FetchLiveMotorAsync(uid, mi, ct).ConfigureAwait(true);
                if (m == null) continue;
                if (m.PpsReal > peak) peak = m.PpsReal;
                // Las 2 primeras son arranque: el nodo puede no haber
                // procesado el start todavía.
                if (k >= 2)
                {
                    if (m.Pwm > pwmMax) pwmMax = m.Pwm;
                    if (m.Pwm < pwmMin) pwmMin = m.Pwm;
                    muestras++;
                }
            }
        }
        catch (OperationCanceledException) { /* el stop va en el finally igual */ }
        catch (Exception ex) { QxUi.SetMsg(msg, "✕ " + ex.Message, "err"); }
        finally
        {
            try { await C.Client.TestStopAsync(uid, mi, CancellationToken.None).ConfigureAwait(true); } catch { }
            btn.IsEnabled = true;
            btn.Content = textoOrig;
        }

        if (ct.IsCancellationRequested) return;

        if (peak < 1)
        {
            QxUi.SetMsg(msg, "✕ no se detectaron pulsos — revisá el sensor", "err");
            return;
        }
        // El nodo nunca sostuvo el PWM máximo: la medición no sirve.
        if (muestras > 0 && pwmMin < 3900)
        {
            QxUi.SetMsg(msg, "✕ el nodo no sostuvo el PWM: aplicó " + pwmMin + "–" + pwmMax
                           + " de 4095. No se guardó. Apagá la dosis/secciones y actualizá el firmware del nodo.", "err");
            return;
        }

        double maxHz = Math.Round(peak * 10) / 10.0;
        var motor = C.FindMotor(uid, mi);
        if (motor != null)
        {
            motor.MaxHz = maxHz;
            await C.GuardarAsync(ct).ConfigureAwait(true);
            await C.Client.SendNodoAsync(uid, ct).ConfigureAwait(true);
        }
        QxUi.SetMsg(msg, "✓ Max Hz = " + maxHz.ToString("0.0", CultureInfo.InvariantCulture) + " (aplicado)", "ok");
    }

    // =======================================================================
    //  Selector de motor (chips M0/M1/… + ⧉ Copiar a todos)
    // =======================================================================
    //
    // Mostrar todos los motores a la vez no escala: con 7 canales en tabla
    // queda ilegible. Se muestra UNO, a ancho completo. Lo que se pierde al
    // ver de a uno lo cubre "Copiar a todos".

    public static Control SelectorMotores(QxEditorCtx C, string tab, QxNodoConfig nodo,
                                          List<QxMotorConfig> ms, int activo,
                                          Action redibujar, Func<string, string, Task<bool>>? confirmar)
    {
        var fila = QxUi.Fila(6);
        fila.Margin = new Thickness(0, 0, 0, 8);

        for (int i = 0; i < ms.Count; i++)
        {
            var m = ms[i];
            int idx = i;
            bool on = i == activo;
            var contenido = QxUi.Fila(6);
            contenido.Children.Add(new Border
            {
                Background = on ? QxUi.Verde : QxUi.BgSuave,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = "M" + i, FontSize = 11, FontFamily = QxUi.Mono,
                    Foreground = on ? Brushes.White : QxUi.TextoMuted,
                },
            });
            contenido.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(m.Nombre) ? ("Motor " + (i + 1)) : m.Nombre,
                FontSize = 12, Foreground = QxUi.Texto,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var chip = new Button
            {
                Content = contenido,
                MinHeight = 42,
                Padding = new Thickness(8, 6, 12, 6),
                CornerRadius = new CornerRadius(8),
                Background = on ? QxUi.BgFilaSel : QxUi.BgFila,
                BorderBrush = on ? QxUi.Verde : QxUi.Borde,
                BorderThickness = new Thickness(1),
                // El canal deshabilitado se ve atenuado pero entra igual: hay
                // que poder configurarlo antes de conectarle el motor.
                Opacity = m.Habilitado ? 1.0 : 0.55,
            };
            if (!m.Habilitado)
                ToolTip.SetTip(chip, PilotX.Cockpit.Bars.Traductor.T("Canal sin motor conectado"));
            chip.Click += (_, __) => { C.MotorVista[tab] = idx; redibujar(); };
            fila.Children.Add(chip);
        }

        if (ms.Count > 1)
        {
            var copiar = QxUi.Boton("⧉ Copiar a todos", () => _ = CopiarATodosAsync(C, nodo, activo, redibujar, confirmar));
            ToolTip.SetTip(copiar, PilotX.Cockpit.Bars.Traductor.T(
                "Pone en TODOS los motores del nodo la misma configuración que este"));
            fila.Children.Add(copiar);
        }
        return fila;
    }

    private static async Task CopiarATodosAsync(QxEditorCtx C, QxNodoConfig nodo, int origen,
                                                Action redibujar, Func<string, string, Task<bool>>? confirmar)
    {
        bool ok = confirmar == null || await confirmar(
            "Copiar configuración",
            "¿Copiar la configuración de M" + origen + " a TODOS los motores de este nodo? "
            + "Se replican sensor, PWM, PID y calibración. NO se tocan el nombre, los surcos "
            + "asignados ni si el motor está conectado.").ConfigureAwait(true);
        if (!ok) return;

        var ms = nodo.Motores;
        if (ms == null || origen < 0 || origen >= ms.Count) return;
        var src = ms[origen];
        int copiados = 0;
        for (int k = 0; k < ms.Count; k++)
        {
            if (k == origen || ms[k] == null) continue;
            ms[k].CopiarFierroDesde(src);
            copiados++;
        }
        await C.GuardarAsync().ConfigureAwait(true);
        redibujar();
        C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Copiado a ") + copiados
                      + (copiados == 1 ? PilotX.Cockpit.Bars.Traductor.T(" motor.")
                                       : PilotX.Cockpit.Bars.Traductor.T(" motores.")));
    }
}
