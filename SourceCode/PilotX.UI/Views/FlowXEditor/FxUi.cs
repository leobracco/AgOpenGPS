// ============================================================================
// FxUi.cs — piezas visuales + estado compartido del editor nativo de FlowX.
//
// QUE QUEDO NATIVO: el equivalente de las clases CSS de flowx.html (card,
// fx-section, field, badge, btn/primary, fx-savebar) y del objeto `cfg` que
// js/flowx.js mantenia en memoria mientras el operario editaba.
// QUE SIGUE EN HTML: theme.css/layout.css y js/flowx.js, que sirven a la PWA
// del celular — pages/flowx.html no se toca.
//
// Paleta CLARA de la doctrina (PORTING-AVALONIA §2): card #FAFBFA, superficies
// blancas, bordes #C5CFC5, verde #4ABA3E SOLO como acento, numeros en Consolas.
// Es la misma paleta que QxUi/VxUi; se repite a proposito para no acoplar tres
// productos por un archivo de estilos.
//
// Modelo de edicion: cada control escribe DIRECTO en el DTO en memoria al
// cambiar (no hay "commitEditorToCfg" como en el JS, que leia el DOM al
// guardar). Asi los campos que ninguna pantalla muestra — y "ignorados" —
// sobreviven al round-trip, que en FlowX es critico porque el POST reemplaza
// el archivo entero.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.FlowXEditor;

/// <summary>Pantalla interna del editor. `Rebuild()` reconstruye el arbol
/// entero; solo se llama por accion del operario o al entrar a la pantalla —
/// reconstruir en el tick tira el foco del TextBox y cierra el teclado.</summary>
public abstract class FxTab : StackPanel
{
    protected readonly FxCtx C;

    protected FxTab(FxCtx c)
    {
        C = c;
        Spacing = 12;
    }

    public abstract void Rebuild();

    /// <summary>Refresco liviano de cada tick (1 s): solo lo que depende del
    /// live / de los nodos vistos por MQTT.</summary>
    public virtual void Live() { }

    /// <summary>Se sale de la pantalla (cambio de tab o cierre del panel).</summary>
    public virtual Task AlSalirAsync() => Task.CompletedTask;
}

/// <summary>Estado compartido por las pantallas + los servicios que presta el
/// host (avisos, confirmaciones, overlay de PWM manual, guardado).</summary>
public sealed class FxCtx
{
    public const int CortesPorDefecto = 7;   // DEFAULT_CORTES_PER_NODE del JS
    public const int MaxCortes = 16;         // salidas del PCA9685
    public const int MaxReguladoras = 2;     // MaxProductCount del firmware

    public FlowXClient Client = null!;

    /// <summary>Token del panel: los polls largos (autotune 45 s, caracterizar
    /// 60 s) se abandonan si el operario cierra sin colgar la UI.</summary>
    public System.Threading.CancellationToken Ct;

    // ---- servicios del host -----------------------------------------------
    /// <summary>Aviso corto → toast del host. NUNCA modal (ShowDialog traba la
    /// cabina).</summary>
    public Action<string>? Aviso;
    /// <summary>Renglon de estado del pie: (texto, "" | "ok" | "err").</summary>
    public Action<string, string>? Estado;
    /// <summary>Reemplaza al askConfirm() del JS — overlay interno del panel.</summary>
    public Func<string, string, Task<bool>>? Confirmar;
    /// <summary>Reemplaza al showAlert() del JS.</summary>
    public Func<string, string, Task>? Alertar;
    /// <summary>Reemplaza al askText() del JS (volumen de calibracion).</summary>
    public Func<string, string, string, Task<string?>>? Pedir;
    /// <summary>Abre el overlay de PWM manual sobre la reguladora indicada.</summary>
    public Action<FlowXNodoConfig, int>? AbrirPwmManual;
    /// <summary>POST /api/flowx/config con la config entera. true = guardo.</summary>
    public Func<Task<bool>>? GuardarAsync;
    /// <summary>Rearma la fila selector de nodo (tras importar / eliminar).</summary>
    public Action? RefrescarSelector;
    /// <summary>Rearma la pantalla activa.</summary>
    public Action? RebuildTab;
    /// <summary>Salta a "Nodo activo" (botón Editar PID de la tabla).</summary>
    public Action? IrANodoActivo;

    // ---- estado ------------------------------------------------------------
    public FlowXConfig?        Cfg;
    public List<FlowXNodoLan>  Lan  = new();
    public FlowXLiveSnapshot?  Live;
    public FlowXAogState?      Aog;

