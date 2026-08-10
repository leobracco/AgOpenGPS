// ============================================================================
// TractorRolidoOverlay.cs — tractor visto de atrás, CHIQUITO, abajo a la
// derecha del mapa (pedido 2026-08-10): se inclina en vivo con el ROLIDO QUE
// USA EL GUIADO y muestra el ángulo al lado.
//
// Fuente: /api/aog/graph-correction → roll_degrees (con cero e inversión ya
// aplicados por el motor — la MISMA verdad que el visor de calibración de
// Config → GPS/IMU → Rolido). Si el motor no tiene roll (sin PANDA), el
// tractor queda derecho y el ángulo en "—" gris: eso también es dato.
//
// Solo lectura (IsHitTestVisible=false): los toques pasan al mapa. Patrón
// ImuOverlay: nativo puro, poll 2 Hz, siempre visible.
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

public sealed class TractorRolidoOverlay : Border
{
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#F2101612"));
    private static readonly IBrush TextoClaro = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Gris       = new SolidColorBrush(Color.Parse("#535E54"));

    private readonly RotateTransform _rot = new(0);
    private readonly TextBlock _angulo;

    private HttpClient? _http;
    private string _base = "";
    private CancellationTokenSource? _cts;

    public TractorRolidoOverlay()
    {
        Background = BgPanel;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(8, 6);
        IsVisible = true;
        IsHitTestVisible = false;   // solo lectura: los toques pasan al mapa

        // tractor de atrás, mini (66x42)
        var tractor = new Canvas { Width = 66, Height = 42 };
        void Rect(double x, double y, double w, double h, IBrush b, double rad = 2)
        {
            var r = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = w, Height = h, Fill = b, RadiusX = rad, RadiusY = rad,
            };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            tractor.Children.Add(r);
        }
        Rect(7,  26, 11, 13, TextoClaro, 2);   // rueda izquierda
        Rect(48, 26, 11, 13, TextoClaro, 2);   // rueda derecha
        Rect(4,  20, 58, 8,  Verde, 2);        // eje/cuerpo
        Rect(19, 2,  28, 19, Verde, 3);        // cabina
        Rect(24, 5,  18, 9,  BgPanel, 1);      // vidrio
        tractor.RenderTransform = _rot;
        tractor.RenderTransformOrigin = new RelativePoint(0.5, 0.8, RelativeUnit.Relative);

        // línea de piso fija (referencia del horizonte)
        var piso = new Border
        {
            Height = 1, Background = Gris, Width = 66,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1),
        };
        var marco = new Panel();
        marco.Children.Add(piso);
        marco.Children.Add(tractor);

        _angulo = new TextBlock
        {
            Text = "—", FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = TextoDim, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontFamily = new FontFamily("Consolas,monospace"),
        };

        var fila = new StackPanel { Orientation = Orientation.Horizontal };
        fila.Children.Add(marco);
        fila.Children.Add(_angulo);
        Child = fila;
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
            bool rollOk = false;
            double roll = 0;
            try
            {
                var json = await _http!.GetStringAsync(_base + "/api/aog/graph-correction", ct)
                                       .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                rollOk = r.TryGetProperty("roll_present", out var rp) && rp.GetBoolean();
                if (rollOk && r.TryGetProperty("roll_degrees", out var rd)) roll = rd.GetDouble();
            }
            catch { /* motor ocupado: tractor derecho y "—" */ }

            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (rollOk)
                    {
                        _rot.Angle = Math.Max(-30, Math.Min(30, roll));
                        _angulo.Text = (roll > 0 ? "+" : "") + roll.ToString("0.0") + "°";
                        _angulo.Foreground = TextoClaro;
                    }
                    else
                    {
                        _rot.Angle = 0;
                        _angulo.Text = "—";
                        _angulo.Foreground = TextoDim;
                    }
                });
            }
            catch (OperationCanceledException) { break; }

            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
