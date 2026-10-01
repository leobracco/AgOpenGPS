// ============================================================================
// TareasPanel.cs — "Tarea" del lote abierto, card nativa chica sobre el mapa.
//
// Se abre desde LOTE › Tarea. El operario la usa con el tractor andando, así
// que tiene tres estados y nada más:
//   · sin lote       → un aviso ("abrí un lote").
//   · tarea abierta  → área y tiempo en grande, Pausar/Reanudar y Cerrar
//                      (con confirmación inline, nunca modal).
//   · sin tarea      → "Nueva tarea": tipo de trabajo (5 botones), cultivo y
//                      notas (teclado PROPIO de PilotX), insumo activo del
//                      catálogo a la vista, Iniciar. Abajo las cerradas, con
//                      Exportar (informe HTML + SHP al pendrive, eligiendo
//                      carpeta en el explorador propio).
//
// Las cuentas y los textos (ha, duración, dosis con unidad) vienen hechos del
// motor (TareasService): la pantalla no rehace unidades. Polling cada 2 s SOLO
// mientras está visible, para que el área y el tiempo se muevan.
//
// Nada de ComboBox/Flyout (no se dibujan sobre el mapa GL): todo Border +
// IsVisible. El mapa NUNCA se apaga: la card es un overlay más.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public sealed class TareasPanel : Border
{
    // ---- paleta PilotX (design system claro) ---------------------------------
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgSuave    = new SolidColorBrush(Color.Parse("#F5F7F4"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush BordeSuave = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ambar      = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#C84E48"));

    private static readonly (string Id, string Texto)[] Tipos =
    {
        ("siembra", "Siembra"),
        ("pulverizacion", "Pulverización"),
        ("fertilizacion", "Fertilización"),
        ("cosecha", "Cosecha"),
        ("otro", "Otro"),
    };

    private TareasClient? _client;
    private DispatcherTimer? _timer;
    private bool _ocupado;
    private string _modo = "";            // "sinlote" | "abierta" | "nueva"
    private string _tipoElegido = "siembra";
    private TareasEstadoDto? _ultimo;

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;
    /// <summary>Aviso corto (toast de MainWindow, nunca modal).</summary>
    public event Action<string>? Aviso;

    // ---- controles -----------------------------------------------------------
    private readonly TextBlock _subtitulo;
    private readonly StackPanel _vistaSinLote;
    private readonly StackPanel _vistaAbierta;
    private readonly StackPanel _vistaNueva;
    private readonly TextBlock _sinConexion;

    // abierta
    private readonly Border _pill;
    private readonly TextBlock _pillTxt;
    private readonly TextBlock _abTitulo;
    private readonly TextBlock _abArea;
    private readonly TextBlock _abTiempo;
    private readonly TextBlock _abDetalle;
    private readonly TextBlock _abNotas;
    private readonly Button _btnPausa;
    private readonly Button _btnCerrar;
    private readonly Border _confirmar;

    // nueva
    private readonly Dictionary<string, Button> _btnTipos = new();
    private readonly TextBox _txtCultivo;
    private readonly TextBox _txtNotas;
    private readonly TextBlock _insumoTxt;
    private readonly Button _btnIniciar;
    private readonly StackPanel _listaCerradas;
    private readonly TextBlock _cerradasTit;

    public TareasPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(14);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        Width = 440;
        MaxHeight = 620;
        IsVisible = false;

        // ---------- cabecera ----------
        var titulo = new TextBlock { Text = "Tarea", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Texto };
        _subtitulo = new TextBlock { Text = "", FontSize = 11.5, Foreground = TextoMuted, TextTrimming = TextTrimming.CharacterEllipsis };
        var tituloCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tituloCol.Children.Add(titulo);
        tituloCol.Children.Add(_subtitulo);

        var btnX = new Button
        {
            Content = "✕", Width = 48, Height = 44, FontSize = 14, FontWeight = FontWeight.SemiBold,
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnX.Click += (_, _) => Cerrar();

        var cab = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetColumn(btnX, 1);
        cab.Children.Add(tituloCol);
        cab.Children.Add(btnX);

        _sinConexion = new TextBlock
        {
            Text = "Sin conexión con PilotX. Reintentando…", FontSize = 12, Foreground = Rojo,
            TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 8),
        };

        // ---------- sin lote ----------
        _vistaSinLote = new StackPanel { IsVisible = false };
        _vistaSinLote.Children.Add(new TextBlock
        {
            Text = "Abrí un lote para registrar una tarea (LOTE › Continuar, Abrir o Nuevo).",
            FontSize = 14, Foreground = Texto, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6),
        });

        // ---------- tarea abierta ----------
        _vistaAbierta = new StackPanel { IsVisible = false, Spacing = 10 };
        _pillTxt = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        _pill = new Border
        {
            Child = _pillTxt, CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 3),
            Background = Verde, HorizontalAlignment = HorizontalAlignment.Left,
        };
        _abTitulo = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Texto, TextWrapping = TextWrapping.Wrap };
        var filaTit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        filaTit.Children.Add(_pill);
        filaTit.Children.Add(_abTitulo);

        _abArea = new TextBlock { FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Texto };
        _abTiempo = new TextBlock { FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Texto };
        var kpis = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var kArea = Kpi(_abArea, "Área de la tarea");
        var kTiempo = Kpi(_abTiempo, "Tiempo trabajando");
        kArea.Margin = new Thickness(0, 0, 5, 0);
        kTiempo.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(kTiempo, 1);
        kpis.Children.Add(kArea);
        kpis.Children.Add(kTiempo);

        _abDetalle = new TextBlock { FontSize = 12.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap };
        _abNotas = new TextBlock { FontSize = 12.5, Foreground = Texto, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        _btnPausa = BotonGrande("Pausar", acento: false);
        _btnPausa.Click += async (_, _) => await PausaReanudarAsync();
        _btnCerrar = BotonGrande("Cerrar tarea", acento: false);
        _btnCerrar.Click += (_, _) => { _confirmar!.IsVisible = true; };
        var acciones = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        _btnPausa.Margin = new Thickness(0, 0, 5, 0);
        _btnCerrar.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(_btnCerrar, 1);
        acciones.Children.Add(_btnPausa);
        acciones.Children.Add(_btnCerrar);

        // Confirmación INLINE (no modal: ShowDialog traba la cabina).
        var confTxt = new TextBlock
        {
            Text = "¿Cerrar la tarea? Deja de sumar hectáreas y queda lista para exportar.",
            FontSize = 13, Foreground = Texto, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        var btnNo = BotonGrande("No", acento: false);
        btnNo.Click += (_, _) => _confirmar!.IsVisible = false;
        var btnSi = BotonGrande("Sí, cerrar", acento: true);
        btnSi.Click += async (_, _) => await CerrarTareaAsync();
        var confBtns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        btnNo.Margin = new Thickness(0, 0, 5, 0);
        btnSi.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(btnSi, 1);
        confBtns.Children.Add(btnNo);
        confBtns.Children.Add(btnSi);
        var confCol = new StackPanel();
        confCol.Children.Add(confTxt);
        confCol.Children.Add(confBtns);
        _confirmar = new Border
        {
            Child = confCol, Background = BgSuave, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10), IsVisible = false,
        };

        _vistaAbierta.Children.Add(filaTit);
        _vistaAbierta.Children.Add(kpis);
        _vistaAbierta.Children.Add(_abDetalle);
        _vistaAbierta.Children.Add(_abNotas);
        _vistaAbierta.Children.Add(acciones);
        _vistaAbierta.Children.Add(_confirmar);

        // ---------- nueva tarea ----------
        _vistaNueva = new StackPanel { IsVisible = false, Spacing = 8 };
        _vistaNueva.Children.Add(Etiqueta("Tipo de trabajo"));
        var grillaTipos = new UniformGrid { Columns = 3 };
        foreach (var (id, texto) in Tipos)
        {
            var b = BotonGrande(texto, acento: false);
            b.Height = 52;
            b.FontSize = 13;
            b.Margin = new Thickness(3);
            string idLocal = id;
            b.Click += (_, _) => ElegirTipo(idLocal);
            _btnTipos[id] = b;
            grillaTipos.Children.Add(b);
        }
        _vistaNueva.Children.Add(grillaTipos);

        _vistaNueva.Children.Add(Etiqueta("Cultivo"));
        _txtCultivo = Campo("Ej.: Soja", multilinea: false);
        _txtCultivo.GotFocus += (_, _) => _ = _client?.TecladoAsync(true, Traductor.T("Cultivo"));
        _txtCultivo.LostFocus += (_, _) => _ = _client?.TecladoAsync(false);
        _vistaNueva.Children.Add(_txtCultivo);

        _vistaNueva.Children.Add(Etiqueta("Notas"));
        _txtNotas = Campo("Opcional", multilinea: true);
        _txtNotas.GotFocus += (_, _) => _ = _client?.TecladoAsync(true, Traductor.T("Notas de la tarea"));
        _txtNotas.LostFocus += (_, _) => _ = _client?.TecladoAsync(false);
        _vistaNueva.Children.Add(_txtNotas);

        _insumoTxt = new TextBlock { FontSize = 12.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap };
        _vistaNueva.Children.Add(_insumoTxt);

        _btnIniciar = BotonGrande("Iniciar tarea", acento: true);
        _btnIniciar.Height = 60;
        _btnIniciar.Click += async (_, _) => await IniciarAsync();
        _vistaNueva.Children.Add(_btnIniciar);

        _cerradasTit = Etiqueta("Tareas cerradas");
        _cerradasTit.Margin = new Thickness(0, 8, 0, 0);
        _listaCerradas = new StackPanel { Spacing = 6 };
        _vistaNueva.Children.Add(_cerradasTit);
        _vistaNueva.Children.Add(_listaCerradas);

        // ---------- árbol ----------
        var cuerpo = new StackPanel();
        cuerpo.Children.Add(_sinConexion);
        cuerpo.Children.Add(_vistaSinLote);
        cuerpo.Children.Add(_vistaAbierta);
        cuerpo.Children.Add(_vistaNueva);
        var scroll = new ScrollViewer
        {
            Content = cuerpo,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var dock = new DockPanel();
        DockPanel.SetDock(cab, Dock.Top);
        dock.Children.Add(cab);
        dock.Children.Add(scroll);
        Child = dock;

        ElegirTipo("siembra");
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public void Attach(TareasClient client) => _client = client;

    public void Abrir()
    {
        _modo = "";
        _confirmar.IsVisible = false;
        IsVisible = true;
        Traductor.Aplicar(this);
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await RefrescarAsync());
        _timer.Start();
        _ = RefrescarAsync();
    }

    public void Cerrar()
    {
        bool estaba = IsVisible;
        _timer?.Stop();
        _ = _client?.TecladoAsync(false);
        IsVisible = false;
        if (estaba) Cerrado?.Invoke();
    }

    // =========================================================================
    //  estado
    // =========================================================================

    private async Task RefrescarAsync()
    {
        if (!IsVisible || _client == null) return;
        var e = await _client.EstadoAsync().ConfigureAwait(true);
        if (!IsVisible) return;
        Pintar(e);
    }

    private void Pintar(TareasEstadoDto? e)
    {
        _sinConexion.IsVisible = e == null;
        if (e == null) return;
        _ultimo = e;
        _subtitulo.Text = e.HayLote ? Traductor.T("Lote") + ": " + e.Lote : Traductor.T("Sin lote abierto");

        string modo = !e.HayLote ? "sinlote" : e.Abierta != null ? "abierta" : "nueva";
        bool cambioModo = modo != _modo;
        _modo = modo;
        _vistaSinLote.IsVisible = modo == "sinlote";
        _vistaAbierta.IsVisible = modo == "abierta";
        _vistaNueva.IsVisible = modo == "nueva";

        if (modo == "abierta") PintarAbierta(e.Abierta!);
        if (modo == "nueva") PintarNueva(e, precargar: cambioModo);
    }

    private void PintarAbierta(TareaVistaDto t)
    {
        bool activa = t.Estado == "activa";
        _pillTxt.Text = Traductor.T(t.EstadoTexto ?? "");
        _pill.Background = activa ? Verde : Ambar;
        _pillTxt.Foreground = activa ? Brushes.White : Texto;
        _abTitulo.Text = Traductor.T(t.TipoTexto ?? "") + (string.IsNullOrWhiteSpace(t.Cultivo) ? "" : " · " + t.Cultivo);
        _abArea.Text = t.AreaTexto ?? "";
        _abTiempo.Text = t.DuracionTexto ?? "";

        var partes = new List<string> { Traductor.T("Inicio") + " " + t.InicioTexto };
        if (!string.IsNullOrWhiteSpace(t.Insumo)) partes.Add(t.Insumo!);
        if (!string.IsNullOrWhiteSpace(t.DosisTexto)) partes.Add(t.DosisTexto!);
        _abDetalle.Text = string.Join("  ·  ", partes);
        _abNotas.IsVisible = !string.IsNullOrWhiteSpace(t.Notas);
        _abNotas.Text = t.Notas ?? "";

        _btnPausa.Content = Traductor.T(activa ? "Pausar" : "Reanudar");
        // Reanudar es la acción que hay que ver: va en verde.
        Pintar(_btnPausa, acento: !activa);
    }

    private void PintarNueva(TareasEstadoDto e, bool precargar)
    {
        if (precargar)
        {
            _txtCultivo.Text = e.CultivoSugerido ?? "";
            _txtNotas.Text = "";
            ElegirTipo(string.IsNullOrEmpty(e.TipoSugerido) ? "siembra" : e.TipoSugerido!);
        }
        _insumoTxt.Text = string.IsNullOrWhiteSpace(e.InsumoActivo)
            ? Traductor.T("Sin insumo activo (se elige en Insumos).")
            : Traductor.T("Insumo activo") + ": " + e.InsumoActivo
              + (string.IsNullOrWhiteSpace(e.InsumoDosisTexto) ? "" : "  ·  " + e.InsumoDosisTexto);

        var cerradas = e.Cerradas ?? new List<TareaVistaDto>();
        _cerradasTit.IsVisible = cerradas.Count > 0;
        _listaCerradas.Children.Clear();
        int n = 0;
        foreach (var t in cerradas)
        {
            if (++n > 5) break;
            _listaCerradas.Children.Add(FilaCerrada(t));
        }
    }

    private Control FilaCerrada(TareaVistaDto t)
    {
        var linea1 = new TextBlock
        {
            Text = Traductor.T(t.TipoTexto ?? "") + (string.IsNullOrWhiteSpace(t.Cultivo) ? "" : " · " + t.Cultivo)
                   + "  —  " + t.AreaTexto,
            FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Texto, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var linea2 = new TextBlock
        {
            Text = t.InicioTexto + "  →  " + t.FinTexto + "  ·  " + t.DuracionTexto,
            FontSize = 11.5, Foreground = TextoMuted,
        };
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(linea1);
        col.Children.Add(linea2);

        var btn = BotonGrande("Exportar", acento: false);
        btn.Height = 48;
        btn.Width = 110;
        btn.FontSize = 13;
        btn.Click += async (_, _) => await ExportarAsync(t);

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(btn, 1);
        btn.Margin = new Thickness(8, 0, 0, 0);
        g.Children.Add(col);
        g.Children.Add(btn);
        return new Border
        {
            Child = g, Background = BgFila, BorderBrush = BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 6, 6),
        };
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    private void ElegirTipo(string id)
    {
        _tipoElegido = id;
        foreach (var kv in _btnTipos) Pintar(kv.Value, acento: kv.Key == id);
    }

    private async Task IniciarAsync()
    {
        if (_ocupado || _client == null) return;
        _ocupado = true;
        try
        {
            _ = _client.TecladoAsync(false);
            var e = await _client.CrearAsync(_tipoElegido, _txtCultivo.Text ?? "", _txtNotas.Text ?? "").ConfigureAwait(true);
            Resultado(e, "Tarea iniciada.");
        }
        finally { _ocupado = false; }
    }

    private async Task PausaReanudarAsync()
    {
        if (_ocupado || _client == null || _ultimo?.Abierta == null) return;
        _ocupado = true;
        try
        {
            bool activa = _ultimo.Abierta.Estado == "activa";
            var e = activa ? await _client.PausarAsync().ConfigureAwait(true)
                           : await _client.ReanudarAsync().ConfigureAwait(true);
            Resultado(e, activa ? "Tarea en pausa: no suma hectáreas." : "Tarea en curso.");
        }
        finally { _ocupado = false; }
    }

    private async Task CerrarTareaAsync()
    {
        if (_ocupado || _client == null) return;
        _ocupado = true;
        try
        {
            _confirmar.IsVisible = false;
            var e = await _client.CerrarAsync().ConfigureAwait(true);
            Resultado(e, "Tarea cerrada. Podés exportarla desde la lista.");
        }
        finally { _ocupado = false; }
    }

    private async Task ExportarAsync(TareaVistaDto t)
    {
        if (_ocupado || _client == null || string.IsNullOrEmpty(t.Id)) return;
        string destino = "";
        if (ExploradorArchivos.CardDisponible)
        {
            // El explorador propio (USB arriba de todo): el operario elige el
            // pendrive y el nombre. Al lado del .html quedan los del shapefile.
            string? ruta = await ExploradorArchivos.GuardarAsync(
                Traductor.T("Exportar tarea"), t.ArchivoSugerido ?? "Tarea", ".html").ConfigureAwait(true);
            if (ruta == null) return;   // canceló
            destino = ruta;
        }

        _ocupado = true;
        try
        {
            Aviso?.Invoke(Traductor.T("Exportando la tarea…"));
            var r = await _client.ExportarAsync(t.Id!, destino).ConfigureAwait(true);
            if (r == null) { Aviso?.Invoke(Traductor.T("Sin conexión con PilotX.") + "  (AGP-NET-201)"); return; }
            if (!r.Ok) { Aviso?.Invoke(Traductor.T(r.Error ?? "No se pudo exportar la tarea.")); return; }
            Aviso?.Invoke(Traductor.T(r.Poligonos > 0
                    ? "Tarea exportada (informe + mapa de cobertura) en"
                    : "Tarea exportada (informe; sin cobertura pintada) en") + " " + r.Carpeta);
        }
        finally { _ocupado = false; }
    }

    private void Resultado(TareasEstadoDto? e, string okMsg)
    {
        if (e == null) { Aviso?.Invoke(Traductor.T("Sin conexión con PilotX.") + "  (AGP-NET-201)"); return; }
        if (!e.Ok && !string.IsNullOrEmpty(e.Error)) Aviso?.Invoke(Traductor.T(e.Error!));
        else Aviso?.Invoke(Traductor.T(okMsg));
        Pintar(e);
    }

    // =========================================================================
    //  helpers visuales
    // =========================================================================

    private static Border Kpi(TextBlock valor, string etiqueta)
    {
        var col = new StackPanel();
        col.Children.Add(valor);
        col.Children.Add(new TextBlock { Text = etiqueta, FontSize = 11, Foreground = TextoMuted });
        return new Border
        {
            Child = col, Background = BgSuave, BorderBrush = BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8),
        };
    }

    private static TextBlock Etiqueta(string texto) => new()
    {
        Text = texto, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = TextoMuted,
    };

    private static TextBox Campo(string marca, bool multilinea) => new()
    {
        Watermark = marca, FontSize = 15, MinHeight = multilinea ? 72 : 48,
        AcceptsReturn = multilinea, TextWrapping = multilinea ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MaxLength = multilinea ? 500 : 60,
        Background = BgFila, Foreground = Texto, BorderBrush = Borde, CornerRadius = new CornerRadius(8),
        VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 6),
    };

    private static Button BotonGrande(string texto, bool acento)
    {
        var b = new Button
        {
            Content = texto, Height = 56, FontSize = 14.5, FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        Pintar(b, acento);
        return b;
    }

    private static void Pintar(Button b, bool acento)
    {
        b.Background = acento ? Verde : BgFila;
        b.Foreground = acento ? Brushes.White : Texto;
        b.BorderBrush = acento ? Verde : Borde;
    }
}
