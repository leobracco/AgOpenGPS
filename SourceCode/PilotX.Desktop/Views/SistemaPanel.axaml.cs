// SistemaPanel.axaml.cs
//
// Reemplazo nativo de pages/sistema.html. UI Avalonia + SistemaClient
// (HTTP a EmbedIO). Sin WebView, sin JS.
//
// Brillo: slider 0..100 debounced ~150ms, mas 5 quick presets.
// Power: tap-to-confirm inline (1er tap = arma, 2do tap en <5s = ejecuta).

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class SistemaPanel : UserControl
{
    private SistemaClient? _client;

    // Debounce del slider para no inundar al endpoint mientras el operario
    // arrastra. 150ms es suficiente para sentir respuesta fluida sin
    // bombear 60 requests/seg.
    private DispatcherTimer? _brilloDebounce;
    private int _pendingBrillo = -1;
    private bool _initialLoadDone;
    private bool _suppressSliderEvent;

    // Tap-to-confirm: nombre del action que esta armado (o null si no hay
    // ninguno). El timeout lo revierte si el operario no confirma a tiempo.
    private string? _armedAction;
    private DispatcherTimer? _armTimeout;
    private const int ArmTimeoutSeconds = 5;

    // Brushes para feedback visual del estado "armado".
    private static readonly IBrush _armBg     = new SolidColorBrush(Color.Parse("#332016"));
    private static readonly IBrush _idleBgMid = new SolidColorBrush(Color.Parse("#1A1F1B"));

    public SistemaPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Inyecta el cliente y dispara la carga inicial del brillo. Llamado por
    /// MainWindow la primera vez que se abre el panel (lazy: si nunca se
    /// abre, nunca se hace la request).
    /// </summary>
    public void Attach(SistemaClient client)
    {
        _client = client;
        if (!_initialLoadDone)
        {
            _initialLoadDone = true;
            _ = LoadBrilloAsync();
        }
    }

    /// <summary>
    /// Cancela el tap-armado y resetea labels. Lo llama MainWindow al cerrar
    /// el panel: si el operario abrio "Apagar" sin confirmar y cerro el
    /// panel, no queremos que la proxima apertura lo encuentre armado.
    /// </summary>
    public void Reset()
    {
        DisarmAll();
    }

    // ---------- Brillo --------------------------------------------------

    private async Task LoadBrilloAsync()
    {
        if (_client == null) return;
        var status = this.FindControl<TextBlock>("BrilloStatus");
        if (status != null) status.Text = "Cargando...";
        int v = await _client.GetBrightnessAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (v < 0)
            {
                if (status != null) status.Text = "No se detecto control de brillo (DDC/CI ni WMI).";
                return;
            }
            SetSliderUi(v);
            if (status != null) status.Text = "Brillo actual: " + v.ToString(CultureInfo.InvariantCulture) + "%";
        });
    }

    private void SetSliderUi(int v)
    {
        _suppressSliderEvent = true;
        var slider = this.FindControl<Slider>("BrilloSlider");
        var value  = this.FindControl<TextBlock>("BrilloValue");
        if (slider != null) slider.Value = v;
        if (value  != null) value.Text   = v.ToString(CultureInfo.InvariantCulture);
        _suppressSliderEvent = false;
    }

    private void OnBrilloSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderEvent) return;
        int v = (int)Math.Round(e.NewValue);
        var value = this.FindControl<TextBlock>("BrilloValue");
        if (value != null) value.Text = v.ToString(CultureInfo.InvariantCulture);
        _pendingBrillo = v;
        // Lazy init del timer para no consumir DispatcherTimer si el operario
        // nunca toca el slider.
        if (_brilloDebounce == null)
        {
            _brilloDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _brilloDebounce.Tick += async (_, _) =>
            {
                _brilloDebounce!.Stop();
                if (_pendingBrillo >= 0)
                {
                    int target = _pendingBrillo;
                    _pendingBrillo = -1;
                    await ApplyBrilloAsync(target);
                }
            };
        }
        _brilloDebounce.Stop();
        _brilloDebounce.Start();
    }

    private void OnBrilloPreset(object? sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn?.Tag is not string tagStr) return;
        if (!int.TryParse(tagStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return;
        SetSliderUi(v);
        _ = ApplyBrilloAsync(v);
    }

    private async Task ApplyBrilloAsync(int v)
    {
        if (_client == null) return;
        var status = this.FindControl<TextBlock>("BrilloStatus");
        bool ok = await _client.SetBrightnessAsync(v).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (status == null) return;
            status.Text = ok
                ? "Brillo aplicado: " + v.ToString(CultureInfo.InvariantCulture) + "%"
                : "No se pudo aplicar (" + v.ToString(CultureInfo.InvariantCulture) + "%).";
        });
    }

    // ---------- Power (tap-to-confirm) ----------------------------------

    private void OnPowerClick(object? sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn?.Tag is not string action) return;

        if (_armedAction == action)
        {
            // Segunda tocada en <5s: ejecutar.
            DisarmAll();
            _ = ExecutePowerAsync(action);
            return;
        }

        // Primer tap o tap en otro card: rearmar.
        ArmAction(action);
    }

    private void ArmAction(string action)
    {
        DisarmAll();
        _armedAction = action;
        UpdateArmUi(action, armed: true);
        var status = this.FindControl<TextBlock>("PowerStatus");
        if (status != null) status.Text = "Toca de nuevo en " + ArmTimeoutSeconds + "s para confirmar.";

        _armTimeout?.Stop();
        _armTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ArmTimeoutSeconds) };
        _armTimeout.Tick += (_, _) =>
        {
            _armTimeout!.Stop();
            if (_armedAction == action)
            {
                DisarmAll();
                if (status != null) status.Text = "Cancelado (timeout).";
            }
        };
        _armTimeout.Start();
    }

    private void DisarmAll()
    {
        _armTimeout?.Stop();
        _armTimeout = null;
        if (_armedAction != null)
        {
            UpdateArmUi(_armedAction, armed: false);
            _armedAction = null;
        }
    }

    private void UpdateArmUi(string action, bool armed)
    {
        (Button? btn, TextBlock? title, TextBlock? desc, string baseTitle, string baseDesc) info = action switch
        {
            "shutdown" => (this.FindControl<Button>("BtnPowerShutdown"),
                           this.FindControl<TextBlock>("LblShutdownTitle"),
                           this.FindControl<TextBlock>("LblShutdownDesc"),
                           "Apagar la PC",
                           "Detiene PilotX y apaga el sistema."),
            "restart"  => (this.FindControl<Button>("BtnPowerRestart"),
                           this.FindControl<TextBlock>("LblRestartTitle"),
                           this.FindControl<TextBlock>("LblRestartDesc"),
                           "Reiniciar la PC",
                           "Detiene PilotX y reinicia el sistema."),
            "suspend"  => (this.FindControl<Button>("BtnPowerSuspend"),
                           this.FindControl<TextBlock>("LblSuspendTitle"),
                           this.FindControl<TextBlock>("LblSuspendDesc"),
                           "Suspender",
                           "Pone la PC en suspension (S3)."),
            "exitApp"  => (this.FindControl<Button>("BtnPowerExitApp"),
                           this.FindControl<TextBlock>("LblExitAppTitle"),
                           this.FindControl<TextBlock>("LblExitAppDesc"),
                           "Cerrar PilotX",
                           "Cierra solo la aplicacion."),
            _ => (null, null, null, "", "")
        };

        if (info.btn   != null) info.btn.Background = armed ? _armBg : _idleBgMid;
        if (info.title != null) info.title.Text     = armed ? ("Confirmar: " + info.baseTitle) : info.baseTitle;
        if (info.desc  != null) info.desc.Text      = armed
            ? "Tocá de nuevo para ejecutar."
            : info.baseDesc;
    }

    private async Task ExecutePowerAsync(string action)
    {
        if (_client == null) return;
        var status = this.FindControl<TextBlock>("PowerStatus");
        if (status != null) status.Text = "Enviando: " + action + "...";

        PowerAction pa;
        switch (action)
        {
            case "shutdown": pa = PowerAction.Shutdown; break;
            case "restart":  pa = PowerAction.Restart;  break;
            case "suspend":  pa = PowerAction.Suspend;  break;
            case "exitApp":  pa = PowerAction.ExitApp;  break;
            default: return;
        }

        bool ok = await _client.PowerAsync(pa).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (status == null) return;
            status.Text = ok
                ? "Accion enviada: " + action
                : "Fallo: " + action + " (la PC no respondio ok).";
        });
    }
}
