// ============================================================================
// DireccionPanel.CeroWasAuto.cs — "Cero automático del WAS" en
// Menú izquierdo › Dirección › Sensor.
//
// OPCIONAL y APAGADO de fábrica. El cero del WAS se calibra una vez y queda
// fijo: esto NUNCA lo cambia solo. Prendido, el motor mide el ángulo de las
// ruedas andando derecho con el piloto puesto y PROPONE un offset; el offset
// cambia únicamente cuando el operario toca "Aplicar" (con el piloto suelto),
// y queda "Deshacer".
//
//   GET  /api/steer/cero-was-auto           (cada 2 s con la pestaña a la vista)
//   POST /api/steer/cero-was-auto/activar   {"on":bool}
//   POST /api/steer/cero-was-auto/aplicar | /deshacer | /reiniciar
//
// Sin servicio en el motor (404 / service-unavailable) la sección no se muestra.
// Nada de popups: todo vive en la misma tarjeta de la pestaña.
// ============================================================================

using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PilotX.Desktop.Views;

public sealed partial class DireccionPanel
{
    private static readonly NumberFormatInfo EsAr = new NumberFormatInfo
    {
        // Coma decimal y punto de miles SIN depender de la cultura "es-AR":
        // PilotX.Desktop corre con InvariantGlobalization (crear es-AR ahí
        // tira CultureNotFoundException y voltea la pantalla). Mismo patrón
        // que TareaFormato.
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = new[] { 3 },
    };

    private int _cwTick;
    private bool _cwActivo;
    private StackPanel _cwSeccion = null!;
    private Button _cwToggle = null!;
    private Border _cwCard = null!;
    private TextBlock _cwEstado = null!;
    private TextBlock _cwDetalle = null!;
    private TextBlock _cwBloqueo = null!;
    private Button _cwAplicar = null!;
    private Button _cwDeshacer = null!;
    private Button _cwReiniciar = null!;

    private Control ConstruirCeroWasAuto()
    {
        _cwSeccion = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };

        _cwSeccion.Children.Add(SubTituloAyuda("Cero automático del WAS",
            "Opcional. Prendido, PilotX mira el ángulo de las ruedas mientras andás DERECHO con el piloto " +
            "puesto (guía recta, 4 a 20 km/h, a menos de 15 cm de la línea, sin girar) y calcula si el cero " +
            "quedó corrido. NUNCA cambia el cero solo: te PROPONE un valor y vos decidís con Aplicar " +
            "(con el piloto suelto). Después de aplicar queda Deshacer. Apagado no mide nada.", sep: true));

        _cwToggle = BotonSeg("Cero automático del WAS (propone, no aplica solo)");
        _cwToggle.Click += async (_, _) =>
            await CwPost("/api/steer/cero-was-auto/activar", "{\"on\":" + (_cwActivo ? "false" : "true") + "}");
        _cwSeccion.Children.Add(_cwToggle);

        _cwEstado = new TextBlock
        {
            FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Texto, TextWrapping = TextWrapping.Wrap,
        };
        _cwDetalle = new TextBlock
        {
            FontSize = 12, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        };
        _cwBloqueo = new TextBlock
        {
            FontSize = 12, Foreground = Ambar, TextWrapping = TextWrapping.Wrap, IsVisible = false,
            Margin = new Thickness(0, 4, 0, 0),
        };

        _cwAplicar = BotonSeg("Aplicar");
        _cwAplicar.Click += async (_, _) => await CwAccion("/api/steer/cero-was-auto/aplicar");
        _cwDeshacer = BotonSeg("Deshacer");
        _cwDeshacer.Click += async (_, _) => await CwAccion("/api/steer/cero-was-auto/deshacer");
        _cwReiniciar = BotonSeg("Medir de nuevo");
        _cwReiniciar.Click += async (_, _) => await CwPost("/api/steer/cero-was-auto/reiniciar", "{}");

        var botones = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
        botones.Children.Add(Envolver(_cwAplicar, 0, 3));
        botones.Children.Add(Envolver(_cwDeshacer, 3, 3));
        botones.Children.Add(Envolver(_cwReiniciar, 3, 0));

        var cuerpo = new StackPanel { Spacing = 0 };
        cuerpo.Children.Add(_cwEstado);
        cuerpo.Children.Add(_cwDetalle);
        cuerpo.Children.Add(_cwBloqueo);
        cuerpo.Children.Add(botones);