    public string? CurrentUid;
    /// <summary>Indice (no id) de la reguladora cuyo PID se edita.</summary>
    public int SelectedProdIdx;

    /// <summary>Tira lo leido del backend al cerrar. La pagina HTML arrancaba
    /// con el estado vacio cada vez; el panel, si no se limpia, vuelve a pintar
    /// lo de la sesion anterior y el operario terminaria guardando encima con
    /// datos viejos (entre medio se pudo guardar desde el celular).</summary>
    public void LimpiarCache()
    {
        Cfg = null;
        Lan = new List<FlowXNodoLan>();
        Live = null;
        Aog = null;
        CurrentUid = null;
        SelectedProdIdx = 0;
    }

    // ---- helpers de config -------------------------------------------------

    public List<FlowXNodoConfig> Nodos()
    {
        if (Cfg == null) return new List<FlowXNodoConfig>();
        Cfg.Nodos ??= new List<FlowXNodoConfig>();
        return Cfg.Nodos;
    }

    public FlowXNodoConfig? NodoActual()
    {
        if (string.IsNullOrEmpty(CurrentUid)) return null;
        foreach (var n in Nodos())
            if (string.Equals(n.Uid, CurrentUid, StringComparison.Ordinal)) return n;
        return null;
    }

    public static List<FlowXProducto> Productos(FlowXNodoConfig n)
    {
        n.Productos ??= new List<FlowXProducto>();
        return n.Productos;
    }

    /// <summary>Reguladora en edicion, con el indice acotado al array.</summary>
    public FlowXProducto? ProductoActual(FlowXNodoConfig? n)
    {
        if (n == null) return null;
        var ps = Productos(n);
        if (ps.Count == 0) return null;
        if (SelectedProdIdx < 0 || SelectedProdIdx >= ps.Count) SelectedProdIdx = 0;
        return ps[SelectedProdIdx];
    }

    public FlowXNodoLan? LanDe(string? uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        foreach (var l in Lan)
            if (string.Equals(l.Uid, uid, StringComparison.Ordinal)) return l;
        return null;
    }

