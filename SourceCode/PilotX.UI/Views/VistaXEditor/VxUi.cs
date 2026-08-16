// ============================================================================
// VxUi.cs — piezas visuales + estado compartido del editor nativo de VistaX.
//
// QUÉ QUEDÓ NATIVO: el equivalente de las clases CSS que usaban vistax.html y
// vistax-insumo.js (card, form-grid, field, pill, btn/primary, editor table) y
// el objeto `state` del JS (implemento central, implemento VistaX, config,
// catálogo de tipos, catálogo de insumos, nodos vistos por MQTT).
// QUÉ SIGUE EN HTML: theme.css/layout.css y los JS, que sirven a la PWA del
// celular — la página pages/vistax.html no se toca.
//
// Paleta CLARA de la doctrina (PORTING-AVALONIA §2): card #FAFBFA, superficies
// blancas, bordes #C5CFC5, verde #4ABA3E SOLO como acento, números en Consolas.
// Es la misma paleta que QxUi (editor QuantiX); se repite acá a propósito para
// no acoplar dos productos por un archivo de estilos.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.VistaXEditor;

/// <summary>Pantalla interna del editor. `Rebuild()` reconstruye el árbol
/// entero; solo se llama por acción del operario o al entrar a la pantalla —
/// reconstruir en el tick tira el foco del TextBox y cierra el teclado.</summary>
public abstract class VxTab : StackPanel
{
    protected readonly VxCtx C;

    protected VxTab(VxCtx c)
    {
        C = c;
        Spacing = 12;
    }

    public abstract void Rebuild();

    /// <summary>Refresco liviano en cada tick (3 s): solo lo que depende de los
    /// nodos vistos por MQTT.</summary>
    public virtual void Live() { }

    /// <summary>Carga diferida al entrar (lazy-load, igual que el HTML).</summary>
    public virtual Task AlEntrarAsync() => Task.CompletedTask;

    public virtual Task AlSalirAsync() => Task.CompletedTask;
}

/// <summary>Estado compartido por las tres pantallas + los servicios que
/// presta el host (avisos, navegación, calibración).</summary>
public sealed class VxCtx
{
    public VistaXClient Client = null!;

    // ---- servicios del host -----------------------------------------------
    /// <summary>Aviso corto → toast del host. NUNCA modal (ShowDialog traba la
    /// cabina); reemplaza a los AgpModal.alert del JS.</summary>
    public Action<string>? Aviso;
    /// <summary>Los paneles no navegan solos: el host decide.</summary>
    public Action? AbrirInsumos;
    public Action? AbrirConfigCentral;
    /// <summary>Arranca la ventana de captura (modo, id de insumo, nombre para
    /// el subtítulo). El overlay y el polling de 250 ms viven en el panel.</summary>
    public Func<string, string, string, Task>? IniciarCalibracion;
    /// <summary>El panel avisa que se guardó un valor al insumo, para que la
    /// pantalla de insumo recargue el catálogo y muestre el metadato nuevo.</summary>
    public Action? AlAplicarCalibracion;

    // ---- estado ------------------------------------------------------------
    public VxImplementoCentral?  Central;
    public VxImplementoCargado?  Imp;
    public VxConfig?             Cfg;
    public List<VxTipoSensor>    Tipos  = new();
    public List<VistaXNodoLive>  Nodos  = new();
    public VxInsumoCatalogo?     Insumos;

    public string InsumoActivoId => Insumos?.ActivoId ?? "";

    public VxInsumo? InsumoActivo()
    {
        var id = InsumoActivoId;
        if (string.IsNullOrEmpty(id) || Insumos?.Items == null) return null;
        foreach (var i in Insumos.Items)
            if (string.Equals(i.Id, id, StringComparison.Ordinal)) return i;
        return null;
    }

    /// <summary>Trenes del implemento CENTRAL (fuente única). Fallback al tren 0
    /// para que el desplegable del mapeo nunca quede vacío.</summary>
    public List<VxCentralTren> TrenesCentral()
    {
        var ts = Central?.Trenes;
        if (ts != null && ts.Count > 0) return ts;
        return new List<VxCentralTren> { new VxCentralTren { Id = 0, Nombre = "Tren 0" } };
    }

