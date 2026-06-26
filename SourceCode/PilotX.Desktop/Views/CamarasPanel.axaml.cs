// CamarasPanel.axaml.cs
//
// Reemplazo nativo (parcial) de pages/camaras.html. Tab Monitor only:
// muestra hasta 4 camaras activas en layouts 1x1 / 2x1 / 2x2 con snapshot
// polling JPEG over HTTP. El tab Config sigue en HTML (formulario IP/
// usuario/clave por camara — necesita teclado virtual).
//
// Cadence: config.refrescoMs (default 1000ms). Solo se polea la camara
// visible (en 1x1: la enfocada; en 2x1: las 2 primeras; en 2x2: las 4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class CamarasPanel : UserControl
{
    private CamarasClient? _client;
    private CancellationTokenSource? _cts;
    private CamarasConfig? _config;
    private string _layout = "2x2";    // 1x1 | 2x1 | 2x2
    private int _focusIdx = -1;        // idx absoluto (sobre _config.Camaras) cuando layout=1x1

    // Brushes cacheados.
    private static readonly IBrush BrushOk      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush BrushWarn    = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush BrushErr     = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush BrushDim     = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush BrushMid     = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush BrushHi      = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush BrushBgHigh  = new SolidColorBrush(Color.Parse("#1A2520"));
    private static readonly IBrush BrushAccent  = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush BrushBorder  = new SolidColorBrush(Color.Parse("#26302A"));
    private static readonly IBrush BrushTileBg  = new SolidColorBrush(Color.Parse("#0E1612"));

    public Action? OnRequestConfigurar { get; set; }

    public CamarasPanel()
    {
        InitializeComponent();
        PaintLayoutButtons();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Attach(CamarasClient client)
    {
        _client = client;
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = LoadConfigAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _config = null;
        // Limpio frames cargados para liberar bitmap memory.
        var grid = this.FindControl<UniformGrid>("CamGrid");
        if (grid != null) grid.Children.Clear();
    }

    private async Task LoadConfigAsync(CancellationToken ct)
    {
        if (_client == null) return;
        SetStatus("warn", "Cargando...");
        var resp = await _client.GetConfigAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (resp == null || !resp.Ok || resp.Config == null)
            {
                _config = new CamarasConfig { Camaras = new List<CamaraDto>(), RefrescoMs = 1000 };
                SetStatus("err", resp?.Error ?? "Sin conexion al servicio de camaras");
                ShowEmptyState(true);
                return;
            }
            _config = resp.Config;
            BuildGrid();
        });

        if (ct.IsCancellationRequested) return;
        // Arranca el loop de snapshots.
        await SnapshotLoopAsync(ct).ConfigureAwait(false);
    }

    private void BuildGrid()
    {
        var grid = this.FindControl<UniformGrid>("CamGrid");
        var empty = this.FindControl<TextBlock>("EmptyMsg");
        var sub = this.FindControl<TextBlock>("Subtitle");
        if (grid == null) return;
        grid.Children.Clear();

        var cams = _config?.Camaras ?? new List<CamaraDto>();
        var activas = new List<(int idx, CamaraDto cam)>();
        for (int i = 0; i < cams.Count; i++)
        {
            var c = cams[i];
            if (c != null && c.Activa) activas.Add((i, c));
        }

        if (sub != null)
            sub.Text = cams.Count.ToString(CultureInfo.InvariantCulture)
                     + " configurada" + (cams.Count != 1 ? "s" : "")
                     + " · " + activas.Count.ToString(CultureInfo.InvariantCulture)
                     + " activa" + (activas.Count != 1 ? "s" : "");

        if (activas.Count == 0)
        {
            ShowEmptyState(true);
            SetStatus("warn", "Sin camaras activas");
            return;
        }

        ShowEmptyState(false);
        SetStatus("ok", activas.Count.ToString(CultureInfo.InvariantCulture) +
                        " camara" + (activas.Count != 1 ? "s" : "") +
                        " activa" + (activas.Count != 1 ? "s" : ""));

        // Determino el layout vigente: si solo hay 1 activa, fuerzo 1x1; si 2, max 2x1.
        if (activas.Count <= 1) _layout = "1x1";
        else if (activas.Count == 2 && _layout == "2x2") _layout = "2x1";
        ApplyLayoutGrid(grid, activas.Count);
        PaintLayoutButtons();

        // Si el focus es invalido para layout 1x1, fijo al primero activo.
        if (_layout == "1x1")
        {
            if (_focusIdx < 0 || !activas.Any(a => a.idx == _focusIdx))
                _focusIdx = activas[0].idx;
        }

        // Genero tiles.
        int maxTiles = _layout switch
        {
            "1x1" => 1,
            "2x1" => 2,
            _     => 4
        };

        IEnumerable<(int idx, CamaraDto cam)> visibles = _layout == "1x1"
            ? activas.Where(a => a.idx == _focusIdx)
            : activas.Take(maxTiles);

        foreach (var (idx, cam) in visibles)
        {
            grid.Children.Add(BuildTile(idx, cam));
        }
    }

    private static void ApplyLayoutGrid(UniformGrid grid, int activeCount)
    {
        // El layout viene del estado del usuario; pero respeta cap por # activas.
        // 1x1 = 1col,1row | 2x1 = 2col,1row | 2x2 = 2col,2row
        // El "_layout" externo ya fue ajustado en BuildGrid.
    }

    private Border BuildTile(int idx, CamaraDto cam)
    {
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = idx,
            Name = "Frame_" + idx.ToString(CultureInfo.InvariantCulture)
        };

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 10, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Ellipse
                    {
                        Width = 8, Height = 8,
                        Fill = BrushOk,
                        VerticalAlignment = VerticalAlignment.Center,
                        Name = "Dot_" + idx.ToString(CultureInfo.InvariantCulture)
                    },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(cam.Nombre)
                            ? "Camara " + (idx + 1).ToString(CultureInfo.InvariantCulture)
                            : cam.Nombre!,
                        Foreground = Brushes.White,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };

        var errMsg = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 10, 10),
            IsVisible = false,
            Name = "Err_" + idx.ToString(CultureInfo.InvariantCulture),
            Child = new TextBlock
            {
                Text = "Sin conexion con la camara",
                Foreground = BrushErr,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            }
        };

        var ph = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(cam.Ip) ? "..." : cam.Ip!,
            Foreground = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255)),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "Ph_" + idx.ToString(CultureInfo.InvariantCulture)
        };

        var content = new Grid();
        content.Children.Add(ph);
        content.Children.Add(img);
        content.Children.Add(label);
        content.Children.Add(errMsg);

        var tile = new Border
        {
            Background = BrushTileBg,
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(4),
            Tag = idx,
            ClipToBounds = true,
            Child = content
        };

        // Doble click -> toggle foco
        tile.DoubleTapped += (s, e) =>
        {
            if (_layout == "1x1")
            {
                _layout = "2x2";
                _focusIdx = -1;
            }
            else
            {
                _layout = "1x1";
                _focusIdx = idx;
            }
            BuildGrid();
        };

        return tile;
    }

    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        // Primer tick inmediato.
        await TickSnapshotsAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            int refresh = _config?.RefrescoMs ?? 1000;
            if (refresh < 300) refresh = 300;
            try { await Task.Delay(TimeSpan.FromMilliseconds(refresh), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickSnapshotsAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickSnapshotsAsync(CancellationToken ct)
    {
        if (_client == null || _config == null) return;
        // Recolecto idx de tiles visibles (los hijos del UniformGrid).
        var visibles = new List<int>();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var grid = this.FindControl<UniformGrid>("CamGrid");
            if (grid == null) return;
            foreach (var ch in grid.Children)
            {
                if (ch is Border b && b.Tag is int idx) visibles.Add(idx);
            }
        });

        foreach (var idx in visibles)
        {
            if (ct.IsCancellationRequested) return;
            var bytes = await _client.GetSnapshotAsync(idx, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() => ApplyFrame(idx, bytes));
        }
    }

    private void ApplyFrame(int idx, byte[]? bytes)
    {
        var img = this.FindControl<Image>("Frame_" + idx.ToString(CultureInfo.InvariantCulture));
        var err = this.FindControl<Border>("Err_" + idx.ToString(CultureInfo.InvariantCulture));
        var dot = this.FindControl<Ellipse>("Dot_" + idx.ToString(CultureInfo.InvariantCulture));
        var ph  = this.FindControl<TextBlock>("Ph_" + idx.ToString(CultureInfo.InvariantCulture));
        if (img == null) return;

        if (bytes == null || bytes.Length < 64)
        {
            // Fallo. Marco el tile como errored.
            if (err != null) err.IsVisible = true;
            if (dot != null) dot.Fill = BrushErr;
            img.Opacity = 0.15;
            return;
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            img.Source = bmp;
            img.Opacity = 1.0;
            if (err != null) err.IsVisible = false;
            if (dot != null) dot.Fill = BrushOk;
            if (ph  != null) ph.IsVisible = false;
        }
        catch
        {
            if (err != null) err.IsVisible = true;
            if (dot != null) dot.Fill = BrushErr;
        }
    }

    private void OnLayoutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string lay)
        {
            _layout = lay;
            // Si selecciono 1x1 sin focus, el primero activo gana (BuildGrid lo resuelve).
            BuildGrid();
        }
    }

    private void PaintLayoutButtons()
    {
        Paint("BtnLay1x1", "1x1");
        Paint("BtnLay2x1", "2x1");
        Paint("BtnLay2x2", "2x2");
    }

    private void Paint(string ctrlName, string lay)
    {
        var btn = this.FindControl<Button>(ctrlName);
        if (btn == null) return;
        bool active = _layout == lay;
        btn.Background = active ? BrushAccent : BrushBgHigh;
        btn.Foreground = active ? new SolidColorBrush(Color.Parse("#101612")) : BrushMid;
        btn.BorderBrush = active ? BrushAccent : BrushBorder;
        btn.BorderThickness = new Thickness(1);
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void ShowEmptyState(bool empty)
    {
        var grid = this.FindControl<UniformGrid>("CamGrid");
        var msg  = this.FindControl<TextBlock>("EmptyMsg");
        if (grid != null) grid.IsVisible = !empty;
        if (msg  != null) msg.IsVisible = empty;
    }

    private void SetStatus(string kind, string text)
    {
        var dot = this.FindControl<Ellipse>("StatusDot");
        var tb  = this.FindControl<TextBlock>("StatusText");
        if (dot != null)
            dot.Fill = kind switch
            {
                "ok"   => BrushOk,
                "warn" => BrushWarn,
                "err"  => BrushErr,
                _      => BrushDim
            };
        if (tb != null)
        {
            tb.Text = text;
            tb.Foreground = kind == "err" ? BrushErr : BrushHi;
        }
    }

    private void OnConfigurarClick(object? sender, RoutedEventArgs e)
    {
        OnRequestConfigurar?.Invoke();
    }
}
