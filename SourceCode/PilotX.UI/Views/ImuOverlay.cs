// ============================================================================
// ImuOverlay.cs — visor de IMU en la pantalla principal (pedido 2026-08-07,
// probando el CoreX ECU real: "hace falta el visor del IMU en la pantalla").
//
// Panel chico arriba a la izquierda del mapa: rumbo / rolido / cabeceo vivos
// y un punto de estado (verde = IMU presente, rojo = ECU sin IMU, gris = sin
// ECU). Nativo puro, mismo estilo que el overlay de QuantiX — nada de
// WebView en el hot path de operación.
//
// Fuente: /api/corex-ecu/status (el proxy de PilotX al Teensy) a 2 Hz. Es la
// única fuente con el IMU completo — el HUD (/api/aog/state) no trae IMU.
// Si el ECU no contesta, el panel lo dice en gris; no desaparece, porque
// "no hay IMU" es exactamente lo que el operario necesita ver (el piloto
// sin IMU banquea distinto).
// ============================================================================

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PilotX.Desktop.Views;

public sealed class ImuOverlay : Border
{
    private static readonly IBrush BgPanel   = new SolidColorBrush(Color.Parse("#F2101612"));
    private static readonly IBrush TextoClaro= new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush TextoDim  = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush Verde     = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo      = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush Gris      = new SolidColorBrush(Color.Parse("#535E54"));

    private readonly Ellipse _punto;
    private readonly TextBlock _rumbo, _rolido, _cabeceo, _estado;

    private HttpClient? _http;
    private string _base = "";
    private CancellationTokenSource? _cts;

    private sealed class Ellipse : Control
    {
        public IBrush Relleno = Brushes.Gray;
        public override void Render(DrawingContext ctx)
        {
            ctx.DrawEllipse(Relleno, null, new Point(Bounds.Width / 2, Bounds.Height / 2),
                            Bounds.Width / 2, Bounds.Height / 2);
        }
    }

    public ImuOverlay()
    {
        Background = BgPanel;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 7);
        IsVisible = true;
        IsHitTestVisible = false;   // solo lectura: los toques pasan al mapa

        _punto = new Ellipse { Width = 9, Height = 9, Relleno = Gris };

        var titulo = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titulo.Children.Add(_punto);
        titulo.Children.Add(new TextBlock
        {
            Text = "IMU", FontSize = 11, FontWeight = FontWeight.Bold,
            Foreground = TextoDim, VerticalAlignment = VerticalAlignment.Center
        });
        _estado = new TextBlock
        {
            Text = "", FontSize = 11, Foreground = TextoDim,
            VerticalAlignment = VerticalAlignment.Center
        };
        titulo.Children.Add(_estado);

        TextBlock Valor() => new TextBlock
        {
            Text = "—", FontSize = 16, FontWeight = FontWeight.Bold,
            Foreground = TextoClaro,
            FontFamily = new FontFamily("Consolas,monospace"),
        };
        TextBlock Cap(string t) => new TextBlock
        {
            Text = t, FontSize = 9, Foreground = TextoDim
        };

        _rumbo = Valor(); _rolido = Valor(); _cabeceo = Valor();

        var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        StackPanel Celda(string cap, TextBlock v)
        {
            var p = new StackPanel();
            p.Children.Add(v);
            p.Children.Add(Cap(cap));
            return p;
        }
        // Términos del rubro: rumbo / rolido / cabeceo (nunca yaw/roll/pitch
        // al operario). Se traducen por el diccionario al estar en el árbol.
        fila.Children.Add(Celda("RUMBO", _rumbo));
        fila.Children.Add(Celda("ROLIDO", _rolido));
        fila.Children.Add(Celda("CABECEO", _cabeceo));

        var raiz = new StackPanel { Spacing = 2 };
        raiz.Children.Add(titulo);
        raiz.Children.Add(fila);
        Child = raiz;
    }

    public void Attach(HttpClient http, string baseUrl)
    {
        _http = http;
        _base = baseUrl.TrimEnd('/');
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = PollAsync(_cts.Token);
    }

    public void Detach()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool ok = false, presente = false;
            double yaw = 0, roll = 0, pitch = 0;
            string modo = "";
            try
            {
                var json = await _http!.GetStringAsync(_base + "/api/corex-ecu/status", ct)
                                       .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                ok = r.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                if (ok && r.TryGetProperty("imu", out var imu))
                {
                    presente = imu.TryGetProperty("present", out var p) && p.GetBoolean();
                    if (imu.TryGetProperty("mode", out var m)) modo = m.GetString() ?? "";
                    if (imu.TryGetProperty("yaw_deg", out var y)) yaw = y.GetDouble();
                    if (imu.TryGetProperty("roll_deg", out var ro)) roll = ro.GetDouble();
                    if (imu.TryGetProperty("pitch_deg", out var pi)) pitch = pi.GetDouble();
                }
            }
            catch { /* sin proxy/ECU: se pinta gris abajo */ }

            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ok && presente)
                    {
                        _punto.Relleno = Verde;
                        _estado.Text = modo.Length > 0 ? "· " + modo : "";
                        _rumbo.Text   = yaw.ToString("0.0") + "°";
                        _rolido.Text  = roll.ToString("0.0") + "°";
                        _cabeceo.Text = pitch.ToString("0.0") + "°";
                        _rumbo.Foreground = _rolido.Foreground = _cabeceo.Foreground = TextoClaro;
                    }
                    else
                    {
                        // Distinguir "ECU sin IMU" (rojo: el módulo está pero el
                        // sensor no) de "sin ECU" (gris: no hay módulo que mirar).
                        _punto.Relleno = ok ? Rojo : Gris;
                        _estado.Text = ok
                            ? PilotX.Cockpit.Bars.Traductor.T("sin sensor")
                            : PilotX.Cockpit.Bars.Traductor.T("sin ECU");
                        _rumbo.Text = _rolido.Text = _cabeceo.Text = "—";
                        _rumbo.Foreground = _rolido.Foreground = _cabeceo.Foreground = TextoDim;
                    }
                    _punto.InvalidateVisual();
                });
            }
            catch (OperationCanceledException) { break; }

            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
