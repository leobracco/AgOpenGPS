// ============================================================================
// CfgUi.cs — piezas visuales compartidas por las pestañas del ConfigPanel
// nativo (el porteo de pages/config.html) + la clase base ConfigTab.
//
// QUÉ QUEDÓ NATIVO: el equivalente de las clases CSS de config.html (.carta,
// .sumgrid, .nota, .field, .nud, .radioimg, el botón Guardar y el footer).
// QUÉ SIGUE EN HTML: theme.css/layout.css y la página entera, que sirven a la
// PWA del celular — no se tocan ni se borran.
//
// Paleta CLARA de la doctrina (PORTING-AVALONIA §2): card #FAFBFA, superficies
// blancas, bordes #C5CFC5, verde #4ABA3E SOLO como acento, números en Consolas.
// Nada oscuro ni decorativo: esto se mira con sol de frente.
//
// Los textos pasan por Traductor.T() ACÁ, en el origen: los rebuilds parciales
// de una pestaña no vuelven a llamar a Traductor.Aplicar, así que un texto que
// no se traduce al crearse se queda en castellano con la pantalla en inglés.
// ============================================================================

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PilotX.Desktop.Views.ConfigEditor;

/// <summary>
/// Base de una pestaña de Configuración. Réplica del contrato que tiene cada
/// tab en config.js: `enter()` (pintar desde el snapshot) y `leave()` →
/// Promise&lt;bool&gt; (guardar si hay cambios; false CANCELA la navegación,
/// igual que en la página: si el guardado falla, el operario se queda donde
/// estaba con el error a la vista).
/// </summary>
public abstract class ConfigTab : StackPanel
{
    protected readonly CfgCtx C;

    protected ConfigTab(CfgCtx c)
    {
        C = c;
        Spacing = 12;
    }

    /// <summary>Rebuild COMPLETO del árbol. Solo al entrar a la pestaña o por
    /// acción del operario: reconstruir con un refresco de fondo tira el foco
    /// del TextBox y cierra el teclado nativo.</summary>
    public abstract void Rebuild();

    /// <summary>Equivalente de `enter()`: repinta con el snapshot actual.</summary>
    public virtual Task AlEntrarAsync() => Task.CompletedTask;

    /// <summary>Equivalente de `leave()`: guarda si hay cambios. false = no se
    /// pudo guardar ⇒ el shell NO navega.</summary>
    public virtual Task<bool> AlSalirAsync() => Task.FromResult(true);

    /// <summary>¿La pestaña tiene algo para guardar? Si es false el shell
    /// esconde el botón Guardar — un botón que dice "Guardado ✔" sin guardar
    /// nada (el quirk que tiene hoy Resumen en el HTML) le miente al operario.
    /// </summary>
    public virtual bool TieneGuardar => false;

    /// <summary>Refresco liviano por tick (solo labels vivos). Vacío por
    /// defecto: la config no es telemetría.</summary>
    public virtual void Live() { }
}

public static class CfgUi
{
    // ---- paleta ------------------------------------------------------------

    public static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    public static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    public static readonly IBrush BgFilaSel  = new SolidColorBrush(Color.Parse("#DCEFD8"));
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
    public static readonly IBrush TextoError = new SolidColorBrush(Color.Parse("#B33F3A"));

    public static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    private static string T(string s) => PilotX.Cockpit.Bars.Traductor.T(s);

    // ---- textos ------------------------------------------------------------