    /// <summary>Catálogo de tipos de sensor con el fallback estático del JS por
    /// si el endpoint no contesta.</summary>
    public List<VxTipoSensor> TiposConFallback()
    {
        if (Tipos.Count > 0) return Tipos;
        var l = new List<VxTipoSensor>();
        void Add(string id, string et) => l.Add(new VxTipoSensor { Id = id, Etiqueta = et });
        Add("semilla", "Semilla");
        Add("fertilizante", "Fertilizante");
        Add("rotacion_eje", "Rotación de eje");
        Add("turbina", "Turbina");
        Add("bajada_herramienta", "Bajada de herramienta");
        Add("tolva_vacia", "Tolva vacía");
        Add("tolva_llena", "Tolva llena");
        Add("presion", "Presión");
        Add("final_carrera", "Final de carrera");
        return l;
    }

    /// <summary>UID del primer nodo online (o el primero a secas). Pre-siembra
    /// la fila nueva del mapeo para evitar el "tengo un solo nodo y aun así me
    /// pide elegirlo".</summary>
    public string PrimerNodoUid()
    {
        foreach (var n in Nodos) if (n.Online && !string.IsNullOrEmpty(n.Uid)) return n.Uid!;
        foreach (var n in Nodos) if (!string.IsNullOrEmpty(n.Uid)) return n.Uid!;
        return "";
    }
}

public static class VxUi
{
    public static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    public static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    public static readonly IBrush BgSuave    = new SolidColorBrush(Color.Parse("#F5F7F4"));
    public static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    public static readonly IBrush BordeSuave = new SolidColorBrush(Color.Parse("#E2E7E2"));
    public static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    public static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    public static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#7A857B"));
    public static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    public static readonly IBrush Ok         = new SolidColorBrush(Color.Parse("#3D9A33"));
    public static readonly IBrush Warn       = new SolidColorBrush(Color.Parse("#B98A2E"));
    public static readonly IBrush Err        = new SolidColorBrush(Color.Parse("#D0504A"));
    public static readonly IBrush Dim        = new SolidColorBrush(Color.Parse("#8A958B"));
    public static readonly IBrush BgError    = new SolidColorBrush(Color.Parse("#FBECEC"));

    public static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    private static string T(string s) => PilotX.Cockpit.Bars.Traductor.T(s);

    // ---- textos ------------------------------------------------------------
    //
    // Los títulos/etiquetas pasan por Traductor.T() ACÁ, en el origen: los
    // rebuilds parciales no vuelven a llamar a Traductor.Aplicar y un texto que
    // no se traduce al crearse queda en castellano con la pantalla en inglés.

    public static TextBlock Titulo(string t) => new TextBlock
    {
        Text = T(t), Foreground = Texto, FontSize = 15, FontWeight = FontWeight.Bold,
    };

