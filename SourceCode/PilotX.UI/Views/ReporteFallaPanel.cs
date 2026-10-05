// ============================================================================
// ReporteFallaPanel.cs — "Reportar falla" en un toque (SISTEMA › Reportar falla).
//
// El soporte remoto era lento: una pantalla fallaba en el campo y nadie podía
// leer los logs. Con este panel el operario:
//
//   1. toca SISTEMA › Reportar falla → la captura ya se tomó (antes de abrir
//      el panel, así sale lo que tenía delante, no el formulario);
//   2. escribe qué pasó (opcional, con el teclado propio de PilotX que se abre
//      solo al tocar el campo) y toca "Enviar reporte";
//   3. ve un CÓDIGO grande (RF-K7M-4QX) para dictar por teléfono. El ZIP queda
//      en cola en el motor y sube a OrbitX solo cuando hay internet;
//   4. si no hay internet o la pantalla no está vinculada, "Guardar en
//      pendrive" lo deja en <pendrive>\PilotX-Reportes\.
//
// Overlay propio (Border + IsVisible dentro del Canvas de overlays), NO un
// popup: sobre el mapa GL los popups no se ven. Construido en código, mismos
// tokens de color que ChatPanel.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public sealed class ReporteFallaPanel : UserControl
{
    private ReporteFallaClient? _client;
    private CancellationTokenSource? _cts;
    private byte[]? _captura;
    private string? _codigo;
    private bool _enviando;

    /// <summary>El operario cerró el panel.</summary>
    public Action? OnRequestCerrar { get; set; }

    private readonly StackPanel _paso1;
    private readonly StackPanel _paso2;
    private readonly Image _miniatura;
    private readonly TextBlock _capturaTexto;
    private readonly TextBox _descripcion;
    private readonly Button _btnEnviar;
    private readonly TextBlock _error;
    private readonly TextBlock _codigoTexto;
    private readonly TextBlock _estadoTexto;
    private readonly Button _btnPendrive;
    private readonly TextBlock _pendriveTexto;

    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    private static readonly Dictionary<string, string> Respaldo = new()
    {
        ["PilotXPanelCard"]       = "#FAFBFA",
        ["PilotXPanelSurface"]    = "#FFFFFF",
        ["PilotXPanelSurface2"]   = "#EDF1EC",
        ["PilotXPanelBorder"]     = "#E2E7E2",
        ["PilotXPanelBorderHigh"] = "#C5CFC5",
        ["PilotXPanelText"]       = "#101612",
        ["PilotXPanelTextDim"]    = "#535E54",
        ["PilotXPanelAccent"]     = "#4ABA3E",
        ["PilotXPanelOk"]         = "#2F7A26",
        ["PilotXPanelOkSoft"]     = "#E8F4E5",
        ["PilotXPanelWarn"]       = "#8A5A00",
        ["PilotXPanelDanger"]     = "#B3261E",
    };

    private readonly Dictionary<string, IBrush> _pinceles = new();

    private IBrush P(string token)
    {
        if (_pinceles.TryGetValue(token, out var cache)) return cache;
        try
        {
            if (this.TryFindResource(token, out var v) && v is IBrush ib)
            {
                _pinceles[token] = ib;
                return ib;
            }
        }
        catch { }
        return new SolidColorBrush(Color.Parse(Respaldo.TryGetValue(token, out var hex) ? hex : "#535E54"));
    }

    public ReporteFallaPanel()
    {
        // ── header ──
        var titulo = new TextBlock
        {
            Text = T("Reportar falla"),
            Foreground = P("PilotXPanelText"),
            FontSize = 20,
            FontWeight = FontWeight.Bold,
        };
        var subt = new TextBlock
        {
            Text = T("Le llega a soporte con captura, registros y configuración (sin contraseñas)."),
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        var tituloCol = new StackPanel { Spacing = 2 };
        tituloCol.Children.Add(titulo);
        tituloCol.Children.Add(subt);

        var btnCerrar = new Button
        {
            Content = "✕",
            Foreground = P("PilotXPanelTextDim"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 20,
            Width = 48,
            Height = 48,
            VerticalAlignment = VerticalAlignment.Top,
        };
        btnCerrar.Click += (_, __) => Cerrar();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetColumn(tituloCol, 0);
        Grid.SetColumn(btnCerrar, 1);
        header.Children.Add(tituloCol);
        header.Children.Add(btnCerrar);

        // ── paso 1: captura + descripción ──
        _miniatura = new Image { Height = 120, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        _capturaTexto = new TextBlock
        {
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var filaCaptura = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        filaCaptura.Children.Add(new Border
        {
            Child = _miniatura,
            BorderBrush = P("PilotXPanelBorderHigh"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
        });
        filaCaptura.Children.Add(_capturaTexto);

        _descripcion = new TextBox
        {
            Watermark = T("¿Qué pasó? Por ejemplo: se cortó el piloto en la cabecera (opcional)"),
            Foreground = P("PilotXPanelText"),
            Background = P("PilotXPanelSurface"),
            BorderBrush = P("PilotXPanelBorderHigh"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinHeight = 110,
            FontSize = 15,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = 4000,
        };

        _btnEnviar = BotonAcento(T("Enviar reporte"));
        _btnEnviar.Click += (_, __) => _ = EnviarAsync();
        var btnCancelar = BotonSecundario(T("Cancelar"));
        btnCancelar.Click += (_, __) => Cerrar();
        var botones1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        botones1.Children.Add(btnCancelar);
        botones1.Children.Add(_btnEnviar);

        _error = new TextBlock
        {
            Foreground = P("PilotXPanelDanger"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        _paso1 = new StackPanel { Spacing = 12 };
        _paso1.Children.Add(filaCaptura);
        _paso1.Children.Add(_descripcion);
        _paso1.Children.Add(_error);
        _paso1.Children.Add(botones1);

        // ── paso 2: código para dictar ──
        var codigoTitulo = new TextBlock
        {
            Text = T("Código del reporte"),
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _codigoTexto = new TextBlock
        {
            Foreground = P("PilotXPanelText"),
            FontSize = 38,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var codigoCaja = new Border
        {
            Background = P("PilotXPanelOkSoft"),
            BorderBrush = P("PilotXPanelBorderHigh"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10),
            Child = _codigoTexto,
        };
        var dictar = new TextBlock
        {
            Text = T("Dictale este código a soporte por teléfono."),
            Foreground = P("PilotXPanelText"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _estadoTexto = new TextBlock
        {
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _btnPendrive = BotonSecundario(T("Guardar en pendrive"));
        _btnPendrive.Click += (_, __) => _ = PendriveAsync();
        var btnListo = BotonAcento(T("Cerrar"));
        btnListo.Click += (_, __) => Cerrar();
        var botones2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        botones2.Children.Add(_btnPendrive);
        botones2.Children.Add(btnListo);

        _pendriveTexto = new TextBlock
        {
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false,
        };

        _paso2 = new StackPanel { Spacing = 10, IsVisible = false };
        _paso2.Children.Add(codigoTitulo);
        _paso2.Children.Add(codigoCaja);
        _paso2.Children.Add(dictar);
        _paso2.Children.Add(_estadoTexto);
        _paso2.Children.Add(botones2);
        _paso2.Children.Add(_pendriveTexto);

        var cuerpo = new StackPanel();
        cuerpo.Children.Add(header);
        cuerpo.Children.Add(_paso1);
        cuerpo.Children.Add(_paso2);

        Content = new Border
        {
            Background = P("PilotXPanelCard"),
            BorderBrush = P("PilotXPanelBorderHigh"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            BoxShadow = BoxShadows.Parse("0 6 24 0 #33000000"),
            Child = cuerpo,
        };
    }

    private Button BotonAcento(string texto) => new Button
    {
        Content = texto,
        Background = P("PilotXPanelAccent"),
        // Texto OSCURO sobre el verde de marca (al sol el blanco no se lee).
        Foreground = P("PilotXPanelText"),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(8),
        MinHeight = 52,
        Padding = new Thickness(22, 0),
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private Button BotonSecundario(string texto) => new Button
    {
        Content = texto,
        Background = P("PilotXPanelSurface2"),
        Foreground = P("PilotXPanelText"),
        BorderBrush = P("PilotXPanelBorderHigh"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        MinHeight = 52,
        Padding = new Thickness(18, 0),
        FontSize = 16,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Abre el panel con la captura ya tomada (puede ser null).</summary>
    public void Abrir(ReporteFallaClient client, byte[]? captura)
    {
        _client = client;
        _captura = captura;
        _codigo = null;
        _enviando = false;
        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();

        _descripcion.Text = "";
        _error.IsVisible = false;
        _btnEnviar.IsEnabled = true;
        _btnEnviar.Content = T("Enviar reporte");
        _paso1.IsVisible = true;
        _paso2.IsVisible = false;
        _pendriveTexto.IsVisible = false;

        _miniatura.Source = null;
        if (captura != null)
        {
            try
            {
                using var ms = new MemoryStream(captura);
                _miniatura.Source = new Bitmap(ms);
            }
            catch { _miniatura.Source = null; }
        }
        _miniatura.IsVisible = _miniatura.Source != null;
        _capturaTexto.Text = _miniatura.Source != null
            ? T("Captura de la pantalla tomada.")
            : T("No se pudo tomar la captura; el reporte sale igual.");
    }

    private void Cerrar()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        OnRequestCerrar?.Invoke();
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    private async Task EnviarAsync()
    {
        if (_enviando || _client == null) return;
        _enviando = true;
        _btnEnviar.IsEnabled = false;
        _btnEnviar.Content = T("Armando el reporte…");
        _error.IsVisible = false;
        var ct = _cts?.Token ?? CancellationToken.None;

        var r = await _client.CrearAsync(_descripcion.Text ?? "", _captura, ct);
        if (ct.IsCancellationRequested) return;
        _enviando = false;

        if (!r.Ok || string.IsNullOrEmpty(r.Codigo))
        {
            _btnEnviar.IsEnabled = true;
            _btnEnviar.Content = T("Enviar reporte");
            _error.Text = T("No se pudo armar el reporte") + ": " + (r.Error == "service-unavailable"
                ? T("el motor de PilotX no tiene el reporte disponible")
                : r.Error ?? "?");
            _error.IsVisible = true;
            return;
        }

        _codigo = r.Codigo;
        _codigoTexto.Text = r.Codigo;
        _paso1.IsVisible = false;
        _paso2.IsVisible = true;
        PintarEstado(r.Estado, r.Vinculado);
        _ = SeguirEstadoAsync(r.Codigo, ct);
    }

    private void PintarEstado(string? estado, bool vinculado)
    {
        if (estado == "subido")
        {
            _estadoTexto.Text = "✓ " + T("Ya está en OrbitX.");
            _estadoTexto.Foreground = P("PilotXPanelOk");
        }
        else if (!vinculado)
        {
            _estadoTexto.Text = T("La pantalla no está vinculada a OrbitX: guardalo en un pendrive y mandalo.");
            _estadoTexto.Foreground = P("PilotXPanelWarn");
        }
        else
        {
            _estadoTexto.Text = T("En cola: se sube solo apenas haya internet. Podés cerrar.");
            _estadoTexto.Foreground = P("PilotXPanelTextDim");
        }
    }

    /// <summary>Mientras el panel esté abierto, mira si ya subió (cada 3 s).</summary>
    private async Task SeguirEstadoAsync(string codigo, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
            if (_client == null) return;
            var r = await _client.EstadoAsync(codigo, ct);
            if (ct.IsCancellationRequested || codigo != _codigo) return;
            if (!r.Ok) continue;
            await Dispatcher.UIThread.InvokeAsync(() => PintarEstado(r.Estado, r.Vinculado));
            if (r.Estado == "subido") return;
        }
    }

    private async Task PendriveAsync()
    {
        if (_client == null || string.IsNullOrEmpty(_codigo)) return;
        _btnPendrive.IsEnabled = false;
        _pendriveTexto.IsVisible = true;
        _pendriveTexto.Foreground = P("PilotXPanelTextDim");
        _pendriveTexto.Text = T("Copiando al pendrive…");

        var r = await _client.PendriveAsync(_codigo, _cts?.Token ?? CancellationToken.None);
        _btnPendrive.IsEnabled = true;
        if (r.Ok)
        {
            _pendriveTexto.Foreground = P("PilotXPanelOk");
            _pendriveTexto.Text = "✓ " + T("Guardado en") + " " + r.Ruta;
        }
        else
        {
            _pendriveTexto.Foreground = P("PilotXPanelDanger");
            _pendriveTexto.Text = r.Error ?? T("No se pudo copiar al pendrive.");
        }
    }
}
