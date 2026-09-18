// RedIpPanel.axaml.cs
// Config de IP (DHCP / fija) por adaptador. PilotX corre limitado: el cambio
// lo aplica el helper SYSTEM (NetApplyWatcher) via RedIpService/RedIpController.
// Las tarjetas por adaptador se generan aca (code-behind).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class RedIpPanel : UserControl, IPanelEmbebible
{
    private RedIpClient? _client;

    private static readonly IBrush BgSurface = new SolidColorBrush(Color.Parse("#F4F6F4"));
    private static readonly IBrush BgField   = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgSel      = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush BgUnsel    = new SolidColorBrush(Color.Parse("#E7ECE7"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#D9E0D9"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#1E241E"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#5B655B"));
    private static readonly IBrush Blanco      = Brushes.White;

    public RedIpPanel() { InitializeComponent(); }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Attach(RedIpClient client) { _client = client; }

    public void Recargar() { if (_client != null) _ = CargarAsync(); }

    public void Reset() { }

    /// <summary>La invoca el botón ✕ Cerrar. MainWindow la setea a CloseRedIp.</summary>
    public Action? OnCerrar { get; set; }
    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnCerrar?.Invoke();

    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        var raiz = this.FindControl<StackPanel>("ContenidoRaiz");
        if (raiz != null) { raiz.Margin = new Thickness(0); raiz.Spacing = 12; }
    }

    public Control? PillsDeContexto() => null;

    private async Task CargarAsync()
    {
        var cont = this.FindControl<StackPanel>("Adaptadores");
        var status = this.FindControl<TextBlock>("StatusGlobal");
        if (cont == null || _client == null) return;
        await Dispatcher.UIThread.InvokeAsync(() => { if (status != null) status.Text = "Cargando adaptadores..."; cont.Children.Clear(); });

        var lista = await _client.ListarAsync().ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cont.Children.Clear();
            if (lista.Count == 0)
            {
                if (status != null) status.Text = "No se encontraron adaptadores WiFi/Ethernet.";
                return;
            }
            // Salida a internet actual = adaptador Up con gateway y MENOR métrica.
            int primariaIdx = lista.Where(x => x.Up && !string.IsNullOrEmpty(x.Gateway) && x.Metric > 0)
                                   .OrderBy(x => x.Metric).Select(x => x.IfIndex).FirstOrDefault();
            foreach (var a in lista) cont.Children.Add(Tarjeta(a, a.IfIndex == primariaIdx));
            if (status != null) status.Text = "";
        });
    }

    private Border Tarjeta(RedAdaptadorDto a, bool esPrimaria)
    {
        var tbIp  = Campo(a.Ip ?? "");
        var tbPfx = Campo(a.Prefix > 0 ? a.Prefix.ToString() : "24", 70);
        var tbGw  = Campo(a.Gateway ?? "");
        var tbDns = Campo(a.Dns != null ? string.Join(", ", a.Dns) : "");
        // Enganchar el teclado nativo (numerico: dígitos + punto, para IPs).
        EngancharTeclado(tbIp, "IP");
        EngancharTeclado(tbPfx, "Prefijo");
        EngancharTeclado(tbGw, "Gateway");
        EngancharTeclado(tbDns, "DNS");

        var camposGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        void Fila(int r, string lbl, Control c)
        {
            var t = new TextBlock { Text = lbl, Foreground = TextoDim, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4), MinWidth = 90 };
            Grid.SetRow(t, r); Grid.SetColumn(t, 0);
            Grid.SetRow(c, r); Grid.SetColumn(c, 1);
            camposGrid.Children.Add(t); camposGrid.Children.Add(c);
        }
        Fila(0, "IP", tbIp);
        Fila(1, "Prefijo (/)", tbPfx);
        Fila(2, "Gateway", tbGw);
        Fila(3, "DNS", tbDns);

        var estado = new TextBlock { Foreground = TextoDim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 14 };

        var btnDhcp   = Chip("DHCP");
        var btnFija   = Chip("IP fija");
        var toggles   = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        toggles.Children.Add(btnDhcp); toggles.Children.Add(btnFija);

        // estado local: modo
        bool[] esFija = { !a.Dhcp };
        void PintarModo()
        {
            btnDhcp.Background = esFija[0] ? BgUnsel : BgSel;
            btnDhcp.Foreground = esFija[0] ? Texto : Blanco;
            btnFija.Background = esFija[0] ? BgSel : BgUnsel;
            btnFija.Foreground = esFija[0] ? Blanco : Texto;
            camposGrid.IsEnabled = esFija[0];
            camposGrid.Opacity = esFija[0] ? 1.0 : 0.5;
        }
        btnDhcp.Click += (_, _) => { esFija[0] = false; PintarModo(); };
        btnFija.Click += (_, _) => { esFija[0] = true; PintarModo(); };
        PintarModo();

        var btnAplicar = new Button
        {
            Content = "Aplicar",
            Background = BgSel, Foreground = Blanco, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(22, 12, 22, 12),
            FontSize = 15, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
        };
        btnAplicar.Click += async (_, _) =>
        {
            btnAplicar.IsEnabled = false;
            estado.Foreground = TextoDim;
            estado.Text = "Aplicando (puede tardar unos segundos)...";
            try
            {
                bool ok; string err;
                if (esFija[0])
                {
                    if (!int.TryParse((tbPfx.Text ?? "").Trim(), out int pfx) || pfx < 1 || pfx > 32)
                    { estado.Foreground = Brushes.Firebrick; estado.Text = "Prefijo invalido (1-32)."; btnAplicar.IsEnabled = true; return; }
                    string ip = (tbIp.Text ?? "").Trim();
                    if (ip.Length == 0)
                    { estado.Foreground = Brushes.Firebrick; estado.Text = "Falta la IP."; btnAplicar.IsEnabled = true; return; }
                    var dns = (tbDns.Text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                    (ok, err) = await _client!.AplicarAsync(a.IfIndex, "static", ip, pfx, (tbGw.Text ?? "").Trim(), dns);
                }
                else
                {
                    (ok, err) = await _client!.AplicarAsync(a.IfIndex, "dhcp", null, 0, null, null);
                }

                if (ok) { estado.Foreground = new SolidColorBrush(Color.Parse("#2E7D32")); estado.Text = "Aplicado."; _ = RefrescarLuego(); }
                else { estado.Foreground = Brushes.Firebrick; estado.Text = "No se pudo: " + (err ?? "error"); }
            }
            catch (Exception ex) { estado.Foreground = Brushes.Firebrick; estado.Text = "Error: " + ex.Message; }
            finally { btnAplicar.IsEnabled = true; }
        };

        string actual = "IP actual: " + (string.IsNullOrEmpty(a.Ip) ? "(sin IP)" : a.Ip + "/" + a.Prefix)
                        + "  -  " + (a.Dhcp ? "DHCP" : "Fija") + (a.Up ? "" : "  (adaptador caido)")
                        + (esPrimaria ? "   ·  SALIDA A INTERNET" : "");

        // Boton "usar para internet" (baja la metrica del adaptador elegido).
        var btnInternet = new Button
        {
            Content = esPrimaria ? "Salida a internet (actual)" : "Usar para internet",
            Background = esPrimaria ? BgUnsel : new SolidColorBrush(Color.Parse("#2B6CA3")),
            Foreground = esPrimaria ? Texto : Blanco,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 12, 18, 12), FontSize = 15, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 0, 0), IsEnabled = !esPrimaria && a.Up,
        };
        btnInternet.Click += async (_, _) =>
        {
            btnInternet.IsEnabled = false;
            estado.Foreground = TextoDim; estado.Text = "Cambiando salida a internet...";
            try
            {
                var (ok, err) = await _client!.AplicarAsync(a.IfIndex, "primary", null, 0, null, null);
                if (ok) { estado.Foreground = new SolidColorBrush(Color.Parse("#2E7D32")); estado.Text = "Listo. Internet por este adaptador."; _ = RefrescarLuego(); }
                else { estado.Foreground = Brushes.Firebrick; estado.Text = "No se pudo: " + (err ?? "error"); btnInternet.IsEnabled = true; }
            }
            catch (Exception ex) { estado.Foreground = Brushes.Firebrick; estado.Text = "Error: " + ex.Message; btnInternet.IsEnabled = true; }
        };

        var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        fila.Children.Add(btnAplicar);
        fila.Children.Add(btnInternet);

        var inner = new StackPanel { Spacing = 2 };
        inner.Children.Add(new TextBlock { Text = a.Nombre + "  (" + (a.Tipo == "wifi" ? "Wi-Fi" : "Ethernet") + ")", Foreground = Texto, FontSize = 16, FontWeight = FontWeight.SemiBold });
        inner.Children.Add(new TextBlock { Text = actual, Foreground = esPrimaria ? new SolidColorBrush(Color.Parse("#2E7D32")) : TextoDim, FontSize = 12, FontWeight = esPrimaria ? FontWeight.SemiBold : FontWeight.Normal });
        inner.Children.Add(toggles);
        inner.Children.Add(camposGrid);
        inner.Children.Add(fila);
        inner.Children.Add(estado);

        return new Border
        {
            Background = BgSurface, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 16),
            MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left,
            Child = inner,
        };
    }

    // Pide abrir el teclado nativo al enfocar el campo y cerrarlo al salir
    // (igual que WifiPanel). Sin esto el teclado no se engancha a estos TextBox.
    private void EngancharTeclado(TextBox tb, string titulo)
    {
        tb.GotFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, true, titulo); };
        tb.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
    }

    private async Task RefrescarLuego()
    {
        await Task.Delay(2500).ConfigureAwait(false);
        await CargarAsync().ConfigureAwait(false);
    }

    private static TextBox Campo(string val, double width = 220) => new TextBox
    {
        Text = val, Width = width, Background = BgField, Foreground = Texto,
        BorderBrush = Borde, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8, 10, 8), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static Button Chip(string txt) => new Button
    {
        Content = txt, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(20, 10, 20, 10), FontSize = 14, FontWeight = FontWeight.SemiBold,
    };
}
