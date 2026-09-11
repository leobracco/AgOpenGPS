// ============================================================================
// ResumenTab.cs — pestaña "Resumen" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=summary` (la sección data-tab="summary" +
// tabs.summary de config.js), que a su vez replicaba
// ConfigSummaryControl.UpdateSummary del FormConfig original.
//
// QUÉ QUEDÓ NATIVO: los 9 datos de la carta (perfil, unidades, ancho,
// secciones, offset, overlap, lookahead, ancho tram, entre ejes) con sus
// formatos exactos.
// QUÉ SIGUE EN HTML: la página config.html entera (la usa la PWA del celular)
// y las pestañas que todavía no se portaron, que el shell abre por WebView.
//
// Es una pestaña de SOLO LECTURA: cero inputs, cero validación, cero POST.
// Por eso TieneGuardar es false y el shell no muestra el botón Guardar. En el
// HTML ese botón se ve también acá y al tocarlo dice "Guardado ✔" sin guardar
// nada: es un quirk heredado del shell de 18 pestañas y NO se porta — decirle
// "guardado" a un operario cuando no se guardó nada es peor que no tener el
// botón.
//
// Los signos se muestran tal cual vienen (offset a la izquierda = negativo,
// gap = negativo): el signo es información, no un defecto visual.
// ============================================================================

