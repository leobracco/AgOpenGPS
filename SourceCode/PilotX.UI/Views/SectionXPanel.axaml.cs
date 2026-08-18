// SectionXPanel.axaml.cs
//
// Reemplazo nativo (live-only) de pages/sectionx.html. Combina dos fuentes:
//   - HudSnapshot (PilotX a 4Hz): NumSections + SectionOnRequest[]
//   - SectionXStatus (HTTP a 1Hz): connected/running/nodoCount/lastPublishMsAgo
//
// El EDITOR (mapeo surcos->secciones, test de reles, debug MQTT) NO esta
// portado: el boton "Configurar" abre el WebView lazy sobre pages/sectionx.html.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class SectionXPanel : UserControl, IPanelEmbebible
{
    private SectionXClient? _client;
    private CancellationTokenSource? _cts;
    private SectionXStatus? _status;
    private HudSnapshot? _hud;

    // Callback opcional para que MainWindow abra el WebView lazy con la
    // pagina del Hub cuando el operario pide "Configurar".
    public Action? OnRequestConfigurar { get; set; }

    // Paleta CLARA de panel (tokens PilotXPanel* del theme). El semaforo va
    // con los tonos oscuros de cada color: sobre fondo claro el verde de marca
    // #4ABA3E da 2.5:1 y al sol no se lee. Medidos sobre blanco: verde 5.3:1,
    // ambar 5.5:1, rojo 5.9:1, gris 4.5:1, texto 18.3:1.
    private static readonly IBrush _brushOk     = new SolidColorBrush(Color.Parse("#2F7A26"));
    private static readonly IBrush _brushWarn   = new SolidColorBrush(Color.Parse("#8A6100"));
    private static readonly IBrush _brushErr    = new SolidColorBrush(Color.Parse("#C0261F"));
    private static readonly IBrush _brushDim    = new SolidColorBrush(Color.Parse("#6E7A70"));
    private static readonly IBrush _textHi      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush _textDim     = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush _bgOff       = new SolidColorBrush(Color.Parse("#EDF1EC"));
    private static readonly IBrush _borderOff   = new SolidColorBrush(Color.Parse("#C5CFC5"));
    // Seccion abierta: relleno verde de marca con texto OSCURO encima (7.3:1).
    private static readonly IBrush _fillOpen    = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _textOnFill  = new SolidColorBrush(Color.Parse("#101612"));

    public SectionXPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Adentro de la Configuración: sin marco de tarjeta y sin el
    /// título grande. La pill del bridge y el botón "Configurar" quedan en
    /// una fila compacta arriba del KPI; los márgenes de 24 bajan a 10.</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        var raiz = this.FindControl<StackPanel>("ContenidoRaiz");
        if (raiz != null) { raiz.Margin = new Thickness(10); raiz.Spacing = 12; }
    }

    /// <summary>Arranca el polling 1Hz al /api/sectionx/status.</summary>
    public void Attach(SectionXClient client)
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

    /// <summary>
    /// MainWindow pushea el HUD cuando el panel esta visible. El render real
    /// se hace en cada tick local (1Hz) para no redibujar la grilla a 4Hz.
    /// </summary>
    public void OnSnapshot(HudSnapshot s)
    {
        _hud = s;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Tick inmediato para no esperar 1s en abrir.
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_client != null)
        {
            var s = await _client.GetStatusAsync(ct).ConfigureAwait(false);
            _status = s; // null si fallo: se renderiza como "sin datos"
        }
        await Dispatcher.UIThread.InvokeAsync(Render);
    }

    private void Render()
    {
        var bridgeDot  = this.FindControl<Ellipse>("BridgeDot");
        var bridgeText = this.FindControl<TextBlock>("BridgeText");
        var kpiOpen    = this.FindControl<TextBlock>("KpiOpen");
        var kpiTotal   = this.FindControl<TextBlock>("KpiTotal");
        var host       = this.FindControl<ItemsControl>("SectionsHost");
        var emptyHint  = this.FindControl<TextBlock>("EmptyHint");

        // ---- Bridge chip: 4 estados verbatim del JS original ------------
        IBrush dot; string label;
        if (_status == null)
        {
            dot = _brushDim; label = "sin datos";
        }
        else if (!_status.Connected)
        {
            dot = _brushErr; label = "broker caido";
        }
        else if (!_status.Running || _status.NodoCount <= 0)
        {
            dot = _brushWarn; label = "sin nodos";
        }
        else if (_status.LastPublishMsAgo.HasValue && _status.LastPublishMsAgo.Value < 3000)
        {
            dot = _brushOk; label = "publicando";
        }
        else
        {
            dot = _brushWarn; label = "inactivo";
        }
        if (bridgeDot  != null) bridgeDot.Fill   = dot;
        if (bridgeText != null) bridgeText.Text  = label;

        // ---- KPIs + grilla de secciones ---------------------------------
        int total = _hud?.NumSections ?? 0;
        var arr   = _hud?.SectionOnRequest;
        int open  = 0;
        if (arr != null)
        {
            int lim = Math.Min(arr.Length, total);
            for (int i = 0; i < lim; i++) if (arr[i]) open++;
        }

        if (kpiOpen  != null) kpiOpen.Text  = total > 0 ? open.ToString(CultureInfo.InvariantCulture) : "--";
        if (kpiTotal != null) kpiTotal.Text = total > 0 ? "de " + total.ToString(CultureInfo.InvariantCulture) : "de --";

        if (host != null)
        {
            // Rebuild rapido: hasta 16 secciones, no vale la pena MVVM.
            host.Items.Clear();
            if (total <= 0)
            {
                if (emptyHint != null) emptyHint.IsVisible = true;
            }
            else
            {
                if (emptyHint != null) emptyHint.IsVisible = false;
                for (int i = 0; i < total; i++)
                {
                    bool on = arr != null && i < arr.Length && arr[i];
                    host.Items.Add(BuildSectionPill(i + 1, on));
                }
            }
        }
    }

    private static Control BuildSectionPill(int number, bool open)
    {
        var border = new Border
        {
            Background     = open ? _fillOpen   : _bgOff,
            BorderBrush    = open ? _brushOk    : _borderOff,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius   = new global::Avalonia.CornerRadius(8),
            Padding        = new global::Avalonia.Thickness(14, 8, 14, 8),
            Margin         = new global::Avalonia.Thickness(0, 0, 8, 8),
            MinWidth       = 72,
        };
        var sp = new StackPanel
        {
            Spacing             = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sp.Children.Add(new TextBlock
        {
            Text          = "Sec " + number.ToString(CultureInfo.InvariantCulture),
            Foreground    = open ? _textOnFill : _textDim,
            FontSize      = 11,
            FontWeight    = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text          = open ? "ABIERTA" : "cerrada",
            Foreground    = open ? _textOnFill : _textDim,
            FontSize      = 13,
            FontWeight    = open ? FontWeight.Bold : FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        border.Child = sp;
        return border;
    }

    private void OnConfigurarClick(object? sender, RoutedEventArgs e)
    {
        OnRequestConfigurar?.Invoke();
    }
}
