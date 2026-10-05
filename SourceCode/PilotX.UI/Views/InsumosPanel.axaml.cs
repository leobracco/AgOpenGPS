// InsumosPanel.axaml.cs
//
// Reemplazo nativo de pages/insumos.html + js/insumos.js — el CATÁLOGO
// COMPARTIDO de insumos (semillas, fertilizantes, fitosanitarios).
//
// Compartido de verdad: el mismo ítem lo leen VistaX (densidad objetivo,
// densidad asumida al saturar, singulación), QuantiX (densidad de siembra),
// FlowX (L/ha) y los datos del lote (precio). Por eso el port es LITERAL:
//   · MISMOS endpoints y verbos: GET/POST /api/insumos (catálogo ENTERO en el
//     body) y POST /api/insumos/activo con { id }.
//   · MISMO ID: slug del nombre + sufijo -2, -3… si ya existe. Nunca cambia al
//     editar (otros configs lo referencian).
//   · MISMA validación: lo único obligatorio es el NOMBRE. Todo lo demás entra
//     por num() y un valor no numérico queda en 0 (la singulación en 0 vuelve
//     a 97, igual que el JS).
//   · MISMOS bloques por tipo: semilla muestra cultivo + densidades + límites;
//     semilla y fertilizante muestran la densidad de siembra, su unidad y el
//     precio por kg; fertilizante y fitosanitario muestran L/ha y precio por L.
//     Los campos escondidos NO se borran: se guardan con el valor que tenían,
//     igual que en la página (el JS escribía todos los campos del formulario
//     estuvieran visibles o no).
//   · MISMA confirmación de borrado y mismo efecto: si el borrado saca al
//     insumo ACTIVO, activo_id queda en "" dentro del mismo POST del catálogo
//     (el JS no manda /activo aparte — se replica tal cual).
//
// UNIDADES: la unidad de la densidad de siembra es del insumo (kg/ha, sem/ha o
// sem/m) y se muestra tal cual el operario la eligió. Nada de pps.
//
// La página HTML queda intacta: la usa la PWA del celular y el Hub remoto.
//
// API: Attach(InsumosClient) carga el catálogo; Detach() corta las llamadas en
// curso, cierra diálogos y baja el teclado. Sin polling — el original tampoco
// lo tiene.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class InsumosPanel : UserControl, IPanelEmbebible
{
    // ---- wire / estado -----------------------------------------------------
    private InsumosClient? _client;
    private CancellationTokenSource? _cts;

    /// <summary>El catálogo tal cual vino del GET. Se muta en memoria y se
    /// postea ENTERO, igual que el `state.catalogo` del JS.</summary>
    private InsumoCatalogoWire _catalogo = new InsumoCatalogoWire();

    /// <summary>null = editor cerrado · "" = insumo nuevo · id = editando.</summary>
    private string? _editando;

    /// <summary>Tipo elegido en el editor ("semilla" por default).</summary>
    private string _tipo = "semilla";

    /// <summary>Valor del &lt;select&gt; de unidad: "kg_ha" | "sem_ha" | "sem_m".</summary>
    private string _dosisUnidad = "kg_ha";

    // ---- paleta clara (tokens PilotXPanel* del theme) ----------------------
    private static readonly IBrush Superficie  = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Superficie2 = new SolidColorBrush(Color.Parse("#EDF1EC"));
    private static readonly IBrush Borde       = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush BordeAlto   = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto       = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoTenue  = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde       = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush VerdeTexto  = new SolidColorBrush(Color.Parse("#2F7A26"));
    private static readonly IBrush VerdeSuave  = new SolidColorBrush(Color.Parse("#E8F4E5"));
    private static readonly IBrush Rojo        = new SolidColorBrush(Color.Parse("#C0261F"));
    private static readonly IBrush Apagado     = new SolidColorBrush(Color.Parse("#6E7A70"));

    /// <summary>Unidades del &lt;select&gt;, en el MISMO orden que la página.</summary>
    private static readonly (string Valor, string Etiqueta)[] Unidades =
    {
        ("kg_ha",  "kg/ha"),
        ("sem_ha", "sem/ha"),
        ("sem_m",  "sem/m"),
    };

    // ---- callbacks al host -------------------------------------------------

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Aviso corto para el operario (el host lo muestra como toast).</summary>
    public event Action<string>? Aviso;

    // ---- diálogo interno ---------------------------------------------------
    // _modalOnOk    → confirm(): corre SOLO si el operario toca "Aceptar".
    // _alertaCierre → alert(): corre con CUALQUIER cierre (Aceptar o tocar
    //                 afuera), igual que la promesa de AgpModal.alert.
    private Action? _modalOnOk;
    private Action? _alertaCierre;

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    public InsumosPanel()
    {
        InitializeComponent();

        ArmarPickerUnidad();
        SetTipo("semilla");
        SetUnidad("kg_ha");
        RenderPill();
        RenderLista();

        // Teclado nativo: NO es automático por foco, cada campo lo pide con la
        // misma señal HTTP que manda keyboard.js. Numérico donde el original
        // usaba <input type="number">, qwerty en nombre/cultivo/notas.
        Teclado("FNombre",   "Nombre visible", false);
        Teclado("FCultivo",  "Cultivo", false);
        Teclado("FNotas",    "Notas", false);
        Teclado("FDensObj",  "Densidad objetivo (sem/m)", true);
        Teclado("FDensSat",  "Densidad asumida al saturar (sem/m)", true);
        Teclado("FSingul",   "Singulación objetivo (%)", true);
        Teclado("FDropMin",  "Mín. aceptable (sem/m)", true);
        Teclado("FDropMax",  "Máx. aceptable (sem/m)", true);
        Teclado("FDosisKg",  "Densidad de siembra", true);
        Teclado("FPrecioKg", "Precio (USD/kg)", true);
        Teclado("FDosisL",   "Dosis (L/ha)", true);
        Teclado("FPrecioL",  "Precio (USD/L)", true);

        // El diccionario se aplica UNA SOLA VEZ, al construir: Aplicar guarda el
        // primer texto de cada control y se lo reescribe encima en cada pasada —
        // llamarlo en los render congelaría la pill del insumo activo en su
        // primer valor (lección de NodosPanel). Todo lo que escribe el código
        // pasa por T().
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Teclado(string nombre, string titulo, bool numerico)
    {
        var tb = this.FindControl<TextBox>(nombre);
        if (tb == null) return;
        tb.GotFocus  += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, T(titulo), numerico); };
        tb.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
    }

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    /// <summary>La pill del insumo activo va a la barra de contexto del shell;
    /// la fila de cabecera vieja queda oculta.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<StackPanel>("HeaderPills"));

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public void Attach(InsumosClient client)
    {
        _client = client;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = CargarAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _alertaCierre = null;
        CerrarModal();
        CerrarPicker();
        _ = _client?.TecladoAsync(false);
    }

    // =========================================================================
    //  load / persist (load() y persist() del JS)
    // =========================================================================

    private async Task CargarAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var dto = await _client.GetCatalogoAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        // El JS solo hacía console.warn en la falla y renderizaba con lo que
        // tenía en memoria: sin pantalla de error.
        if (dto != null) _catalogo = dto;
        _catalogo.Items ??= new List<InsumoWire>();
        Render();
    }

    /// <summary>POST del catálogo entero. El JS ignora el resultado salvo por un
    /// console.warn; acá se avisa al operario, que en cabina no tiene consola.</summary>
    private async Task PersistirAsync()
    {
        if (_client == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        bool ok = await _client.GuardarCatalogoAsync(_catalogo, ct).ConfigureAwait(true);
        if (!ok && !ct.IsCancellationRequested)
            Aviso?.Invoke(T("No se pudo guardar el catálogo de insumos."));
    }

    private async Task SetActivoAsync(string id)
    {
        if (_client == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        await _client.SetActivoAsync(id ?? "", ct).ConfigureAwait(true);
        // El JS actualiza su copia local pase lo que pase (el catch solo loguea).
        _catalogo.ActivoId = id ?? "";
        Render();
    }

    // =========================================================================
    //  render
    // =========================================================================

    private void Render()
    {
        RenderPill();
        RenderLista();
    }

    private InsumoWire? Activo()
    {
        string id = _catalogo.ActivoId ?? "";
        var items = _catalogo.Items;
        if (items == null) return null;
        foreach (var it in items)
            if (string.Equals(it.Id, id, StringComparison.Ordinal)) return it;
        return null;
    }

    private void RenderPill()
    {
        var box = this.FindControl<Border>("ActivoPillBox");
        var dot = this.FindControl<Ellipse>("ActivoPillDot");
        var txt = this.FindControl<TextBlock>("ActivoPill");
        if (box == null || txt == null) return;

        var activo = Activo();
        if (activo != null)
        {
            txt.Text = T("Activo") + ": " + (string.IsNullOrEmpty(activo.Nombre) ? activo.Id : activo.Nombre);
            txt.Foreground = VerdeTexto;
            box.Background = VerdeSuave;
            box.BorderBrush = VerdeTexto;
            if (dot != null) dot.Fill = Verde;
        }
        else
        {
            txt.Text = T("Sin insumo activo");
            txt.Foreground = TextoTenue;
            box.Background = Superficie2;
            box.BorderBrush = BordeAlto;
            if (dot != null) dot.Fill = Apagado;
        }
    }

    private void RenderLista()
    {
        var lista = this.FindControl<StackPanel>("Lista");
        if (lista == null) return;
        lista.Children.Clear();

        var items = _catalogo.Items ?? new List<InsumoWire>();
        if (items.Count == 0)
        {
            lista.Children.Add(new TextBlock
            {
                Text = T("No hay insumos cargados. Tocá \"+ Nuevo insumo\" para empezar."),
                Foreground = TextoTenue,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        string activoId = _catalogo.ActivoId ?? "";
        foreach (var it in items)
            lista.Children.Add(Fila(it, string.Equals(it.Id, activoId, StringComparison.Ordinal)));
    }

    /// <summary>Una fila del catálogo (.insumo-row): círculo de activo · nombre
    /// y meta · botón Editar. Tocar en cualquier lado que no sea el círculo abre
    /// el editor, exactamente como el handler del JS.</summary>
    private Control Fila(InsumoWire it, bool activo)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto") };

        // --- círculo de activo (radio-dot) ---
        var aro = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(2),
            BorderBrush = activo ? Verde : BordeAlto,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (activo)
        {
            aro.Child = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = Verde,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        // El área táctil es la celda entera (28 px de ancho por el alto de la
        // fila): el círculo de 22 px es un blanco chico para un dedo con guante.
        var zonaDot = new Panel { Background = Brushes.Transparent };
        zonaDot.Children.Add(aro);
        string idDot = it.Id ?? "";
        zonaDot.PointerPressed += (_, e) =>
        {
            e.Handled = true;   // no llega al Border de la fila (que abriría el editor)
            // `setActivo(id === activo_id ? '' : id)`: volver a tocar el activo
            // lo desactiva.
            _ = SetActivoAsync(string.Equals(idDot, _catalogo.ActivoId ?? "", StringComparison.Ordinal)
                               ? "" : idDot);
        };
        Grid.SetColumn(zonaDot, 0);

        // --- nombre + meta ---
        var pila = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        pila.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(it.Nombre) ? (it.Id ?? "") : it.Nombre,
            Foreground = Texto,
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap
        });
        pila.Children.Add(new TextBlock
        {
            Text = FmtMeta(it),
            Foreground = TextoTenue,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(pila, 1);

        // --- Editar ---
        string idFila = it.Id ?? "";
        var btn = new Button
        {
            Content = T("Editar"),
            Background = Superficie,
            Foreground = Texto,
            BorderBrush = BordeAlto,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinHeight = 40,
            Padding = new Thickness(18, 0, 18, 0),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        btn.Click += (_, __) => AbrirEditor(idFila);
        Grid.SetColumn(btn, 2);

        grid.Children.Add(zonaDot);
        grid.Children.Add(pila);
        grid.Children.Add(btn);

        var fila = new Border
        {
            Background = activo ? VerdeSuave : Superficie,
            BorderBrush = activo ? Verde : Borde,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
        fila.PointerPressed += (_, __) => AbrirEditor(idFila);
        return fila;
    }

    /// <summary>fmtMeta() del JS: tipo · cultivo · densidad objetivo · densidad
    /// de siembra con SU unidad · L/ha. Los ceros no se muestran (el JS los
    /// filtra por falsy).</summary>
    private static string FmtMeta(InsumoWire it)
    {
        var partes = new List<string>();
        if (!string.IsNullOrEmpty(it.Tipo)) partes.Add(it.Tipo);
        if (!string.IsNullOrEmpty(it.Cultivo)) partes.Add(it.Cultivo);
        if (it.Tipo == "semilla" && it.DensidadObjetivoSemM != 0)
            partes.Add(NumJs(it.DensidadObjetivoSemM) + " sem/m");
        if (it.DosisKgha != 0)
        {
            string uni = it.DosisUnidad switch
            {
                "kg_ha"  => "kg/ha",
                "sem_ha" => "sem/ha",
                "sem_m"  => "sem/m",
                _        => "kg/ha",   // `|| 'kg/ha'` del mapa del JS
            };
            partes.Add(NumJs(it.DosisKgha) + " " + uni);
        }
        if (it.DosisLha != 0) partes.Add(NumJs(it.DosisLha) + " L/ha");
        return string.Join(" · ", partes);
    }

    // =========================================================================
    //  editor (openEditor / closeEditor / setTipo del JS)
    // =========================================================================

    private void OnNuevoClick(object? sender, RoutedEventArgs e) => AbrirEditor("");

    private void AbrirEditor(string id)
    {
        InsumoWire? it = null;
        if (!string.IsNullOrEmpty(id))
        {
            foreach (var x in _catalogo.Items ?? new List<InsumoWire>())
                if (string.Equals(x.Id, id, StringComparison.Ordinal)) { it = x; break; }
        }
        _editando = id ?? "";

        // Draft = el ítem o los defaults del JS.
        var d = it ?? new InsumoWire
        {
            Id = "",
            Nombre = "",
            Tipo = "semilla",
            Cultivo = "",
            SingulacionObjetivoPct = 97,
            DosisUnidad = "kg_ha",
            Notas = "",
        };

        SetTexto("EditorTitle", it != null ? T("Editar insumo") : T("Nuevo insumo"));
        SetTexto("EditorIdHint", it != null
            ? T("ID") + ": " + it.Id + " " + T("(no se cambia)")
            : T("El ID se genera automáticamente del nombre al guardar."));

        SetCampo("FNombre",   d.Nombre ?? "");
        SetCampo("FCultivo",  d.Cultivo ?? "");
        // `draft.x || ''` — el cero se muestra vacío (y el placeholder vuelve).
        SetCampo("FDensObj",  Vacio(d.DensidadObjetivoSemM));
        SetCampo("FDensSat",  Vacio(d.DensidadAsumidaSaturadoSemM));
        // `draft.singulacion_objetivo_pct || 97` — el cero vuelve a 97.
        SetCampo("FSingul",   NumJs(d.SingulacionObjetivoPct != 0 ? d.SingulacionObjetivoPct : 97));
        SetCampo("FDropMin",  Vacio(d.DropMinSemM));
        SetCampo("FDropMax",  Vacio(d.DropMaxSemM));
        SetCampo("FDosisKg",  Vacio(d.DosisKgha));
        SetCampo("FPrecioKg", Vacio(d.PrecioUsdKg));
        SetCampo("FDosisL",   Vacio(d.DosisLha));
        SetCampo("FPrecioL",  Vacio(d.PrecioUsdL));
        SetCampo("FNotas",    d.Notas ?? "");

        SetUnidad(string.IsNullOrEmpty(d.DosisUnidad) ? "kg_ha" : d.DosisUnidad);
        SetTipo(string.IsNullOrEmpty(d.Tipo) ? "semilla" : d.Tipo);

        var borrar = this.FindControl<Button>("BtnEliminar");
        if (borrar != null) borrar.IsVisible = it != null;

        var card = this.FindControl<Border>("EditorCard");
        if (card != null)
        {
            card.IsVisible = true;
            // scrollIntoView({block:'start'}) del original: el editor queda a la
            // vista después de que el layout lo midió.
            Dispatcher.UIThread.Post(() => { try { card.BringIntoView(); } catch { } },
                                     DispatcherPriority.Background);
        }
    }

    private void CerrarEditor()
    {
        _editando = null;
        var card = this.FindControl<Border>("EditorCard");
        if (card != null) card.IsVisible = false;
        _ = _client?.TecladoAsync(false);
    }

    private void OnCancelarClick(object? sender, RoutedEventArgs e) => CerrarEditor();

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    private void OnTipoClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t) SetTipo(t);
    }

    /// <summary>setTipo(): pinta el botón elegido y muestra/esconde los bloques.
    /// Reglas EXACTAS del JS — semilla y fertilizante comparten el bloque
    /// sólido; fertilizante y fitosanitario comparten el líquido.</summary>
    private void SetTipo(string t)
    {
        _tipo = string.IsNullOrEmpty(t) ? "semilla" : t;

        PintarTipo("BtnTipoSemilla",       _tipo == "semilla");
        PintarTipo("BtnTipoFertilizante",  _tipo == "fertilizante");
        PintarTipo("BtnTipoFitosanitario", _tipo == "fitosanitario");

        bool semilla = _tipo == "semilla";
        bool solido  = _tipo == "semilla" || _tipo == "fertilizante";
        bool liquido = _tipo == "fertilizante" || _tipo == "fitosanitario";

        Mostrar("CampoCultivo",      semilla);
        Mostrar("CampoDensObj",      semilla);
        Mostrar("CampoDensSat",      semilla);
        Mostrar("CampoSingul",       semilla);
        Mostrar("CampoDropMin",      semilla);
        Mostrar("CampoDropMax",      semilla);
        Mostrar("CampoDosisKg",      solido);
        Mostrar("CampoDosisUnidad",  solido);
        Mostrar("CampoPrecioKg",     solido);
        Mostrar("CampoDosisL",       liquido);
        Mostrar("CampoPrecioL",      liquido);
    }

    private void PintarTipo(string nombre, bool encendido)
    {
        var b = this.FindControl<Button>(nombre);
        if (b == null) return;
        b.Background  = encendido ? Verde : Superficie;
        b.Foreground  = Texto;
        b.BorderBrush = encendido ? Verde : BordeAlto;
        b.FontWeight  = encendido ? FontWeight.SemiBold : FontWeight.Normal;
    }

    // =========================================================================
    //  selector de unidad (el <select> del original)
    // =========================================================================

    private void ArmarPickerUnidad()
    {
        var host = this.FindControl<StackPanel>("PickerLista");
        if (host == null) return;
        host.Children.Clear();

        foreach (var u in Unidades)
        {
            var op = u;
            var b = new Button
            {
                Content = op.Etiqueta,
                Background = Superficie,
                Foreground = Texto,
                BorderBrush = BordeAlto,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinHeight = 48,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            b.Click += (_, __) => { SetUnidad(op.Valor); CerrarPicker(); };
            host.Children.Add(b);
        }
    }

    private void SetUnidad(string valor)
    {
        _dosisUnidad = "kg_ha";
        string etiqueta = "kg/ha";
        foreach (var u in Unidades)
            if (u.Valor == valor) { _dosisUnidad = u.Valor; etiqueta = u.Etiqueta; break; }

        var btn = this.FindControl<Button>("BtnDosisUnidad");
        if (btn != null) btn.Content = etiqueta;
    }

    private void OnUnidadClick(object? sender, RoutedEventArgs e)
    {
        var ov = this.FindControl<Border>("PickerOverlay");
        if (ov != null) ov.IsVisible = true;
    }

    private void OnPickerCancelarClick(object? sender, RoutedEventArgs e) => CerrarPicker();
    private void OnPickerBackdropPressed(object? sender, PointerPressedEventArgs e) => CerrarPicker();
    private void OnPickerCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void CerrarPicker()
    {
        var ov = this.FindControl<Border>("PickerOverlay");
        if (ov != null) ov.IsVisible = false;
    }

    // =========================================================================
    //  guardar (save() del JS)
    // =========================================================================

    private async void OnGuardarClick(object? sender, RoutedEventArgs e)
    {
        string nombre = (this.FindControl<TextBox>("FNombre")?.Text ?? "").Trim();
        if (nombre.Length == 0)
        {
            AbrirAlerta(T("Insumos"), T("Falta el nombre del insumo."));
            return;
        }

        _catalogo.Items ??= new List<InsumoWire>();
        var items = _catalogo.Items;
        string editId = _editando ?? "";

        InsumoWire? dto = null;
        if (editId.Length > 0)
        {
            foreach (var x in items)
                if (string.Equals(x.Id, editId, StringComparison.Ordinal)) { dto = x; break; }
            if (dto == null) { dto = new InsumoWire { Id = editId }; items.Add(dto); }
        }
        else
        {
            dto = new InsumoWire { Id = IdUnico(Slug(nombre), items) };
            items.Add(dto);
        }

        // Se escriben TODOS los campos, estén visibles o no: el JS leía el
        // formulario entero y los escondidos conservan el valor del draft.
        dto.Nombre = nombre;
        dto.Tipo = string.IsNullOrEmpty(_tipo) ? "semilla" : _tipo;
        dto.Cultivo = (this.FindControl<TextBox>("FCultivo")?.Text ?? "").Trim();
        dto.DensidadObjetivoSemM = Num("FDensObj");
        dto.DensidadAsumidaSaturadoSemM = Num("FDensSat");
        // `num(...) || 97`: vacío, 0 o basura vuelven a 97.
        double sing = Num("FSingul");
        dto.SingulacionObjetivoPct = sing != 0 ? sing : 97;
        dto.DropMinSemM = Num("FDropMin");
        dto.DropMaxSemM = Num("FDropMax");
        dto.DosisKgha = Num("FDosisKg");
        dto.DosisUnidad = string.IsNullOrEmpty(_dosisUnidad) ? "kg_ha" : _dosisUnidad;
        dto.DosisLha = Num("FDosisL");
        dto.PrecioUsdKg = Num("FPrecioKg");
        dto.PrecioUsdL = Num("FPrecioL");
        dto.Notas = (this.FindControl<TextBox>("FNotas")?.Text ?? "").Trim();

        await PersistirAsync().ConfigureAwait(true);
        CerrarEditor();
        Render();
    }

    // =========================================================================
    //  eliminar (eliminar() del JS)
    // =========================================================================

    private void OnEliminarClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_editando)) return;   // `if (!state.editando) return;`
        AbrirModal(T("Eliminar insumo"), T("¿Eliminar este insumo del catálogo?"),
                   T("Aceptar"), true, () => _ = EliminarAsync());
    }

    private async Task EliminarAsync()
    {
        string id = _editando ?? "";
        if (id.Length == 0) return;

        var quedan = new List<InsumoWire>();
        foreach (var x in _catalogo.Items ?? new List<InsumoWire>())
            if (!string.Equals(x.Id, id, StringComparison.Ordinal)) quedan.Add(x);
        _catalogo.Items = quedan;

        // Mismo efecto que el JS: si se borró el activo, activo_id queda "" y
        // viaja adentro del MISMO POST del catálogo (no hay POST /activo).
        if (string.Equals(_catalogo.ActivoId, id, StringComparison.Ordinal))
            _catalogo.ActivoId = "";

        await PersistirAsync().ConfigureAwait(true);
        CerrarEditor();
        Render();
    }

    // =========================================================================
    //  diálogo (AgpModal.confirm / AgpModal.alert)
    // =========================================================================

    private void AbrirModal(string titulo, string mensaje, string textoOk, bool destructivo, Action onOk)
    {
        SetTexto("ModalTitulo", titulo);
        SetTexto("ModalMensaje", mensaje);
        var btnOk = this.FindControl<Button>("BtnModalConfirmar");
        if (btnOk != null)
        {
            btnOk.Content = textoOk;
            btnOk.Background = destructivo ? Rojo : Verde;
            btnOk.Foreground = destructivo ? Superficie : Texto;
        }
        var btnCancel = this.FindControl<Button>("BtnModalCancelar");
        if (btnCancel != null) btnCancel.IsVisible = true;
        _modalOnOk = onOk;
        _alertaCierre = null;
        MostrarModal(true);
    }

    /// <summary>alert(): un solo botón, sin "Cancelar" (igual que modal.js).</summary>
    private void AbrirAlerta(string titulo, string mensaje, Action? alCerrar = null)
    {
        SetTexto("ModalTitulo", titulo);
        SetTexto("ModalMensaje", mensaje);
        var btnOk = this.FindControl<Button>("BtnModalConfirmar");
        if (btnOk != null)
        {
            btnOk.Content = T("Aceptar");
            btnOk.Background = Verde;
            btnOk.Foreground = Texto;
        }
        var btnCancel = this.FindControl<Button>("BtnModalCancelar");
        if (btnCancel != null) btnCancel.IsVisible = false;
        _modalOnOk = null;
        _alertaCierre = alCerrar;
        MostrarModal(true);
    }

    private void MostrarModal(bool visible)
    {
        var ov = this.FindControl<Border>("ModalOverlay");
        if (ov != null) ov.IsVisible = visible;
    }

    private void CerrarModal()
    {
        var cierre = _alertaCierre;
        _modalOnOk = null;
        _alertaCierre = null;
        MostrarModal(false);
        var btnCancel = this.FindControl<Button>("BtnModalCancelar");
        if (btnCancel != null) btnCancel.IsVisible = true;
        cierre?.Invoke();
    }

    private void OnModalConfirmarClick(object? sender, RoutedEventArgs e)
    {
        var accion = _modalOnOk;
        CerrarModal();
        accion?.Invoke();
    }

    private void OnModalCancelarClick(object? sender, RoutedEventArgs e) => CerrarModal();
    private void OnModalBackdropPressed(object? sender, PointerPressedEventArgs e) => CerrarModal();
    // La card absorbe el toque para que no llegue al backdrop y cierre el diálogo.
    private void OnModalCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    // =========================================================================
    //  helpers
    // =========================================================================

    private void SetTexto(string nombre, string texto)
    {
        var tb = this.FindControl<TextBlock>(nombre);
        if (tb != null) tb.Text = texto;
    }

    private void SetCampo(string nombre, string texto)
    {
        var tb = this.FindControl<TextBox>(nombre);
        if (tb != null) tb.Text = texto;
    }

    private void Mostrar(string nombre, bool visible)
    {
        var c = this.FindControl<StackPanel>(nombre);
        if (c != null) c.IsVisible = visible;
    }

    /// <summary>num() del JS: parseFloat y 0 si no es finito. La coma se acepta
    /// como separador decimal (convención de los paneles nativos del repo): el
    /// &lt;input type="number"&gt; del browser directamente no dejaba tipearla, y
    /// acá el TextBox sí — sin esto, "14,5" se guardaría como 0.</summary>
    private double Num(string nombre)
    {
        var tb = this.FindControl<TextBox>(nombre);
        return ParseFloatJs(tb?.Text);
    }

    internal static double ParseFloatJs(string? texto)
    {
        string s = (texto ?? "").Trim().Replace(',', '.');
        if (s.Length == 0) return 0;
        // parseFloat toma el prefijo numérico y descarta la cola ("12abc" → 12).
        var m = Regex.Match(s, @"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?");
        if (!m.Success) return 0;
        return double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
               && !double.IsNaN(v) && !double.IsInfinity(v)
            ? v : 0;
    }

    /// <summary>`valor || ''` del JS: el 0 se muestra como campo vacío.</summary>
    private static string Vacio(double v) => v == 0 ? "" : NumJs(v);

    /// <summary>Número → texto como lo concatena JavaScript: sin ceros de
    /// relleno ni separador de miles, y con punto decimal.</summary>
    internal static string NumJs(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "";
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>slug() del JS: minúsculas, sin acentos, todo lo que no sea
    /// [a-z0-9] pasa a "-", y sin guiones en las puntas.</summary>
    internal static string Slug(string? s)
    {
        string t = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        string r = Regex.Replace(sb.ToString(), "[^a-z0-9]+", "-");
        return r.Trim('-');
    }

    /// <summary>uniqueId() del JS: el slug, o "insumo-&lt;ms&gt;" si quedó vacío,
    /// con sufijo -2, -3… hasta que no choque con ningún id existente.</summary>
    internal static string IdUnico(string baseId, List<InsumoWire> items)
    {
        string id = !string.IsNullOrEmpty(baseId)
            ? baseId
            : "insumo-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        int i = 2;
        while (Existe(items, id))
            id = (string.IsNullOrEmpty(baseId) ? "insumo" : baseId) + "-" + (i++).ToString(CultureInfo.InvariantCulture);
        return id;
    }

    private static bool Existe(List<InsumoWire> items, string id)
    {
        if (items == null) return false;
        foreach (var it in items)
            if (string.Equals(it.Id, id, StringComparison.Ordinal)) return true;
        return false;
    }
}
