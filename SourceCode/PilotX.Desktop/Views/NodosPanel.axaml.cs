// NodosPanel.axaml.cs
//
// Reemplazo nativo (parcial) de pages/nodos.html. Polling 3s a
// /api/nodos/unified; renderiza tabs + tabla + banner alarma.
// Las acciones de curado (aceptar/ignorar/renombrar/restaurar + diag MQTT
// + wildcard + msg log) se delegan a la pagina HTML via boton "Configurar".
//
// API: Attach(NodosClient) prende el polling; Detach() lo apaga.
// OnRequestConfigurar Action -> MainWindow wirea a NavigateTo("pages/nodos.html").

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class NodosPanel : UserControl
{
    private NodosClient? _client;
    private CancellationTokenSource? _cts;
    private string _activeTab = "pendiente";
    private List<NodoUnified> _last = new List<NodoUnified>();

    // Brushes cacheados (palette cockpit)
    private static readonly IBrush BrushOk      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush BrushWarn    = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush BrushErr     = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush BrushErrBg   = new SolidColorBrush(Color.Parse("#C92D2D"));
    private static readonly IBrush BrushDim     = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush BrushMid     = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush BrushHi      = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush BrushBgHigh  = new SolidColorBrush(Color.Parse("#1A2520"));
    private static readonly IBrush BrushBgRow   = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush BrushAccent  = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush BrushBorder  = new SolidColorBrush(Color.Parse("#26302A"));

    /// <summary>Callback opcional para abrir nodos.html en WebView lazy.</summary>
    public Action? OnRequestConfigurar { get; set; }

    public NodosPanel()
    {
        InitializeComponent();
        PaintTabs();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Attach(NodosClient client)
    {
        _client = client;
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var resp = await _client.GetUnifiedAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Render(resp));
    }

    private void Render(NodosUnifiedResponse? resp)
    {
        _last = resp?.Nodos ?? new List<NodoUnified>();

        // ---- Broker pill ----
        var brokerDot = this.FindControl<Ellipse>("BrokerDot");
        var brokerTxt = this.FindControl<TextBlock>("BrokerText");
        bool conn = resp?.BrokerConnected ?? false;
        if (brokerDot != null) brokerDot.Fill = conn ? BrushOk : BrushErr;
        if (brokerTxt != null)
        {
            brokerTxt.Text = conn ? "Broker conectado" : "Broker desconectado";
            brokerTxt.Foreground = conn ? BrushHi : BrushErr;
        }

        // ---- Implemento pill ----
        var implTxt = this.FindControl<TextBlock>("ImplementoText");
        if (implTxt != null)
        {
            var slug = resp?.ImplementoSlug;
            implTxt.Text = string.IsNullOrWhiteSpace(slug)
                ? "Implemento: ninguno"
                : "Implemento: " + slug;
        }

        // ---- Alert banner (nodos del implemento activo offline) ----
        var offlineImpl = new List<NodoUnified>();
        foreach (var n in _last)
        {
            if (n.DelImplementoActivo && !n.Online) offlineImpl.Add(n);
        }
        var banner = this.FindControl<Border>("AlertBanner");
        var lista  = this.FindControl<TextBlock>("AlertLista");
        if (banner != null)
        {
            banner.IsVisible = offlineImpl.Count > 0;
            if (lista != null && offlineImpl.Count > 0)
            {
                var parts = new List<string>(offlineImpl.Count);
                foreach (var n in offlineImpl)
                {
                    var label = !string.IsNullOrWhiteSpace(n.Alias) ? n.Alias!
                              : !string.IsNullOrWhiteSpace(n.Uid)   ? n.Uid!
                                                                    : "?";
                    if (!string.IsNullOrWhiteSpace(n.Tipo)) label += " (" + n.Tipo + ")";
                    parts.Add(label);
                }
                lista.Text = string.Join("    ", parts);
            }
        }

        // ---- Counts en tabs ----
        int cP = 0, cA = 0, cO = 0, cI = 0;
        foreach (var n in _last)
        {
            switch ((n.Estado ?? "").ToLowerInvariant())
            {
                case "pendiente": cP++; break;
                case "aceptado":  if (n.Online) cA++; else cO++; break;
                case "offline":   cO++; break;
                case "ignorado":  cI++; break;
            }
        }
        UpdateTabLabel("TabPendiente", "Pendientes", cP);
        UpdateTabLabel("TabAceptado",  "Aceptados",  cA);
        UpdateTabLabel("TabOffline",   "Off-line",   cO);
        UpdateTabLabel("TabIgnorado",  "Ignorados",  cI);

        // ---- Filas filtradas ----
        var filas = this.FindControl<StackPanel>("FilasList");
        if (filas == null) return;
        filas.Children.Clear();

        var visibles = new List<NodoUnified>();
        foreach (var n in _last)
        {
            var est = (n.Estado ?? "").ToLowerInvariant();
            bool match = _activeTab switch
            {
                "pendiente" => est == "pendiente",
                "aceptado"  => est == "aceptado" && n.Online,
                "offline"   => est == "offline" || (est == "aceptado" && !n.Online),
                "ignorado"  => est == "ignorado",
                _ => false
            };
            if (match) visibles.Add(n);
        }

        if (visibles.Count == 0)
        {
            filas.Children.Add(new TextBlock
            {
                Text = "No hay nodos en esta vista.",
                Padding = new Thickness(14, 18, 14, 18),
                Foreground = BrushDim,
                FontSize = 12
            });
            return;
        }

        bool alt = false;
        foreach (var n in visibles)
        {
            filas.Children.Add(BuildRow(n, alt));
            alt = !alt;
        }
    }

    private Grid BuildRow(NodoUnified n, bool alt)
    {
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120,*,120,140,140,160"),
            Background = alt ? BrushBgHigh : BrushBgRow,
        };

        // Estado: dot + texto
        var estadoBox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 10, 8, 10)
        };
        var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        dot.Fill = n.Online ? BrushOk : BrushDim;
        var estadoTxt = new TextBlock
        {
            Text = n.Online ? "Online" : "Offline",
            Foreground = n.Online ? BrushHi : BrushDim,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        estadoBox.Children.Add(dot);
        estadoBox.Children.Add(estadoTxt);
        if (n.SafeMode)
        {
            estadoBox.Children.Add(MakeBadge("SAFE", BrushWarn));
        }
        if (!string.IsNullOrEmpty(n.BootReason) &&
            !string.Equals(n.BootReason, "poweron", StringComparison.OrdinalIgnoreCase))
        {
            estadoBox.Children.Add(MakeBadge(n.BootReason!, BrushErrBg));
        }
        Grid.SetColumn(estadoBox, 0);
        g.Children.Add(estadoBox);

        // Alias / UID
        var aliasBox = new StackPanel { Spacing = 2, Margin = new Thickness(12, 10, 8, 10) };
        aliasBox.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(n.Alias) ? "(sin alias)" : n.Alias!,
            Foreground = BrushHi,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold
        });
        aliasBox.Children.Add(new TextBlock
        {
            Text = n.Uid ?? "",
            Foreground = BrushDim,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        });
        Grid.SetColumn(aliasBox, 1);
        g.Children.Add(aliasBox);

        // Tipo
        var tipoPill = new Border
        {
            Background = BrushBgHigh,
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(12, 14, 8, 14),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(n.Tipo) ? "?" : n.Tipo!,
                Foreground = BrushMid,
                FontSize = 11
            }
        };
        Grid.SetColumn(tipoPill, 2);
        g.Children.Add(tipoPill);

        // IP
        var ipTxt = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(n.Ip) ? "—" : n.Ip!,
            Padding = new Thickness(12, 10, 8, 10),
            Foreground = BrushMid,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        Grid.SetColumn(ipTxt, 3);
        g.Children.Add(ipTxt);

        // FW
        var fwTxt = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(n.Firmware) ? "—" : n.Firmware!,
            Padding = new Thickness(12, 10, 8, 10),
            Foreground = BrushMid,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        Grid.SetColumn(fwTxt, 4);
        g.Children.Add(fwTxt);

        // Ultima senal
        var lastTxt = new TextBlock
        {
            Text = FormatLastSeen(n.LastSeenUtc),
            Padding = new Thickness(12, 10, 12, 10),
            Foreground = BrushMid,
            FontSize = 12
        };
        Grid.SetColumn(lastTxt, 5);
        g.Children.Add(lastTxt);

        return g;
    }

    private static Border MakeBadge(string text, IBrush bg)
    {
        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(7, 1, 7, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeight.Bold
            }
        };
    }

    private static string FormatLastSeen(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return iso!;
        var dt = DateTime.UtcNow - t;
        if (dt.TotalSeconds < 0) return "ahora";
        if (dt.TotalSeconds < 60) return ((int)dt.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
        if (dt.TotalMinutes < 60) return ((int)dt.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        if (dt.TotalHours   < 24) return ((int)dt.TotalHours).ToString(CultureInfo.InvariantCulture)   + " h";
        return ((int)dt.TotalDays).ToString(CultureInfo.InvariantCulture) + " d";
    }

    private void UpdateTabLabel(string ctrlName, string label, int count)
    {
        var btn = this.FindControl<Button>(ctrlName);
        if (btn == null) return;
        btn.Content = label + "  (" + count.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private void OnTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tab)
        {
            _activeTab = tab;
            PaintTabs();
            Render(new NodosUnifiedResponse { Nodos = _last });  // re-render con cache sin volver a pegarle al server
        }
    }

    private void PaintTabs()
    {
        Paint("TabPendiente", "pendiente");
        Paint("TabAceptado",  "aceptado");
        Paint("TabOffline",   "offline");
        Paint("TabIgnorado",  "ignorado");
    }

    private void Paint(string ctrlName, string tab)
    {
        var btn = this.FindControl<Button>(ctrlName);
        if (btn == null) return;
        bool active = _activeTab == tab;
        btn.Background = active ? BrushAccent : BrushBgHigh;
        btn.Foreground = active ? new SolidColorBrush(Color.Parse("#101612")) : BrushMid;
        btn.BorderBrush = active ? BrushAccent : BrushBorder;
        btn.BorderThickness = new Thickness(1);
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void OnConfigurarClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OnRequestConfigurar?.Invoke();
    }
}