    public static TextBlock Sub(string t) => new TextBlock
    {
        Text = T(t), Foreground = TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Etiqueta(string t) => new TextBlock
    {
        Text = T(t), Foreground = TextoDim, FontSize = 10, FontWeight = FontWeight.SemiBold,
    };

    public static TextBlock Valor(string t) => new TextBlock
    {
        Text = t, Foreground = Texto, FontSize = 16, FontWeight = FontWeight.SemiBold,
        FontFamily = Mono,
    };

    public static TextBlock Estado() => new TextBlock
    {
        Text = "", Foreground = TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460,
    };

    /// <summary>Pinta el renglón de estado del pie. `estado`: "" | "ok" | "err".
    /// El detalle de AGP-CFG-001 puede ser largo: va con wrap dentro de la card,
    /// nunca estirando el panel.</summary>
    public static void SetEstado(TextBlock? t, string texto, string estado = "")
    {
        if (t == null) return;
        t.Text = T(texto);
        t.Foreground = estado == "ok" ? Ok : estado == "err" ? Err : TextoMuted;
    }

    // ---- contenedores ------------------------------------------------------

    public static Border Card(Control contenido) => new Border
    {
        Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
        Child = contenido,
    };

    /// <summary>Card con el borde izquierdo de acento (el banner del HTML).</summary>
    public static Border Banner(Control contenido) => new Border
    {
        Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(3, 1, 1, 1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
        Child = contenido,
    };

    public static Border Bloque(string titulo, Control contenido)
    {
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(Etiqueta(titulo));
        sp.Children.Add(contenido);
        return new Border
        {
            Background = BgSuave, BorderBrush = BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 10),
            Child = sp,
        };
    }

    /// <summary>KPI de solo lectura: etiqueta chica arriba, número Consolas.</summary>
    public static Border Kpi(string etiqueta, string valor, string unidad = "")
    {
        var sp = new StackPanel { Spacing = 2, MinWidth = 120 };
        sp.Children.Add(Etiqueta(etiqueta));
        var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        fila.Children.Add(Valor(valor));
        if (!string.IsNullOrEmpty(unidad))
            fila.Children.Add(new TextBlock
            {
                Text = unidad, Foreground = TextoDim, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2),
            });
        sp.Children.Add(fila);
        return new Border
        {
            Background = BgSuave, BorderBrush = BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 8), Child = sp,
        };
    }

    /// <summary>Campo con etiqueta arriba (el ".field" del HTML).</summary>
    public static StackPanel Campo(string etiqueta, Control control, double minAncho = 160)
    {
        var sp = new StackPanel { Spacing = 3, MinWidth = minAncho, Margin = new Thickness(0, 0, 10, 8) };
        sp.Children.Add(Etiqueta(etiqueta));
        sp.Children.Add(control);
        return sp;
    }

    public static WrapPanel Grilla() => new WrapPanel { Orientation = Orientation.Horizontal };

    public static StackPanel Fila(double spacing = 8) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = spacing,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- botones -----------------------------------------------------------

    public static Button Boton(string txt, Action? click = null, bool primario = false, bool peligro = false)
    {
        var b = new Button
        {
            Content = T(txt),
            MinHeight = 42, MinWidth = 44,
            Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(8),
            FontSize = 13,
            FontWeight = primario ? FontWeight.SemiBold : FontWeight.Normal,
            Background = primario ? Verde : BgFila,
            Foreground = primario ? Brushes.White : (peligro ? Err : Texto),
            BorderBrush = peligro ? Err : Borde,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (click != null) b.Click += (_, __) => click();
        return b;
    }

    /// <summary>Cambia el texto del botón por `txt` durante 1,1 s y vuelve al
    /// original (el `flash()` de vistax-insumo.js).</summary>
    public static void Flash(Button? b, string txt)
    {
        if (b == null) return;
        var original = b.Content;
        b.Content = T(txt);
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        timer.Tick += (_, __) =>
        {
            try { timer.Stop(); } catch { }
            b.Content = original;
        };
        timer.Start();
    }

    public static Border Pill(string texto, IBrush color)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sp.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 9, Height = 9, Fill = color, VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = T(texto), Foreground = color, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center, Child = sp,
        };
    }

    // ---- entradas ----------------------------------------------------------

    public static ComboBox Combo(double minAncho = 160) => new ComboBox
    {
        MinHeight = 42, MinWidth = minAncho, FontSize = 13,
        Background = BgFila, Foreground = Texto,
        BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6, 8, 6),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>TextBox que pide el teclado nativo de PilotX al enfocarse (misma
    /// señal HTTP que mandan las páginas del Hub). Sin esto el campo queda
    /// ineditable en la pantalla táctil de cabina.</summary>
    public static TextBox Entrada(VistaXClient client, string valor, bool numerico,
                                  string titulo, double ancho = 160)
    {
        var t = new TextBox
        {
            Text = valor, Width = ancho, MinHeight = 42, FontSize = 13,
            Background = BgFila, Foreground = Texto,
            BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 6, 9, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        t.GotFocus  += (_, __) => _ = client.TecladoAsync(true, numerico, titulo);
        t.LostFocus += (_, __) => _ = client.TecladoAsync(false);
        return t;
    }

    public static CheckBox Check(string texto, bool valor) => new CheckBox
    {
        Content = T(texto), IsChecked = valor, MinHeight = 40, FontSize = 13,
        Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- parseo tolerante --------------------------------------------------

    public static double LeerDouble(TextBox? t, double fallback)
    {
        if (t == null) return fallback;
        var s = (t.Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public static int LeerInt(TextBox? t, int fallback)
    {
        double v = LeerDouble(t, double.NaN);
        return double.IsNaN(v) ? fallback : (int)Math.Round(v);
    }

    public static string Num(double v, int dec)
        => v.ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture);
}
