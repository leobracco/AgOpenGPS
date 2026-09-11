// ============================================================================
// VxInsumoTab.cs — pantalla "Insumo & calibración" del editor nativo de VistaX.
//
// QUÉ QUEDÓ NATIVO: todo vistax-insumo.js — el desplegable del insumo activo
// (GET /api/insumos + POST /api/insumos/activo), los metadatos del insumo, el
// límite del sensor (read-modify-write sobre /api/vistax/implemento) y el
// disparo de la ventana de captura de 5 s.
// QUÉ SIGUE EN HTML: el archivo vistax-insumo.js queda intacto — lo usa la PWA
// del celular. El catálogo de insumos en sí NO se porta acá: es su propia
// pantalla (pages/insumos.html) y se abre desde el host.
//
// El overlay de calibración vive en el panel (VistaXEditorPanel), no acá: tiene
// que tapar la card entera y sobre el mapa GL los Flyout no se dibujan.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.VistaXEditor;

public sealed class VxInsumoTab : VxTab
{
    private ComboBox?  _cboInsumo;
    private TextBlock? _meta;
    private TextBox?   _txtMax;
    private Button?    _btnGuardarMax;
    private TextBlock? _estado;
    private bool       _cargando;

    /// <summary>Opción del desplegable: id vacío = "sin insumo activo".</summary>
    private sealed class OpcionInsumo
    {
        public string Id = "";
        public string Texto = "";
        public override string ToString() => Texto;
    }

    public VxInsumoTab(VxCtx c) : base(c) { }

    public override async Task AlEntrarAsync()
    {
        // Lazy-load igual que el HTML: recién al entrar a la pantalla.
        C.Insumos = await C.Client.GetInsumosAsync().ConfigureAwait(true);
        C.Imp ??= await C.Client.GetImplementoAsync().ConfigureAwait(true);
        Rebuild();
    }

    private async Task RecargarInsumosAsync()
    {
        C.Insumos = await C.Client.GetInsumosAsync().ConfigureAwait(true);
        Rebuild();
    }

    /// <summary>Lo llama el panel cuando la calibración guardó al insumo.</summary>
    public void RecargarInsumos() => _ = RecargarInsumosAsync();

    private double MaxSensor()
        => (C.Imp?.Implemento?.Setup?.MaxDensidadSensor ?? 0) > 0
           ? C.Imp!.Implemento.Setup.MaxDensidadSensor : 20;

    public override void Rebuild()
    {
        Children.Clear();

        var cols = new WrapPanel { Orientation = Orientation.Horizontal };
        cols.Children.Add(CardInsumoActivo());
        cols.Children.Add(CardDetectar());
        Children.Add(cols);

        _estado = VxUi.Estado();
        Children.Add(_estado);

        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    // =======================================================================
    //  Card "Insumo activo"
    // =======================================================================

    private Control CardInsumoActivo()
    {
        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(VxUi.Titulo("Insumo activo"));
        sp.Children.Add(VxUi.Sub(
            "VistaX usa el insumo activo del catálogo para conocer la densidad objetivo "
            + "y la densidad asumida al saturar (modo flujo). El catálogo se edita en "
            + "la pantalla Insumos."));

        _cboInsumo = VxUi.Combo(240);
        var opciones = new List<OpcionInsumo>
        {
            new OpcionInsumo { Id = "", Texto = "— Sin insumo activo —" },
        };
        int sel = 0;
        var items = C.Insumos?.Items;
        if (items != null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                opciones.Add(new OpcionInsumo
                {
                    Id = it.Id,
                    Texto = string.IsNullOrEmpty(it.Nombre) ? it.Id : it.Nombre!,
                });
                if (string.Equals(it.Id, C.InsumoActivoId, StringComparison.Ordinal))
                    sel = opciones.Count - 1;
            }
        }
        _cargando = true;
        _cboInsumo.ItemsSource = opciones;
        _cboInsumo.SelectedIndex = sel;
        _cargando = false;
        _cboInsumo.SelectionChanged += (_, __) =>
        {
            if (_cargando) return;
            if (_cboInsumo?.SelectedItem is OpcionInsumo o) _ = ActivarInsumoAsync(o.Id);
        };
        sp.Children.Add(VxUi.Campo("Insumo", _cboInsumo, 240));

        _meta = new TextBlock
        {
            Foreground = VxUi.TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        PintarMeta();
        sp.Children.Add(_meta);

        sp.Children.Add(VxUi.Boton("Catálogo de insumos", () => C.AbrirInsumos?.Invoke()));

        return new Border
        {
            Background = VxUi.BgFila, BorderBrush = VxUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
            MinWidth = 330, MaxWidth = 430, Margin = new Thickness(0, 0, 10, 10),
            Child = sp,
        };
    }

    private void PintarMeta()
    {
        if (_meta == null) return;
        var a = C.InsumoActivo();
        if (a == null)
        {
            _meta.Text = PilotX.Cockpit.Bars.Traductor.T(
                "Ningún insumo activo. Elegí uno para que VistaX use sus densidades.");
            _meta.Foreground = VxUi.TextoDim;
            _meta.FontStyle = FontStyle.Italic;
            return;
        }
        _meta.FontStyle = FontStyle.Normal;
        _meta.Foreground = VxUi.TextoMuted;
        var partes = new List<string>();
        if (a.DensidadObjetivoSemM > 0)
            partes.Add(PilotX.Cockpit.Bars.Traductor.T("Objetivo") + ": "
                       + VxUi.Num(a.DensidadObjetivoSemM, 1) + " sem/m");
        if (a.DensidadAsumidaSaturadoSemM > 0)
            partes.Add(PilotX.Cockpit.Bars.Traductor.T("Modo flujo") + ": "
                       + VxUi.Num(a.DensidadAsumidaSaturadoSemM, 1) + " sem/m");
        if (a.SingulacionObjetivoPct > 0)
            partes.Add(PilotX.Cockpit.Bars.Traductor.T("Singulación obj") + ": "
                       + VxUi.Num(a.SingulacionObjetivoPct, 0) + " %");
        if (!string.IsNullOrEmpty(a.Cultivo))
            partes.Add(PilotX.Cockpit.Bars.Traductor.T("Cultivo") + ": " + a.Cultivo);
        _meta.Text = string.Join("  ·  ", partes);
    }

    private async Task ActivarInsumoAsync(string id)
    {
        var r = await C.Client.SetInsumoActivoAsync(id).ConfigureAwait(true);
        if (!r.Ok) C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo activar el insumo")
                                   + ": " + r.Texto());
        await RecargarInsumosAsync().ConfigureAwait(true);
    }

