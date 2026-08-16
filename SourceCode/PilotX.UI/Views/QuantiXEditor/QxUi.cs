// ============================================================================
// QxUi.cs — piezas visuales compartidas por las 6 tabs del editor QuantiX.
//
// Paleta CLARA de la doctrina (PORTING-AVALONIA §2): card #FAFBFA, superficies
// blancas, bordes #C5CFC5, verde #4ABA3E SOLO como acento, números en Consolas.
// Nada oscuro ni decorativo — esto se mira con sol de frente.
//
// QUÉ QUEDÓ NATIVO: el equivalente de las clases CSS que usaba quantix.html
// (card, kv, field, pill, chip, send-msg, motor-chip, btn/primary/danger).
// QUÉ SIGUE EN HTML: theme.css/layout.css, que sirven a la PWA del celular.
// ============================================================================

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.QuantiXEditor;

public abstract class QxTab : StackPanel
{
    protected readonly QxEditorCtx C;

    protected QxTab(QxEditorCtx c)
    {
        C = c;
        Spacing = 10;
    }

    /// <summary>Rebuild COMPLETO del árbol. Solo por acción del operario o al
    /// entrar a la tab: reconstruir en el tick live tira el foco del TextBox y
    /// cierra el teclado nativo (la versión Avalonia del "DOM abajo del dedo").</summary>
    public abstract void Rebuild();

    /// <summary>Refresco de SOLO los labels/pills live. Se llama en cada tick.</summary>
    public virtual void Live() { }

    /// <summary>Se entra a la tab (carga diferida de datos, etc.).</summary>
    public virtual Task AlEntrarAsync() => Task.CompletedTask;

    /// <summary>Se sale de la tab o se cierra el panel: acá tiene que parar
    /// cualquier motor que esta tab haya puesto a girar.</summary>
    public virtual Task AlSalirAsync() => Task.CompletedTask;
}

public static class QxUi
{
    public static readonly IBrush BgPanel   = new SolidColorBrush(Color.Parse("#FAFBFA"));
    public static readonly IBrush BgFila    = new SolidColorBrush(Color.Parse("#FFFFFF"));
    public static readonly IBrush BgFilaSel = new SolidColorBrush(Color.Parse("#DCEFD8"));
    public static readonly IBrush BgSuave   = new SolidColorBrush(Color.Parse("#F5F7F4"));
    public static readonly IBrush Borde     = new SolidColorBrush(Color.Parse("#C5CFC5"));
    public static readonly IBrush BordeSuave= new SolidColorBrush(Color.Parse("#E2E7E2"));
    public static readonly IBrush Texto     = new SolidColorBrush(Color.Parse("#101612"));
    public static readonly IBrush TextoMuted= new SolidColorBrush(Color.Parse("#535E54"));
    public static readonly IBrush TextoDim  = new SolidColorBrush(Color.Parse("#7A857B"));
    public static readonly IBrush Verde     = new SolidColorBrush(Color.Parse("#4ABA3E"));
    public static readonly IBrush Ok        = new SolidColorBrush(Color.Parse("#3D9A33"));
    public static readonly IBrush Warn      = new SolidColorBrush(Color.Parse("#B98A2E"));
    public static readonly IBrush Err       = new SolidColorBrush(Color.Parse("#D0504A"));
    public static readonly IBrush Dim       = new SolidColorBrush(Color.Parse("#8A958B"));

    public static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    // ---- textos ------------------------------------------------------------

    // Títulos, subtítulos, etiquetas y chips pasan por Traductor.T() ACÁ, en
    // el origen: los rebuilds parciales de las tabs (lista de motores, tabla,
    // biblioteca) no vuelven a llamar a Traductor.Aplicar, así que un texto
    // que no se traduce al crearse se quedaba en castellano con la pantalla
    // en inglés o portugués. T() con el idioma en "es" no hace nada.

    public static TextBlock Titulo(string t) => new TextBlock
    {
        Text = PilotX.Cockpit.Bars.Traductor.T(t),
        Foreground = Texto, FontSize = 15, FontWeight = FontWeight.Bold,
    };

