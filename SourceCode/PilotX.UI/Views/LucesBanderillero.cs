// ============================================================================
// LucesBanderillero.cs — la barra de luces para manejar a mano siguiendo la
// guía, como las barras de banderillero.
//
// 15 luces: 7 a cada lado y una central. Se prenden del centro hacia el lado al
// que hay que IR. La CANTIDAD prendida dice cuánto te fuiste sin que haga falta
// leer el número, que es lo que permite manejarla de reojo — por eso la escala
// de 4 colores es segura acá y no lo sería en un número suelto: el color es
// refuerzo, no el único canal.
//
// La luz central es aparte y no cuenta como luz de desvío: verde cuando estás
// en la línea, apagada en cuanto se prende la primera lateral, para que el ojo
// siga al grupo que se mueve y no al centro.
//
// Toda la aritmética está en EscalaDesvio (Cockpit.Bars, con tests). Acá sólo
// se pinta.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Cockpit.Bars;

// OJO con el namespace: este proyecto tiene AssemblyName = PilotX.UI pero
// RootNamespace = PilotX.Desktop (está documentado en el .csproj; el rename
// quedó diferido). Los 65 archivos de Views/ usan PilotX.Desktop.Views.
namespace PilotX.Desktop.Views;

public sealed class LucesBanderillero : Border
{
    private const int AnchoLuz = 26;
    private const int AltoLuz = 30;
    private const int AnchoCentral = 10;

    private static readonly IBrush Apagada = new SolidColorBrush(Color.Parse("#2A3329"));
    private static readonly IBrush BordeLuz = new SolidColorBrush(Color.Parse("#66FFFFFF"));

    // Un brush por nivel, cacheados una sola vez: Actualizar() corre a la
    // frecuencia del GPS y no puede andar creando SolidColorBrush por frame
    // para sólo 4 colores posibles. El color sigue saliendo de
    // EscalaDesvio.ColorHex(), acá sólo se cachea.
    private static readonly IBrush BrushVerde = new SolidColorBrush(Color.Parse(EscalaDesvio.ColorHex(NivelDesvio.Verde)));
    private static readonly IBrush BrushAmarillo = new SolidColorBrush(Color.Parse(EscalaDesvio.ColorHex(NivelDesvio.Amarillo)));
    private static readonly IBrush BrushNaranja = new SolidColorBrush(Color.Parse(EscalaDesvio.ColorHex(NivelDesvio.Naranja)));
    private static readonly IBrush BrushRojo = new SolidColorBrush(Color.Parse(EscalaDesvio.ColorHex(NivelDesvio.Rojo)));

    private static IBrush BrushDeNivel(NivelDesvio nivel) => nivel switch
    {
        NivelDesvio.Verde => BrushVerde,
        NivelDesvio.Amarillo => BrushAmarillo,
        NivelDesvio.Naranja => BrushNaranja,
        NivelDesvio.Rojo => BrushRojo,
        _ => BrushVerde,
    };

    // Índice 0 = la más CERCANA al centro, en los dos arreglos. Así
    // `i < LucesEncendidas` vale igual para los dos lados en Actualizar()
    // (ver la convención completa más abajo, en el constructor).
    private readonly Border[] _izquierda = new Border[EscalaDesvio.LucesPorLado];
    private readonly Border[] _derecha = new Border[EscalaDesvio.LucesPorLado];
    private readonly Border _central;
    private readonly TextBlock _flecha;
    private readonly TextBlock _numero;
    private readonly TextBlock _unidad;

    public LucesBanderillero()
    {
        // Mismo fondo oscuro translúcido que el cluster del piloto: sobre el
        // piso texturado un panel claro lava los colores de las luces.
        Background = new SolidColorBrush(Color.Parse("#D9101612"));
        BorderBrush = new SolidColorBrush(Color.Parse("#66FFFFFF"));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12, 8);
        IsVisible = false;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Center;

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // CONVENCIÓN DE ÍNDICES, en los dos arreglos: el índice 0 es la luz
        // MÁS CERCANA AL CENTRO. Así `i < LucesEncendidas` se lee igual para
        // los dos lados en Actualizar(). El orden de AGREGADO al panel es otra
        // cosa: de izquierda a derecha en pantalla.