    // =======================================================================
    //  Card "Detectar densidad de siembra"
    // =======================================================================

    private Control CardDetectar()
    {
        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(VxUi.Titulo("Detectar densidad de siembra"));
        sp.Children.Add(VxUi.Sub(
            "Con la sembradora trabajando, tocá el botón. Durante 5 s VistaX promedia los "
            + "sem/m de los surcos. Si supera el límite del sensor ("
            + VxUi.Num(MaxSensor(), 1) + " sem/m) sugerimos guardar como modo flujo."));

        var bObj = VxUi.Boton("◉ Detectar densidad — 5 s", () => _ = CalibrarAsync("objetivo"), primario: true);
        bObj.MinHeight = 48;
        bObj.MinWidth = 230;
        var bSat = VxUi.Boton("⟁ Configurar modo flujo — 5 s", () => _ = CalibrarAsync("saturado"));
        bSat.MinHeight = 48;
        bSat.MinWidth = 230;
        var botones = new WrapPanel { Orientation = Orientation.Horizontal };
        bObj.Margin = new Thickness(0, 0, 8, 8);
        bSat.Margin = new Thickness(0, 0, 8, 8);
        botones.Children.Add(bObj);
        botones.Children.Add(bSat);
        sp.Children.Add(botones);

        _txtMax = VxUi.Entrada(C.Client, VxUi.Num(MaxSensor(), 1), true,
                               "Límite del sensor (sem/m)", 130);
        _btnGuardarMax = VxUi.Boton("Guardar límite", () => _ = GuardarMaxAsync());
        var fila = VxUi.Fila();
        fila.Children.Add(VxUi.Campo("Límite del sensor (sem/m)", _txtMax, 130));
        fila.VerticalAlignment = VerticalAlignment.Bottom;
        var filaConBoton = new WrapPanel { Orientation = Orientation.Horizontal };
        filaConBoton.Children.Add(fila);
        _btnGuardarMax.Margin = new Thickness(0, 0, 0, 8);
        filaConBoton.Children.Add(_btnGuardarMax);
        sp.Children.Add(filaConBoton);

        return new Border
        {
            Background = VxUi.BgFila, BorderBrush = VxUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
            MinWidth = 330, MaxWidth = 470, Margin = new Thickness(0, 0, 0, 10),
            Child = sp,
        };
    }

    /// <summary>Guard del JS: sin insumo activo no se calibra (el resultado no
    /// tendría dónde guardarse).</summary>
    private async Task CalibrarAsync(string modo)
    {
        string id = (_cboInsumo?.SelectedItem as OpcionInsumo)?.Id ?? "";
        if (string.IsNullOrEmpty(id))
        {
            C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
                "No hay insumo activo. Elegí uno antes de calibrar."));
            return;
        }
        string nombre = (_cboInsumo?.SelectedItem as OpcionInsumo)?.Texto ?? "";
        if (C.IniciarCalibracion != null)
            await C.IniciarCalibracion(modo, id, nombre).ConfigureAwait(true);
    }

    /// <summary>Read-modify-write: se vuelve a pedir el implemento ENTERO justo
    /// antes del PUT para no pisar lo que se haya guardado desde la pantalla
    /// Implemento (o desde el celular con la página HTML).</summary>
    private async Task GuardarMaxAsync()
    {
        double val = VxUi.LeerDouble(_txtMax, double.NaN);
        if (!(val > 0 && val < 500))
        {
            C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Valor inválido."));
            return;
        }

        var cargado = await C.Client.GetImplementoAsync().ConfigureAwait(true);
        if (cargado == null)
        {
            VxUi.SetEstado(_estado, "No se pudo cargar el implemento", "err");
            return;
        }
        cargado.Implemento.Setup ??= new VxSetup();
        cargado.Implemento.Setup.MaxDensidadSensor = val;

        var r = await C.Client.PutImplementoAsync(cargado.Implemento).ConfigureAwait(true);
        if (!r.Ok)
        {
            VxUi.SetEstado(_estado, r.Texto(), "err");
            return;
        }
        C.Imp = cargado;
        // El texto de ayuda menciona el límite: se reconstruye para que quede
        // coherente con lo recién guardado. OJO EL ORDEN: Rebuild() crea un
        // botón NUEVO, así que el flash tiene que ir DESPUÉS — al revés el
        // "Guardado" se pintaba sobre el botón viejo, que se tiraba en el mismo
        // renglón, y el operario no veía ninguna confirmación.
        Rebuild();
        VxUi.SetEstado(_estado, "", "");
        VxUi.Flash(_btnGuardarMax, "Guardado");
    }
}
