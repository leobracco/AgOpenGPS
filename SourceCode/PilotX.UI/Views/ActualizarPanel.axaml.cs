// ActualizarPanel.axaml.cs
//
// Reemplazo nativo de pages/actualizar.html. Polling 1s a
// /api/pilotx/update/status mientras el panel esta visible; al cerrar el
// loop se detiene. Las acciones POST (check/download/apply) tienen su
// propio request fire-and-forget; la actualizacion del estado llega por
// el siguiente tick del polling.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class ActualizarPanel : UserControl
{
    private UpdateClient? _client;
    private CancellationTokenSource? _cts;
    private double _lastWidthScale = -1;

    // Brushes cacheados.
    private static readonly IBrush BrushAccent = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush BrushWarn   = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush BrushErr    = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush BrushDim    = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush BrushHi     = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush BrushBgDeep = new SolidColorBrush(Color.Parse("#101612"));

    public ActualizarPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Attach(UpdateClient client)
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
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var resp = await _client.GetStatusAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Render(resp?.Status));
    }

    private void Render(UpdateStatus? st)
    {
        if (st == null) return;

        SetText("VCurrent", string.IsNullOrWhiteSpace(st.CurrentVersion)   ? "—" : st.CurrentVersion!);
        SetText("VAvail",   string.IsNullOrWhiteSpace(st.AvailableVersion) ? "—" : st.AvailableVersion!);
        SetText("VSize",    FormatBytes(st.SizeBytes));
        SetText("VHash",    string.IsNullOrWhiteSpace(st.Sha256) ? "—"
                                                                  : (st.Sha256!.Length > 16 ? st.Sha256.Substring(0,16) + "…" : st.Sha256));
        SetText("VChecked", FormatTimestamp(st.LastCheckUnixMs));

        // Phase pill
        var phaseName = PhaseName(st.Phase);
        SetText("PhaseText", phaseName);
        var phasePill = this.FindControl<Border>("PhasePill");
        var phaseText = this.FindControl<TextBlock>("PhaseText");
        if (phasePill != null && phaseText != null)
        {
            switch (st.Phase)
            {
                case 1: case 3: case 5:
                    phasePill.Background = new SolidColorBrush(Color.Parse("#1F4ABA3E"));
                    phaseText.Foreground = BrushAccent;
                    break;
                case 2: case 4:
                    phasePill.Background = new SolidColorBrush(Color.Parse("#1FE2B53E"));
                    phaseText.Foreground = BrushWarn;
                    break;
                case 9:
                    phasePill.Background = new SolidColorBrush(Color.Parse("#1FE15A5A"));
                    phaseText.Foreground = BrushErr;
                    break;
                default:
                    phasePill.Background = BrushBgDeep;
                    phaseText.Foreground = BrushDim;
                    break;
            }
        }

        // Progress bar - solo redimensionar cuando cambia para no bombear layout.
        double pct = st.ProgressPct;
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        var progBar = this.FindControl<Border>("ProgBar");
        var progParent = progBar?.Parent as Border;
        if (progBar != null && progParent != null)
        {
            double scale = pct / 100.0;
            if (Math.Abs(scale - _lastWidthScale) > 0.001)
            {
                _lastWidthScale = scale;
                double parentW = progParent.Bounds.Width;
                if (parentW <= 0) parentW = 700;  // fallback inicial
                progBar.Width = parentW * scale;
            }
        }

        // Error line
        var errLine = this.FindControl<TextBlock>("ErrLine");
        if (errLine != null)
        {
            if (!string.IsNullOrWhiteSpace(st.LastError))
            {
                errLine.IsVisible = true;
                errLine.Text = "Error: " + st.LastError;
            }
            else
            {
                errLine.IsVisible = false;
                errLine.Text = "";
            }
        }

        // Changelog
        var chBox  = this.FindControl<Border>("ChangelogBox");
        var chText = this.FindControl<TextBlock>("ChangelogText");
        if (chBox != null && chText != null)
        {
            if (!string.IsNullOrWhiteSpace(st.Changelog))
            {
                chBox.IsVisible = true;
                chText.Text = st.Changelog;
            }
            else
            {
                chBox.IsVisible = false;
                chText.Text = "";
            }
        }

        // Botones segun fase (mismas reglas que actualizar.js)
        bool isBusy = (st.Phase == 1 || st.Phase == 3 || st.Phase == 5);
        SetEnabled("BtnCheck", !isBusy);
        SetEnabled("BtnDownload", !isBusy && (st.Phase == 2 || st.Phase == 9));
        SetEnabled("BtnApply", !isBusy && (st.Phase == 4 || st.StagingReady));
    }

    private static string PhaseName(int phase) => phase switch
    {
        0 => "Idle",
        1 => "Buscando",
        2 => "Actualizacion disponible",
        3 => "Descargando",
        4 => "Listo para aplicar",
        5 => "Aplicando",
        9 => "Error",
        _ => "Idle"
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0", CultureInfo.InvariantCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        double gb = mb / 1024.0;
        return gb.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    private static string FormatTimestamp(long unixMs)
    {
        if (unixMs <= 0) return "—";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
        return dt.ToString("HH:mm:ss  dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private void SetText(string name, string text)
    {
        var tb = this.FindControl<TextBlock>(name);
        if (tb != null) tb.Text = text;
    }

    private void SetEnabled(string name, bool enabled)
    {
        var btn = this.FindControl<Button>(name);
        if (btn != null) btn.IsEnabled = enabled;
    }

    private async void OnCheckClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        SetEnabled("BtnCheck", false);
        try { await _client.CheckAsync().ConfigureAwait(false); }
        catch { }
        // El polling se encarga de re-evaluar el boton.
    }

    private async void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        SetEnabled("BtnDownload", false);
        try { await _client.DownloadAsync().ConfigureAwait(false); }
        catch { }
    }

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        SetEnabled("BtnApply", false);
        SetEnabled("BtnCheck", false);
        SetEnabled("BtnDownload", false);
        try { await _client.ApplyAsync().ConfigureAwait(false); }
        catch { }
        // El backend lanza Updater.exe + mata PilotX. Si volvio (no aplico),
        // el polling actualiza el estado.
    }
}