    public static TextBlock Titulo(string t) => new TextBlock
    {
        Text = T(t), Foreground = Texto, FontSize = 15, FontWeight = FontWeight.Bold,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>La `.nota` del HTML: aclaración chica y gris.</summary>
    public static TextBlock Nota(string t) => new TextBlock
    {
        Text = T(t), Foreground = TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Etiqueta(string t) => new TextBlock
    {
        Text = T(t), Foreground = TextoDim, FontSize = 10, FontWeight = FontWeight.SemiBold,
    };

    public static TextBlock Valor(string t) => new TextBlock
    {
        Text = t, Foreground = Texto, FontSize = 13, FontWeight = FontWeight.SemiBold,
        FontFamily = Mono,
    };

    // ---- contenedores ------------------------------------------------------

    /// <summary>La `.carta` del HTML.</summary>
    public static Border Carta(Control contenido) => new Border
    {
        Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
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

    /// <summary>Campo con etiqueta arriba (el `.field` del HTML).</summary>
    public static StackPanel Campo(string etiqueta, Control control)
    {
        var sp = new StackPanel { Spacing = 3, MinWidth = 150 };
        sp.Children.Add(Etiqueta(etiqueta));
        sp.Children.Add(control);
        return sp;
    }

    public static StackPanel Fila(double spacing = 10) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = spacing,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static WrapPanel Grilla() => new WrapPanel { Orientation = Orientation.Horizontal };

    // ---- grilla clave/valor (la `.sumgrid` del HTML) -----------------------

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

    /// <summary>Par clave/valor. Devuelve el TextBlock del valor para poder
    /// refrescarlo sin reconstruir nada.</summary>
    public static TextBlock AgregarKv(Grid kv, int fila, int col, string clave, string valor)
    {
        while (kv.RowDefinitions.Count <= fila) kv.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var k = new TextBlock
        {
            Text = T(clave), Foreground = TextoDim, FontSize = 10, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center,
        };
        var v = new TextBlock
        {
            Text = valor, Foreground = Texto, FontSize = 14, FontWeight = FontWeight.SemiBold,
            FontFamily = Mono, Margin = new Thickness(0, 5, 18, 5),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(k, fila); Grid.SetColumn(k, col);
        Grid.SetRow(v, fila); Grid.SetColumn(v, col + 1);
        kv.Children.Add(k); kv.Children.Add(v);
        return v;
    }

    // ---- botones / chips ---------------------------------------------------

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
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (click != null) b.Click += (_, __) => click();
        return b;
    }

    /// <summary>Chip de error con el código AGP (fallback AGP-NET-201).</summary>
    public static Border ChipError(string mensaje, string codigo = "AGP-NET-201")
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock
        {
            Text = T(mensaje), Foreground = Err, FontSize = 13, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        sp.Children.Add(new TextBlock
        {
            Text = codigo, Foreground = TextoError, FontSize = 11, FontFamily = Mono,
        });
        return new Border
        {
            Background = BgError, BorderBrush = Err, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8),
            Child = sp,
        };
    }

    // ---- entradas ----------------------------------------------------------

    /// <summary>TextBox que pide el teclado nativo de PilotX al enfocarse
    /// (misma señal HTTP que mandan las páginas del Hub).</summary>
    public static TextBox Entrada(CfgCtx c, string valor, bool numerico, string titulo, double ancho = 130)
    {
        var t = new TextBox
        {
            Text = valor, Width = ancho, MinHeight = 42, FontSize = 14,
            Background = BgFila, Foreground = Texto,
            BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 6, 9, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        t.GotFocus  += (_, __) => _ = c.Client?.TecladoAsync(true, numerico, titulo) ?? Task.CompletedTask;
        t.LostFocus += (_, __) => _ = c.Client?.TecladoAsync(false) ?? Task.CompletedTask;
        return t;
    }

    public static CheckBox Check(string texto, bool valor) => new CheckBox
    {
        Content = T(texto), IsChecked = valor, MinHeight = 40, FontSize = 13,
        Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
    };

    public static ComboBox Combo(int seleccionado = 0) => new ComboBox
    {
        MinHeight = 42, MinWidth = 150, FontSize = 13,
        Background = BgFila, Foreground = Texto,
        BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 8, 6),
        SelectedIndex = seleccionado, VerticalAlignment = VerticalAlignment.Center,
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

    /// <summary>Saca un control de su padre anterior (Avalonia revienta con
    /// "The control already has a visual parent" si se reusa entre rebuilds).</summary>
    public static T Soltar<T>(T c) where T : Control
    {
        var p = c.Parent;
        if (p is Panel panel) panel.Children.Remove(c);
        else if (p is Border b && ReferenceEquals(b.Child, c)) b.Child = null;
        else if (p is ContentControl cc && ReferenceEquals(cc.Content, c)) cc.Content = null;
        else if (p is Decorator d && ReferenceEquals(d.Child, c)) d.Child = null;
        return c;
    }
}