        // Izquierda: en pantalla va primero la más lejana (índice 6) y última
        // la pegada al centro (índice 0).
        for (int i = EscalaDesvio.LucesPorLado - 1; i >= 0; i--)
        {
            _izquierda[i] = NuevaLuz(AnchoLuz);
            fila.Children.Add(_izquierda[i]);
        }

        _central = NuevaLuz(AnchoCentral);
        fila.Children.Add(_central);

        // Derecha: al revés, primero la pegada al centro (índice 0).
        for (int i = 0; i < EscalaDesvio.LucesPorLado; i++)
        {
            _derecha[i] = NuevaLuz(AnchoLuz);
            fila.Children.Add(_derecha[i]);
        }

        _flecha = new TextBlock
        {
            FontSize = 26,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F5F7F4")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _numero = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F5F7F4")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _unidad = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#C5CFC5")),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var pie = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        pie.Children.Add(_flecha);
        pie.Children.Add(_numero);
        pie.Children.Add(_unidad);

        var raiz = new StackPanel { Orientation = Orientation.Vertical };
        raiz.Children.Add(fila);
        raiz.Children.Add(pie);
        Child = raiz;
    }

    private static Border NuevaLuz(int ancho) => new Border
    {
        Width = ancho,
        Height = AltoLuz,
        CornerRadius = new CornerRadius(3),
        Background = Apagada,
        BorderBrush = BordeLuz,
        BorderThickness = new Thickness(1),
    };

    /// <summary>Repinta con el desvío actual. xteMetros NaN = sin guía: el
    /// control se esconde solo.</summary>
    public void Actualizar(double xteMetros, double cmPorLuz)
    {
        var l = EscalaDesvio.Leer(xteMetros, cmPorLuz);
        if (!l.HayDato) { IsVisible = false; return; }
        IsVisible = true;

        var encendida = BrushDeNivel(l.Nivel);

        // LucesEncendidas y Nivel son ejes independientes a propósito:
        // LucesEncendidas sale de cm / cmPorLuz, Nivel sale de los cm crudos
        // (cortes fijos en 5/15/25). Con cmPorLuz alto puede haber Nivel !=
        // Verde con 0 luces encendidas, así que la central NO puede reusar
        // "encendida": siempre es verde cuando no hay ninguna lateral
        // prendida, sea cual sea el nivel — es la señal de "estás en la línea".
        _central.Background = l.LucesEncendidas == 0 ? BrushVerde : Apagada;

        for (int i = 0; i < EscalaDesvio.LucesPorLado; i++)
        {
            // El índice 0 es la pegada al centro en los dos arreglos, así que
            // `i < encendidas` vale igual para los dos lados.
            bool prende = i < l.LucesEncendidas;
            _izquierda[i].Background = (prende &&  l.HaciaLaIzquierda) ? encendida : Apagada;
            _derecha[i].Background   = (prende && !l.HaciaLaIzquierda) ? encendida : Apagada;
        }

        _flecha.Text = l.LucesEncendidas == 0 ? "" : (l.HaciaLaIzquierda ? "◀" : "▶");
        _numero.Foreground = encendida;

        // Mismo formato que el cluster: cm enteros bajo el metro, metros con un
        // decimal de ahí en adelante. No se inventa un formato nuevo.
        if (l.Centimetros < 100)
        {
            _numero.Text = l.Centimetros.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            _unidad.Text = "cm";
        }
        else
        {
            _numero.Text = (l.Centimetros / 100.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            _unidad.Text = "m";
        }
    }
}
