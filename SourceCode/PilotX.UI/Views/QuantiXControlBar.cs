// QuantiXControlBar.cs
//
// Barra HORIZONTAL de control de UN motor QuantiX. Vive centrada abajo del
// mapa, arriba del overlay de la pasada — la zona donde el operario ya tiene
// la mano mientras mira la línea.
//
// Nace del rediseño del 2026-08-05 en la pantalla del taller (10", el tamaño
// real de trabajo): el overlay viejo llevaba TODOS los controles adentro de
// cada fila (MAN/AUTO + −/+ por motor) y con dos o tres motores tapaba medio
// mapa. El reparto nuevo:
//
//   · el overlay (QuantiXMapOverlay) queda de MONITOR compacto: lista
//     vertical con real/objetivo de todos los motores, solo mirar y elegir;
//   · tocar un motor en esa lista abre ESTA barra con los controles de ese
//     motor únicamente. Un motor a la vez: es lo que el operario está
//     ajustando, no un tablero completo.
//
// La barra es UI muda a propósito: no conoce el cliente HTTP ni el estado.
// El overlay la alimenta (Actualizar) en cada poll y le cuelga los comandos
// en los callbacks — un solo dueño del estado, cero carreras de refresh.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PilotX.Desktop.Views;

public sealed class QuantiXControlBar : Border
{
    // Misma paleta del overlay: fondo casi opaco, se lee sobre pasto al sol.
    private static readonly IBrush BgPanel  = new SolidColorBrush(Color.Parse("#F2101612"));
    private static readonly IBrush BgBoton  = new SolidColorBrush(Color.Parse("#1B231E"));
    private static readonly IBrush Borde    = new SolidColorBrush(Color.Parse("#2A332C"));
    private static readonly IBrush Acento   = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ambar    = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush TextoHi  = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush TextoMid = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush TextoDim = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush TextoInv = new SolidColorBrush(Color.Parse("#101612"));

    private readonly TextBlock _nombre;
    private readonly Button _btnAuto;
    private readonly Button _btnMan;
    private readonly Button _btnMenos;
    private readonly Button _btnMas;
    private readonly TextBlock _dosis;
    private readonly TextBlock _unidad;
    private readonly TextBlock _rpm;

    /// <summary>Pasar el motor a AUTO (el mapa/prescripción manda).</summary>
    public Action? OnAuto;
    /// <summary>Pasar el motor a MAN (la dosis la fija el operario).</summary>
    public Action? OnMan;
    /// <summary>Paso de dosis en MAN: -1 o +1.</summary>
    public Action<int>? OnPaso;
    /// <summary>El operario cerró la barra (✕): deseleccionar el motor.</summary>
    public Action? OnCerrar;

    public QuantiXControlBar()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 6, 8, 6);
        BoxShadow = BoxShadows.Parse("0 4 16 0 #90000000");
        IsVisible = false;

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _nombre = new TextBlock
        {
            Text = "—",
            Foreground = TextoMid,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 84,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        fila.Children.Add(_nombre);

        _btnAuto = BotonModo("AUTO");
        _btnMan  = BotonModo("MAN");
        _btnAuto.Click += (_, __) => OnAuto?.Invoke();
        _btnMan.Click  += (_, __) => OnMan?.Invoke();
        fila.Children.Add(_btnAuto);
        fila.Children.Add(_btnMan);

        fila.Children.Add(new Border { Width = 1, Background = Borde, Margin = new Thickness(2, 4) });

        _btnMenos = BotonPaso("−");
        _btnMas   = BotonPaso("+");
        _btnMenos.Click += (_, __) => OnPaso?.Invoke(-1);
        _btnMas.Click   += (_, __) => OnPaso?.Invoke(+1);

        _dosis = new TextBlock
        {
            Text = "—",
            Foreground = TextoHi,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            MinWidth = 88,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        };
        _unidad = new TextBlock
        {
            Text = "",
            Foreground = TextoDim,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        fila.Children.Add(_btnMenos);
        fila.Children.Add(_dosis);
        fila.Children.Add(_unidad);
        fila.Children.Add(_btnMas);

        // Las rpm REALES del motor (encoder), al lado de la dosis: con sem/m
        // solas no se ve si el motor está girando como debe o clavado.
        fila.Children.Add(new Border { Width = 1, Background = Borde, Margin = new Thickness(2, 4) });
        _rpm = new TextBlock
        {
            Text = "—",
            Foreground = TextoMid,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 56,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        };
        fila.Children.Add(_rpm);
        fila.Children.Add(new TextBlock
        {
            Text = "rpm",
            Foreground = TextoDim,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var cerrar = new Button
        {
            Content = "✕",
            FontSize = 14,
            Width = 40,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Foreground = TextoDim,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
        };
        cerrar.Click += (_, __) => OnCerrar?.Invoke();
        fila.Children.Add(cerrar);

        Child = fila;
    }

    /// <summary>
    /// Refresca la barra con el estado FRESCO del motor seleccionado. La llama
    /// el overlay en cada poll; acá no se decide nada, solo se pinta.
    /// </summary>
    public void Actualizar(string nombre, bool manual, string dosisTexto, string etiquetaUnidad, int rpm)
    {
        _nombre.Text = nombre;
        _dosis.Text = dosisTexto;
        _unidad.Text = etiquetaUnidad;
        _rpm.Text = rpm.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _btnMan.Background  = manual ? Ambar : BgBoton;
        _btnMan.Foreground  = manual ? TextoInv : TextoMid;
        _btnAuto.Background = manual ? BgBoton : Acento;
        _btnAuto.Foreground = manual ? TextoMid : TextoInv;

        // En AUTO manda la prescripción: los pasos no harían nada.
        _btnMenos.IsEnabled = manual;
        _btnMas.IsEnabled = manual;
        _dosis.Foreground = manual ? TextoHi : TextoDim;
    }

    private static Button BotonModo(string texto) => new Button
    {
        Content = texto,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Width = 62,
        Height = 44,
        Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = BgBoton,
        Foreground = TextoMid,
        BorderBrush = Borde,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
    };

    // 48 px de ancho: guante y tractor moviéndose.
    private static Button BotonPaso(string texto) => new Button
    {
        Content = texto,
        FontSize = 20,
        FontWeight = FontWeight.Bold,
        Width = 48,
        Height = 44,
        Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = BgBoton,
        Foreground = TextoHi,
        BorderBrush = Borde,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
    };
}