using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class ResumenTab : ConfigTab
{
    private TextBlock? _titulo;
    private TextBlock? _vUnidades, _vAncho, _vSecciones, _vTram;
    private TextBlock? _vOffset, _vOverlap, _vLookahead, _vWheelbase;

    /// <summary>Estado con el que se armó el árbol, para saber si el refresco
    /// de fondo tiene que reconstruir (aparece/desaparece el aviso) o alcanza
    /// con repintar los valores.</summary>
    private int _estadoPintado = -1;

    public ResumenTab(CfgCtx c) : base(c) { }

    /// <summary>Solo lectura: no hay nada que guardar (ver cabecera).</summary>
    public override bool TieneGuardar => false;

    /// <summary>`enter()` del HTML: repinta con el snapshot que ya tiene el
    /// shell. No hace fetch propio ni polling.</summary>
    public override Task AlEntrarAsync()
    {
        Rebuild();
        return Task.CompletedTask;
    }

    /// <summary>`leave()` del HTML: Promise.resolve(true). Nunca bloquea la
    /// navegación y nunca guarda nada.</summary>
    public override Task<bool> AlSalirAsync() => Task.FromResult(true);

    public override void Rebuild()
    {
        Children.Clear();
        _estadoPintado = EstadoActual();

        var carta = new StackPanel { Spacing = 10 };

        // ---- título: "Perfil: <nombre>" (el nombre NO se traduce: es dato) --
        _titulo = new TextBlock
        {
            Text = TituloPerfil(),
            Foreground = CfgUi.Texto, FontSize = 18, FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        };
        carta.Children.Add(_titulo);

        // ---- nota (con "Perfiles" en negrita, igual que el HTML; NO navega) -
        var nota = new TextBlock
        {
            Foreground = CfgUi.TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        nota.Inlines = new InlineCollection
        {
            new Run(PilotX.Cockpit.Bars.Traductor.T(
                "Las configuraciones del vehículo se agrupan en «perfiles» — se crean y cargan desde la página ")),
            new Run(PilotX.Cockpit.Bars.Traductor.T("Perfiles")) { FontWeight = FontWeight.Bold },
            new Run("."),
        };
        carta.Children.Add(nota);

        // ---- los tres estados de la doctrina -------------------------------
        if (C.SinDatos)
        {
            carta.Children.Add(new TextBlock
            {
                Text = PilotX.Cockpit.Bars.Traductor.T("PilotX no responde — todavía no llegaron los datos."),
                Foreground = CfgUi.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (C.ServicioCaido)
        {
            carta.Children.Add(CfgUi.ChipError("Servicio de configuración no disponible", "AGP-NET-201"));
        }

        // ---- grilla de datos (la .sumgrid del HTML, en dos columnas) -------
        var kv = CfgUi.GrillaKv(2);
        _vUnidades  = CfgUi.AgregarKv(kv, 0, 0, "Unidades",   "—");
        _vAncho     = CfgUi.AgregarKv(kv, 1, 0, "Ancho",      "—");
        _vSecciones = CfgUi.AgregarKv(kv, 2, 0, "Secciones",  "—");
        _vTram      = CfgUi.AgregarKv(kv, 3, 0, "Ancho tram", "—");

        _vOffset    = CfgUi.AgregarKv(kv, 0, 2, "Offset",     "—");
        _vOverlap   = CfgUi.AgregarKv(kv, 1, 2, "Overlap",    "—");
        _vLookahead = CfgUi.AgregarKv(kv, 2, 2, "Lookahead",  "—");
        _vWheelbase = CfgUi.AgregarKv(kv, 3, 2, "Entre ejes", "—");

        kv.Margin = new Thickness(0, 4, 0, 0);
        carta.Children.Add(kv);

        Children.Add(CfgUi.Carta(carta));

        PintarValores();
    }

    /// <summary>Refresco de fondo (3 s). Como la pestaña no tiene ni un campo
    /// editable, repintar acá no le saca el foco a nadie — y es lo que evita
    /// mostrar datos viejos después de tocar algo en una pestaña HTML.</summary>
    public override void Live()
    {
        if (_estadoPintado != EstadoActual()) { Rebuild(); return; }
        PintarValores();
    }

    // ---- pintura -----------------------------------------------------------

    private void PintarValores()
    {
        if (_titulo != null) _titulo.Text = TituloPerfil();

        var sec  = C.Snap?.Secciones;
        var off  = C.Snap?.Offset;
        var tim  = C.Snap?.Timing;
        var tram = C.Snap?.Tram;
        var dim  = C.Snap?.Dimensiones;

        if (_vUnidades  != null) _vUnidades.Text  = Unidades();
        if (_vAncho     != null) _vAncho.Text     = C.FmtMedium(sec?.ToolWidth);
        if (_vSecciones != null) _vSecciones.Text = Secciones();
        if (_vTram      != null) _vTram.Text      = C.FmtMedium(tram?.TramWidth);

        if (_vOffset    != null) _vOffset.Text    = C.FmtSmall(off?.ToolOffset);
        if (_vOverlap   != null) _vOverlap.Text   = C.FmtSmall(off?.ToolOverlap);
        if (_vLookahead != null) _vLookahead.Text = Lookahead(tim?.LookAheadOn);
        if (_vWheelbase != null) _vWheelbase.Text = C.FmtSmall(dim?.Wheelbase);
    }

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    private string TituloPerfil()
    {
        var p = C.Snap?.PerfilActivo;
        string nombre = string.IsNullOrWhiteSpace(p) ? "—" : p!;
        return PilotX.Cockpit.Bars.Traductor.T("Perfil") + ": " + nombre;
    }

    private string Unidades()
    {
        if (C.Snap == null || !C.Snap.Ok) return "—";
        return PilotX.Cockpit.Bars.Traductor.T(C.Snap.IsMetric ? "Métrico" : "Imperial");
    }

    /// <summary>Entero pelado, sin unidad: en modo secciones individuales sale
    /// num_sections y en modo zonas num_sections_multi (igual que el HTML).</summary>
    private string Secciones()
    {
        var s = C.Snap?.Secciones;
        if (s == null) return "—";
        int n = s.IsSectionsNotZones ? s.NumSections : s.NumSectionsMulti;
        return n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Valor CRUDO en segundos, sin redondeo extra (así lo muestra el
    /// HTML: `snap.timing.look_ahead_on + ' s'`).</summary>
    private static string Lookahead(double? s)
        => s == null ? "—" : CfgCtx.NumJs(s) + " s";
}