    public static TextBlock Sub(string t) => new TextBlock
    {
        Text = PilotX.Cockpit.Bars.Traductor.T(t),
        Foreground = TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Etiqueta(string t) => new TextBlock
    {
        Text = PilotX.Cockpit.Bars.Traductor.T(t),
        Foreground = TextoDim, FontSize = 10, FontWeight = FontWeight.SemiBold,
    };

    public static TextBlock Valor(string t) => new TextBlock
    {
        Text = t, Foreground = Texto, FontSize = 13, FontWeight = FontWeight.SemiBold,
        FontFamily = Mono,
    };

    public static TextBlock Mono2(string t) => new TextBlock
    {
        Text = t, Foreground = TextoMuted, FontSize = 11, FontFamily = Mono,
    };

    /// <summary>Mensaje de resultado de una acción (el "send-msg" del HTML).</summary>
    public static TextBlock Msg() => new TextBlock
    {
        Text = "", Foreground = TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520,
    };

    public static void SetMsg(TextBlock? t, string texto, string estado = "")
    {
        if (t == null) return;
        t.Text = PilotX.Cockpit.Bars.Traductor.T(texto);
        t.Foreground = estado == "ok" ? Ok : estado == "err" ? Err : TextoMuted;
    }

    /// <summary>Saca un control de su padre anterior.
    ///
    /// Las tabs reusan controles (la tira de surcos, el mensaje de resultado,
    /// la vista previa) entre rebuilds, pero los CONTENEDORES intermedios se
    /// arman de nuevo cada vez. Avalonia revienta con "The control already has
    /// a visual parent" si se agrega un control que todavía cuelga del
    /// contenedor viejo — y limpiar los hijos del panel raíz solo desengancha
    /// a los hijos DIRECTOS, no a los anidados.</summary>
    public static T Soltar<T>(T c) where T : Control
    {
        var p = c.Parent;
        if (p is Panel panel) panel.Children.Remove(c);
        else if (p is Border b && ReferenceEquals(b.Child, c)) b.Child = null;
        else if (p is ContentControl cc && ReferenceEquals(cc.Content, c)) cc.Content = null;
        else if (p is Decorator d && ReferenceEquals(d.Child, c)) d.Child = null;
        return c;
    }

    // ---- contenedores ------------------------------------------------------

    public static Border Card(Control contenido, bool atenuada = false) => new Border
    {
        Background = BgFila,
        BorderBrush = Borde,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12),
        Opacity = atenuada ? 0.55 : 1.0,
        Child = contenido,
    };

    public static Border Bloque(string titulo, Control contenido)
    {
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(titulo), Foreground = TextoDim, FontSize = 10,
            FontWeight = FontWeight.SemiBold,
        });
        sp.Children.Add(contenido);
        return new Border
        {
            Background = BgSuave,
            BorderBrush = BordeSuave,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 10),
            Child = sp,
        };
    }

    /// <summary>Campo con etiqueta arriba (el ".field" del HTML).</summary>
    public static StackPanel Campo(string etiqueta, Control control)
    {
        var sp = new StackPanel { Spacing = 3, MinWidth = 150 };
        sp.Children.Add(Etiqueta(etiqueta));
        sp.Children.Add(control);
        return sp;
    }

    public static WrapPanel Grilla() => new WrapPanel
    {
        Orientation = Orientation.Horizontal, ItemWidth = double.NaN,
    };

    public static StackPanel Fila(double spacing = 8) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = spacing,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Par clave/valor de la grilla KV. Devuelve el TextBlock del
    /// valor para poder refrescarlo en el tick sin reconstruir nada.</summary>
    public static TextBlock AgregarKv(Grid kv, int fila, int col, string clave, string valor)
    {
        while (kv.RowDefinitions.Count <= fila) kv.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var k = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(clave),
            Foreground = TextoDim, FontSize = 10, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center,
        };
        var v = new TextBlock
        {
            Text = valor, Foreground = Texto, FontSize = 13, FontWeight = FontWeight.SemiBold,
            FontFamily = Mono, Margin = new Thickness(0, 3, 16, 3),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(k, fila); Grid.SetColumn(k, col);
        Grid.SetRow(v, fila); Grid.SetColumn(v, col + 1);
        kv.Children.Add(k); kv.Children.Add(v);
        return v;
    }

    public static Grid GrillaKv(int columnas = 1)
    {
        var g = new Grid();
        for (int i = 0; i < columnas; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        return g;
    }

    // ---- botones -----------------------------------------------------------

    public static Button Boton(string txt, Action? click = null, bool primario = false, bool peligro = false)
    {
        var b = new Button
        {
            Content = PilotX.Cockpit.Bars.Traductor.T(txt),
            MinHeight = 42,
            MinWidth = 44,
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

    /// <summary>Pill de estado: puntito + texto, borde redondeado.</summary>
    public static Border Pill(string texto, IBrush color, out TextBlock lbl, out Avalonia.Controls.Shapes.Ellipse dot)
    {
        dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 9, Height = 9, Fill = color, VerticalAlignment = VerticalAlignment.Center,
        };
        lbl = new TextBlock
        {
            Text = texto, Foreground = color, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(dot); sp.Children.Add(lbl);
        return new Border
        {
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center, Child = sp,
        };
    }

    public static Border Chip(string texto, IBrush? color = null) => new Border
    {
        Background = BgSuave,
        BorderBrush = color ?? BordeSuave,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(9, 3, 9, 3),
        Margin = new Thickness(0, 2, 4, 2),
        Child = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(texto),
            Foreground = color ?? TextoMuted, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    public static Border Swatch(int idx) => new Border
    {
        Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
        Background = QxEditorCtx.MotorBrush(idx),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- entradas ----------------------------------------------------------

    public static ComboBox Combo(int seleccionado = 0) => new ComboBox
    {
        MinHeight = 42, MinWidth = 150, FontSize = 13,
        Background = BgFila, Foreground = Texto,
        BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6, 8, 6),
        SelectedIndex = seleccionado,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>TextBox que pide el teclado nativo de PilotX al enfocarse
    /// (misma señal HTTP que mandan las páginas del Hub).</summary>
    public static TextBox Entrada(QuantiXEditorClient client, string valor, bool numerico,
                                string titulo, double ancho = 150)
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
        Content = PilotX.Cockpit.Bars.Traductor.T(texto),
        IsChecked = valor,
        MinHeight = 40,
        FontSize = 13,
        Foreground = Texto,
        VerticalAlignment = VerticalAlignment.Center,
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
}