        _cwCard = new Border
        {
            Child = cuerpo, Background = BgCard, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), IsVisible = false,
        };
        _cwSeccion.Children.Add(_cwCard);
        return _cwSeccion;
    }

    // ---- HTTP ------------------------------------------------------------------

    private async Task CwRefrescar()
    {
        if (_http == null) return;
        try
        {
            var resp = await _http.GetAsync(_base + "/api/steer/cero-was-auto").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 = motor sin la función (Android, versión vieja): no se muestra.
                await Dispatcher.UIThread.InvokeAsync(() => _cwSeccion.IsVisible = false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var j = JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
            await Dispatcher.UIThread.InvokeAsync(() => CwRender(j, null));
        }
        catch { /* sin conexión: se deja lo último que se mostró */ }
    }

    /// <summary>Aplicar / Deshacer: cambian was_offset en el motor. Con ediciones
    /// sin guardar NO se hace: Guardar después pisaría el offset recién aplicado
    /// con el viejo que está en pantalla.</summary>
    private async Task CwAccion(string path)
    {
        if (_sucio)
        {
            _cwBloqueo.Text = "Guardá o descartá los cambios del panel antes de tocar el cero.";
            _cwBloqueo.IsVisible = true;
            return;
        }
        bool ok = await CwPost(path, "{}");
        // El offset cambió en el motor: releer la config para no pisarlo después.
        if (ok) await CargarConfig();
    }

    private async Task<bool> CwPost(string path, string json)
    {
        if (_http == null) return false;
        try
        {
            var resp = await _http.PostAsync(_base + path,
                new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonObject? j = null;
            try { j = JsonNode.Parse(body.TrimStart('﻿')) as JsonObject; } catch { }
            bool ok = resp.IsSuccessStatusCode && (j?["ok"]?.GetValue<bool>() ?? false);
            string? error = ok ? null : (j?["error"]?.GetValue<string>() ?? "http-" + (int)resp.StatusCode);
            await Dispatcher.UIThread.InvokeAsync(() => CwRender(j, error));
            return ok;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cwBloqueo.Text = "Sin conexión con PilotX: " + ex.Message;
                _cwBloqueo.IsVisible = true;
            });
            return false;
        }
    }

    // ---- render ------------------------------------------------------------------

    private void CwRender(JsonObject? j, string? error)
    {
        if (j == null) return;
        if (error == "service-unavailable") { _cwSeccion.IsVisible = false; return; }
        _cwSeccion.IsVisible = true;

        _cwActivo = j["activo"]?.GetValue<bool>() ?? false;
        PintarToggle(_cwToggle, _cwActivo);
        _cwToggle.Content = _cwActivo
            ? "Cero automático del WAS: prendido (propone, no aplica solo)"
            : "Cero automático del WAS (propone, no aplica solo)";
        _cwCard.IsVisible = _cwActivo;
        if (!_cwActivo) return;

        string estado = j["estado"]?.GetValue<string>() ?? "juntando";
        double seg = j["segundos"]?.GetValue<double>() ?? 0;
        double segReq = j["segundos_requeridos"]?.GetValue<double>() ?? 60;
        double sesgo = j["sesgo_grados"]?.GetValue<double>() ?? 0;
        double conf = j["confianza"]?.GetValue<double>() ?? 0;
        bool confAlta = j["confianza_alta"]?.GetValue<bool>() ?? false;
        int offAct = j["offset_actual"]?.GetValue<int>() ?? 0;
        int offProp = j["offset_propuesto"]?.GetValue<int>() ?? offAct;
        int offPrev = j["offset_previo"]?.GetValue<int>() ?? 0;
        bool condOk = j["condiciones_ok"]?.GetValue<bool>() ?? false;
        string? motivo = j["motivo"]?.GetValue<string>();
        bool puedeAplicar = j["puede_aplicar"]?.GetValue<bool>() ?? false;
        bool puedeDeshacer = j["puede_deshacer"]?.GetValue<bool>() ?? false;
        bool hayPropuesta = j["hay_propuesta"]?.GetValue<bool>() ?? false;
        string? bloqueo = j["bloqueo_aplicar"]?.GetValue<string>();

        string lado = sesgo > 0 ? "a la derecha" : "a la izquierda";
        string grados = Math.Abs(sesgo).ToString("0.0", EsAr) + "°";
        string confTxt = "Confianza " + (confAlta ? "alta" : "media") + " (" + conf.ToString("0", EsAr) + " %).";
        string pausa = condOk ? "Midiendo ahora." : "En pausa: " + MotivoTexto(motivo) + ".";

        _cwEstado.Foreground = Texto;
        switch (estado)
        {
            case "propuesta":
                _cwEstado.Text = "Tu cero parece corrido " + grados + " " + lado + " → offset propuesto "
                    + offProp.ToString(CultureInfo.InvariantCulture) + " (hoy " + offAct.ToString(CultureInfo.InvariantCulture) + ")";
                _cwEstado.Foreground = confAlta ? Verde : Texto;
                _cwDetalle.Text = confTxt + " No se cambia nada hasta que toques Aplicar.";
                break;
            case "cero_bien":
                _cwEstado.Text = "El cero está bien";
                _cwDetalle.Text = "Corrido " + grados + ", dentro de lo normal. " + pausa;
                break;
            case "inestable":
                _cwEstado.Text = "Los datos varían mucho: sigo midiendo";
                _cwDetalle.Text = "Todavía no hay un cero claro (confianza " + conf.ToString("0", EsAr) + " %). " + pausa;
                break;
            case "sesgo_excesivo":
                _cwEstado.Text = "Corrido más de 6°: no es un cero";
                _cwEstado.Foreground = Rojo;
                _cwDetalle.Text = "Revisá el montaje del sensor y calibralo con el Asistente.";
                break;
            case "fuera_de_rango":
                _cwEstado.Text = "El offset quedaría fuera de rango (±3900)";
                _cwEstado.Foreground = Rojo;
                _cwDetalle.Text = "Revisá el montaje del sensor.";
                break;
            default:
                _cwEstado.Text = "Juntando datos en recta: " + Math.Min(seg, segReq).ToString("0", EsAr)
                    + "/" + segReq.ToString("0", EsAr) + " s";
                _cwDetalle.Text = pausa + " Hace falta andar derecho con el piloto puesto.";
                break;
        }

        _cwAplicar.IsEnabled = puedeAplicar;
        PintarToggle(_cwAplicar, puedeAplicar);
        _cwDeshacer.IsEnabled = puedeDeshacer;
        _cwDeshacer.Content = puedeDeshacer
            ? "Deshacer (volver a " + offPrev.ToString(CultureInfo.InvariantCulture) + ")"
            : "Deshacer";

        // Por qué no deja aplicar (solo si hay algo para aplicar) o el error de la acción.
        string? aviso = null;
        if (error != null) aviso = ErrorTexto(error);
        else if (hayPropuesta && !puedeAplicar && bloqueo != null) aviso = ErrorTexto(bloqueo);
        _cwBloqueo.Text = aviso ?? "";
        _cwBloqueo.IsVisible = aviso != null;
        _cwBloqueo.Foreground = error != null ? Rojo : Ambar;
    }

    private static string MotivoTexto(string? m) => m switch
    {
        "piloto_suelto"     => "el piloto no está enganchado",
        "manejo_libre"      => "manejo libre prendido",
        "marcha_atras"      => "marcha atrás",
        "vuelta_u"          => "vuelta en U",
        "sin_angulo"        => "no llega el ángulo del sensor",
        "sin_guia"          => "sin guía",
        "guia_curva"        => "la guía es curva (hace falta una recta)",
        "velocidad"         => "velocidad fuera de 4–20 km/h",
        "lejos_de_la_linea" => "lejos de la línea (más de 15 cm)",
        "corrigiendo"       => "entrando a la línea",
        "girando"           => "el tractor está girando",
        "rolido"            => "mucha inclinación de costado",
        "angulo_grande"     => "las ruedas están muy giradas",
        "estabilizando"     => "esperando que se estabilice",
        _                   => "sin condiciones",
    };

    private static string ErrorTexto(string e) => e switch
    {
        "piloto-enganchado" => "Para aplicar, desenganchá el piloto (con el piloto puesto el cambio movería las ruedas de golpe).",
        "manejo-libre"      => "Apagá el manejo libre (pestaña Probar) para aplicar.",
        "asistente"         => "Hay un asistente de calibración en curso.",
        "muy-seguido"       => "Esperá unos segundos entre cambios del cero.",
        "sin-propuesta"     => "No hay una propuesta lista.",
        "offset-cambiado"   => "El cero cambió por otro lado (cero manual o Guardar): se vuelve a medir.",
        "fuera-de-rango"    => "El offset quedaría fuera de rango: revisá el montaje del sensor.",
        "sin-deshacer"      => "No hay nada para deshacer.",
        "apagado"           => "La función está apagada.",
        _                   => "No se pudo completar la acción (AGP-SYS-009).",
    };
}