    public FlowXNodoLive? LiveDe(string? uid)
    {
        if (string.IsNullOrEmpty(uid) || Live?.Nodos == null) return null;
        foreach (var l in Live.Nodos)
            if (string.Equals(l.Uid, uid, StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    /// <summary>Secciones que reporta PilotX. 0 = todavia no hay estado (sin
    /// implemento abierto): con 0 NO se debe pisar cables[].</summary>
    public int NumSecAog() => Aog != null && Aog.NumSections > 0 ? Aog.NumSections : 0;

    public double AnchoAog() => Aog != null && Aog.ToolWidth > 0 ? Aog.ToolWidth : 0;

    /// <summary>
    /// Cuantas valvulas tiene la barra, en orden de confianza:
    ///   1. `cortes` del JSON, si el operario lo declaro (lo unico exacto).
    ///   2. el corte MAS ALTO asignado en cables[] — no la cantidad de cables
    ///      distintos: con asignacion manual puede haber huecos (usar S1, S2 y
    ///      S5 son 5 salidas, no 3) y contar unicos se comia las de arriba.
    ///   3. el default de hardware (7 salidas por nodo).
    /// </summary>
    public static int InferNumCortes(FlowXNodoConfig? n)
    {
        if (n == null) return CortesPorDefecto;
        if (n.Cortes > 0) return Math.Min(n.Cortes, MaxCortes);

        if (n.Cables != null && n.Cables.Count > 0)
        {
            int max = 0;
            foreach (var c in n.Cables) if (c != null && c.Cable > max) max = c.Cable;
            if (max > 0) return Math.Min(max, MaxCortes);
        }
        return CortesPorDefecto;
    }

    /// <summary>Reparto uniforme: corte i ↔ secciones
    /// [floor(nSec·i/nCortes)+1 .. floor(nSec·(i+1)/nCortes)], cada corte con
    /// al menos una seccion. Se persiste como pares {cable, seccion_aog} para
    /// no tocar el bridge (hace OR sobre bits[cable-1]).</summary>
    public static List<FlowXCableMap> AutoAsignarCortes(int nCortes, int nSec)
    {
        var outp = new List<FlowXCableMap>();
        if (nCortes <= 0 || nSec <= 0) return outp;
        for (int i = 0; i < nCortes; i++)
        {
            int start = (int)Math.Floor((double)nSec * i / nCortes);
            int end   = (int)Math.Floor((double)nSec * (i + 1) / nCortes);
            if (end <= start) end = start + 1;
            if (end > nSec) end = nSec;
            for (int s = start; s < end; s++)
                outp.Add(new FlowXCableMap { Cable = i + 1, SeccionAog = s + 1 });
        }
        return outp;
    }

    /// <summary>SIEMPRE 10 enteros: mandar menos tiene comportamiento indefinido
    /// en el firmware. Valores fuera de {0,1} van a -1 (global).</summary>
    public static List<int> NormalizarSec3w(List<int>? arr)
    {
        var outp = new List<int>(10);
        for (int i = 0; i < 10; i++)
        {
            int v = (arr != null && i < arr.Count) ? arr[i] : -1;
            if (v != 0 && v != 1) v = -1;
            outp.Add(v);
        }
        return outp;
    }

    /// <summary>Nodo nuevo con los defaults del JS. Los nodos SOLO entran por
    /// descubrimiento MQTT: nunca tipeando un UID (regla del repo).</summary>
    public FlowXNodoConfig NodoNuevo(string uid, string? nombreLan)
    {
        int nSec = NumSecAog();
        return new FlowXNodoConfig
        {
            Uid = uid,
            Nombre = string.IsNullOrEmpty(nombreLan) ? "Nodo FlowX" : nombreLan,
            Habilitado = true,
            AnchoBarraM = AnchoAog(),
            Is3Wire = false,
            InvertRelay = false,
            InvertMotor = false,
            MasterCable = -1,
            Cortes = CortesPorDefecto,
            SectionIs3Wire = NormalizarSec3w(null),
            Productos = new List<FlowXProducto> { new FlowXProducto() },
            Cables = nSec > 0 ? AutoAsignarCortes(CortesPorDefecto, nSec) : new List<FlowXCableMap>(),
        };
    }

    public static string FmtUptime(long seg)
    {
        long s = seg < 0 ? 0 : seg;
        if (s < 60) return s + " s";
        if (s < 3600) return (s / 60) + " min";
        if (s < 86400) return (s / 3600) + " h " + ((s % 3600) / 60) + " min";
        return (s / 86400) + " d " + ((s % 86400) / 3600) + " h";
    }
}

public static class FxUi
{
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
    // Verde de FONDO para texto blanco (el acento #4ABA3E con letras blancas
    // encima da 2,5:1 medido y desaparece con sol). Este da 5,3:1.
    public static readonly IBrush VerdeFuerte = new SolidColorBrush(Color.Parse("#2F7A26"));
    public static readonly IBrush Ok         = new SolidColorBrush(Color.Parse("#3D9A33"));
    public static readonly IBrush Warn       = new SolidColorBrush(Color.Parse("#B98A2E"));
    public static readonly IBrush Err        = new SolidColorBrush(Color.Parse("#D0504A"));
    public static readonly IBrush Dim        = new SolidColorBrush(Color.Parse("#8A958B"));

    public static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    private static string T(string s) => PilotX.Cockpit.Bars.Traductor.T(s);

    // ---- textos ------------------------------------------------------------
    //
    // Los titulos/etiquetas pasan por Traductor.T() ACA, en el origen: los
    // rebuilds parciales no vuelven a llamar a Traductor.Aplicar y un texto que
    // no se traduce al crearse queda en castellano con la pantalla en ingles.

    public static TextBlock Titulo(string t) => new TextBlock
    {
        Text = T(t), Foreground = Texto, FontSize = 15, FontWeight = FontWeight.Bold,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Sub(string t) => new TextBlock
    {
        Text = T(t), Foreground = TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Etiqueta(string t) => new TextBlock
    {
        // 13 px #535E54 (6,3:1). En 10 px #7A857B daba 3,6:1: al sol se veía el
        // número y no de qué era.
        Text = T(t), Foreground = TextoMuted, FontSize = 13, FontWeight = FontWeight.Bold,
    };

    public static TextBlock MonoTexto(string t, double size = 13) => new TextBlock
    {
        Text = t, Foreground = Texto, FontSize = size, FontFamily = Mono,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- contenedores ------------------------------------------------------

    public static Border Card(Control contenido) => new Border
    {
        Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
        Child = contenido,
    };

    /// <summary>Bloque con titulo en MAYUSCULAS (la ".fx-section" del HTML).</summary>
    public static Border Bloque(string titulo, Control contenido)
    {
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(Etiqueta(titulo.ToUpperInvariant()));
        sp.Children.Add(contenido);
        return new Border
        {
            Background = BgSuave, BorderBrush = BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 10),
            Child = sp,
        };
    }

    public static StackPanel Campo(string etiqueta, Control control, double minAncho = 150)
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
            // 48 y no 42: con guante, abajo de 44 px no se acierta.
            MinHeight = 48, MinWidth = 48,
            Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(8),
            FontSize = 13,
            FontWeight = primario ? FontWeight.SemiBold : FontWeight.Normal,
            Background = primario ? VerdeFuerte : BgFila,
            Foreground = primario ? Brushes.White : (peligro ? Err : Texto),
            BorderBrush = primario ? VerdeFuerte : (peligro ? Err : Borde),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (click != null) b.Click += (_, __) => click();
        return b;
    }

    public static Border Pill(string texto, IBrush color)
    {
        var sp = Fila(6);
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

    /// <summary>Badge chato (el ".badge ok|warn|bad" del HTML).</summary>
    public static Border Badge(string texto, IBrush color) => new Border
    {
        Background = BgFila, BorderBrush = color, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = T(texto), Foreground = color, FontSize = 11 },
    };

    // ---- entradas ----------------------------------------------------------

    public static ComboBox Combo(double minAncho = 160) => new ComboBox
    {
        MinHeight = 48, MinWidth = minAncho, FontSize = 13,
        Background = BgFila, Foreground = Texto,
        BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6, 8, 6),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>ComboBox de opciones (valor, etiqueta) que escribe en el DTO al
    /// cambiar. Devuelve el control ya posicionado en `valor`.</summary>
    public static ComboBox ComboOpciones(IReadOnlyList<(int Valor, string Etiqueta)> ops, int valor,
                                         Action<int> alCambiar, double minAncho = 160)
    {
        var cb = Combo(minAncho);
        var vals = new List<int>();
        var etiquetas = new List<string>();
        foreach (var o in ops) { etiquetas.Add(T(o.Etiqueta)); vals.Add(o.Valor); }
        cb.ItemsSource = etiquetas;
        int idx = vals.IndexOf(valor);
        cb.SelectedIndex = idx >= 0 ? idx : 0;
        // El handler se engancha DESPUES de posicionar el combo: si no, un valor
        // guardado que ya no existe en la lista (p. ej. master en el corte 5 con
        // 3 cortes) se pisaria solo con la primera opcion sin que nadie lo toque.
        cb.SelectionChanged += (_, __) =>
        {
            int i = cb.SelectedIndex;
            if (i >= 0 && i < vals.Count) alCambiar(vals[i]);
        };
        return cb;
    }

    /// <summary>TextBox que pide el teclado nativo de PilotX al enfocarse (misma
    /// senal HTTP que mandan las paginas del Hub). Sin esto el campo queda
    /// ineditable en la pantalla tactil de cabina.</summary>
    public static TextBox Entrada(FlowXClient client, string valor, bool numerico,
                                  string titulo, double ancho = 150)
    {
        var t = new TextBox
        {
            Text = valor, Width = ancho, MinHeight = 48, FontSize = 13,
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

    /// <summary>Campo numerico que escribe en el DTO en cada tecla. Tolerante:
    /// mientras se borra o se tipea "1." el DTO se queda con lo ultimo valido.</summary>
    public static TextBox EntradaNum(FlowXClient client, double valor, int dec, string titulo,
                                     Action<double> alCambiar, double ancho = 120)
    {
        var t = Entrada(client, Num(valor, dec), true, titulo, ancho);
        t.TextChanged += (_, __) =>
        {
            double v = LeerDouble(t, double.NaN);
            if (!double.IsNaN(v)) alCambiar(v);
        };
        return t;
    }

    public static CheckBox Check(string texto, bool valor, Action<bool> alCambiar)
    {
        var c = new CheckBox
        {
            Content = T(texto), IsChecked = valor, MinHeight = 48, FontSize = 13,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
        };
        c.IsCheckedChanged += (_, __) => alCambiar(c.IsChecked == true);
        return c;
    }

    // ---- parseo / formato --------------------------------------------------

    public static double LeerDouble(TextBox? t, double fallback)
    {
        if (t == null) return fallback;
        var s = (t.Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public static int LeerInt(TextBox? t, int fallback)
    {
        double v = LeerDouble(t, double.NaN);
        return double.IsNaN(v) ? fallback : (int)Math.Round(v);
    }

    public static string Num(double v, int dec)
        => v.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public static string Int(double v)
        => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
}
