using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;
using WebViewCore.Events;

namespace PilotX.Desktop;

public partial class MainWindow : Window
{
    private WebView _webView;
    private Border _headerBar;
    private Border _bottomToolbar;
    private TextBlock _headerTitle;
    private TextBlock _headerSubtitle;
    private Border _rootBorder;

    public MainWindow()
    {
        InitializeComponent();

        Title = App.WindowTitle;

        _webView         = this.FindControl<WebView>("WebViewHost");
        _headerBar       = this.FindControl<Border>("HeaderBar");
        _bottomToolbar   = this.FindControl<Border>("BottomToolbar");
        _headerTitle     = this.FindControl<TextBlock>("HeaderTitle");
        _headerSubtitle  = this.FindControl<TextBlock>("HeaderSubtitle");
        _rootBorder      = this.FindControl<Border>("RootBorder");

        if (App.WindowMode == "float")
        {
            // Modo widget: borderless con chrome propio + arrastrabilidad
            // por el header. Encima de PilotX, deja ver el mapa detras.
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.Normal;
            Width  = App.WindowWidth  > 0 ? App.WindowWidth  : 640;
            Height = App.WindowHeight > 0 ? App.WindowHeight : 400;
            Topmost = true;
            CanResize = true;
            ShowInTaskbar = true;

            if (_headerBar     != null) _headerBar.IsVisible = true;
            if (_bottomToolbar != null) _bottomToolbar.IsVisible = false;
            if (_rootBorder    != null) _rootBorder.CornerRadius = new global::Avalonia.CornerRadius(10);
            if (_headerTitle   != null) _headerTitle.Text = App.WindowTitle ?? "PilotX";
            if (_headerSubtitle != null)
                _headerSubtitle.Text = DeriveSubtitleFromUrl(App.TargetUrl);

            // Posicion inicial: esquina superior-derecha (deja ver el piloto).
            WindowStartupLocation = WindowStartupLocation.Manual;
            try
            {
                var screen = Screens.Primary;
                if (screen != null)
                {
                    var wa = screen.WorkingArea;
                    Position = new global::Avalonia.PixelPoint(
                        wa.X + wa.Width - (int)Width - 20,
                        wa.Y + 20);
                }
            }
            catch { }
        }
        else
        {
            // Modo full (default) - Hub principal de cabina.
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.Maximized;
            if (_rootBorder != null)
            {
                _rootBorder.CornerRadius = new global::Avalonia.CornerRadius(0);
                _rootBorder.BorderThickness = new global::Avalonia.Thickness(0);
            }
            if (_headerBar     != null) _headerBar.IsVisible = false;
            // Toolbar inferior solo en modo full (los widgets float no la necesitan).
            if (_bottomToolbar != null) _bottomToolbar.IsVisible = true;
            // FAB X para cerrar (no hay header en modo full).
            var closeBtn = this.FindControl<Button>("CloseButton");
            if (closeBtn != null) closeBtn.IsVisible = true;
        }

        if (_webView != null)
        {
            _webView.NavigationCompleted += OnNavigationCompleted;
            try { _webView.Url = new Uri(App.TargetUrl); } catch { }
        }

        // Esc cierra; F12 abre DevTools del WebView2 subyacente.
        KeyDown += OnKeyDown;
    }

    private void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e)
    {
        if (App.ColdStart != null && App.ColdStart.IsRunning)
        {
            App.ColdStart.Stop();
            var ms = App.ColdStart.ElapsedMilliseconds;
            // Stdout para captura via `dotnet run > coldstart.log`. Util para
            // comparar contra WinForms+WebView2 (objetivo <500ms al primer
            // render, baseline Fase 1).
            Console.WriteLine("[PilotX.Desktop] Cold-start (Main -> NavigationCompleted): " + ms + " ms  url=" + App.TargetUrl);
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Cold-start: " + ms + " ms");
            if (_headerSubtitle != null && App.WindowMode == "float")
            {
                var current = _headerSubtitle.Text ?? string.Empty;
                _headerSubtitle.Text = current + (string.IsNullOrEmpty(current) ? "" : "  -  ") + ms + " ms";
            }
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key == Key.F12 && _webView != null)
        {
            try { _webView.OpenDevToolsWindow(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] DevTools error: " + ex.Message); }
        }
    }

    // BeginMoveDrag delega al window-manager del SO el movimiento mientras el
    // mouse este presionado. Lo enganchamos al header entero (sin marcas
    // exclusivas) para que el operario pueda agarrarlo de cualquier punto.
    // Doble-click sobre el header maximiza/restaura (gesto estandar).
    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return;
            }
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Placeholder: los 3 botones de la toolbar inferior aun no estan
    // implementados. Cuando se enganchen a sus paneles reales, este handler
    // desaparece y cada boton agarra su propio Command. Por ahora solo
    // queremos que el chrome compile y renderice.
    private void OnPlaceholderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b)
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] toolbar click: " + b.Name);
    }

    // El subtitulo del header es informativo: muestra que pagina esta
    // cargada. Por ejemplo /pages/camaras.html -> "Camaras". Si no podemos
    // inferirlo, queda vacio y el header solo muestra "PilotX".
    private static string DeriveSubtitleFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try
        {
            var u = new Uri(url);
            var seg = u.AbsolutePath.TrimEnd('/');
            int slash = seg.LastIndexOf('/');
            if (slash < 0) return string.Empty;
            string file = seg.Substring(slash + 1);
            if (string.IsNullOrEmpty(file)) return string.Empty;
            int dot = file.LastIndexOf('.');
            string name = dot > 0 ? file.Substring(0, dot) : file;
            switch (name.ToLowerInvariant())
            {
                case "camaras":        return "Camaras";
                case "camaras-widget": return "Camaras";
                case "flowx":          return "FlowX - Dosificacion";
                case "stormx":         return "StormX - Meteo";
                case "vistax":         return "VistaX - Siembra";
                case "quantix":        return "QuantiX - Motores";
                case "sectionx":       return "SectionX - Secciones";
                case "nodos":          return "Nodos";
                case "debug":          return "Debug";
                default:
                    return char.ToUpperInvariant(name[0]) + name.Substring(1);
            }
        }
        catch { return string.Empty; }
    }
}
