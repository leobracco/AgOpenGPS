// QuantiXMapOverlay.cs
//
// El overlay de QuantiX que se ve SOBRE el mapa mientras se trabaja. Hasta
// ahora esto solo existia en la app WinForms, donde era un WebView2 flotante
// con /pages/widget-quantix.html: en PilotX.Desktop el toggle del Hub escribia
// la preferencia y no aparecia nada, porque no habia quien lo dibujara.
//
// Va nativo, no WebView: el overlay vive encima del mapa GL todo el tiempo que
// dura la labor, y un WebView permanente ahi come memoria, tapa el mapa con una
// superficie opaca y ademas es Windows-only (romperia el port a Android).
//
// Que muestra, en orden de importancia para el que maneja:
//   1. la dosis que esta aplicando, grande, con color segun se aleje del objetivo
//   2. el objetivo y las rpm del motor
//   3. AUTO / MAN, y en MAN los botones - / + para corregir sobre la marcha
//
// El pps no aparece: es una unidad interna del firmware.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public sealed class QuantiXMapOverlay : Border
{
    // Paleta PilotX. Fondo casi opaco: abajo hay mapa, y un panel translucido
    // sobre pasto verde no se lee con sol.
    private static readonly IBrush BgPanel = new SolidColorBrush(Color.Parse("#F2101612"));
    private static readonly IBrush BgFila  = new SolidColorBrush(Color.Parse("#1B231E"));
    private static readonly IBrush Borde   = new SolidColorBrush(Color.Parse("#2A332C"));
    private static readonly IBrush Acento  = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ambar   = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush Rojo    = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush TextoHi = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush TextoMid= new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush TextoDim= new SolidColorBrush(Color.Parse("#8FA092"));

    private const double PollMs = 500;

    private readonly StackPanel _raiz;
    private readonly TextBlock _titulo;
    private readonly Ellipse _puntoOnline;
    private readonly StackPanel _filas;
    private readonly Button _btnAuto;
    private readonly Button _btnMan;
    private readonly Button _btnMenos;
    private readonly Button _btnMas;
    private readonly TextBlock _dosisManual;
    private readonly StackPanel _ctlManual;

    private WidgetQuantiXClient? _client;
    private CancellationTokenSource? _cts;
    private QxWidgetState? _estado;

    /// <summary>Se dispara cuando el operario arrastra el overlay. La posición
    /// la persiste quien lo hospeda (necesita las coords del contenedor).</summary>
    public Action<Point>? OnMovido { get; set; }

    public QuantiXMapOverlay()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12, 10, 12, 10);
        MinWidth = 250;
        BoxShadow = BoxShadows.Parse("0 4 16 0 #90000000");
        // Arrastrable desde cualquier parte que no sea un boton.
        Cursor = new Cursor(StandardCursorType.SizeAll);

        _raiz = new StackPanel { Spacing = 8 };

        // ---- Encabezado: estado del nodo + AUTO/MAN ----
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var headIzq = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _puntoOnline = new Ellipse { Width = 9, Height = 9, Fill = TextoDim, VerticalAlignment = VerticalAlignment.Center };
        _titulo = new TextBlock
        {
            Text = "QuantiX",
            Foreground = TextoHi,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headIzq.Children.Add(_puntoOnline);
        headIzq.Children.Add(_titulo);
        Grid.SetColumn(headIzq, 0);
        head.Children.Add(headIzq);

        var modos = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _btnAuto = BotonModo("AUTO");
        _btnMan  = BotonModo("MAN");
        _btnAuto.Click += async (_, __) => await CambiarModo(false);
        _btnMan.Click   += async (_, __) => await CambiarModo(true);
        modos.Children.Add(_btnAuto);
        modos.Children.Add(_btnMan);
        Grid.SetColumn(modos, 1);
        head.Children.Add(modos);

        _raiz.Children.Add(head);

        // ---- Filas por motor ----
        _filas = new StackPanel { Spacing = 5 };
        _raiz.Children.Add(_filas);

        // ---- Control manual (solo visible en MAN) ----
        _ctlManual = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
        };
        _btnMenos = BotonPaso("−");
        _btnMas   = BotonPaso("+");
        _dosisManual = new TextBlock
        {
            Text = "—",
            Foreground = TextoHi,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            MinWidth = 96,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        };
        _btnMenos.Click += async (_, __) => await Paso(-1);
        _btnMas.Click   += async (_, __) => await Paso(+1);
        _ctlManual.Children.Add(_btnMenos);
        _ctlManual.Children.Add(_dosisManual);
        _ctlManual.Children.Add(_btnMas);
        _raiz.Children.Add(_ctlManual);

        Child = _raiz;
        HabilitarArrastre();
    }

    private static Button BotonModo(string texto) => new Button
    {
        Content = texto,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Padding = new Thickness(10, 4),
        MinHeight = 28,
        Background = BgFila,
        Foreground = TextoMid,
        BorderBrush = Borde,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
    };

    // 48 px: se tiene que poder tocar con guante y el tractor moviendose.
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
        Background = BgFila,
        Foreground = TextoHi,
        BorderBrush = Borde,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
    };

    // ---------- Arrastre ----------------------------------------------------

    private bool _arrastrando;
    private Point _origenPuntero;

    private void HabilitarArrastre()
    {
        PointerPressed += (s, e) =>
        {
            // Un toque sobre + / − / AUTO / MAN es un comando, no un arrastre.
            if (e.Source is Button || (e.Source as Control)?.Parent is Button) return;
            _arrastrando = true;
            _origenPuntero = e.GetPosition(Parent as Visual);
            e.Pointer.Capture(this);
        };
        PointerMoved += (s, e) =>
        {
            if (!_arrastrando) return;
            var p = e.GetPosition(Parent as Visual);
            var nuevo = new Point(
                Canvas.GetLeft(this) + (p.X - _origenPuntero.X),
                Canvas.GetTop(this) + (p.Y - _origenPuntero.Y));
            if (double.IsNaN(nuevo.X) || double.IsNaN(nuevo.Y)) return;
            Canvas.SetLeft(this, Math.Max(0, nuevo.X));
            Canvas.SetTop(this, Math.Max(0, nuevo.Y));
            _origenPuntero = p;
        };
        PointerReleased += (s, e) =>
        {
            if (!_arrastrando) return;
            _arrastrando = false;
            e.Pointer.Capture(null);
            double x = Canvas.GetLeft(this), y = Canvas.GetTop(this);
            if (!double.IsNaN(x) && !double.IsNaN(y)) OnMovido?.Invoke(new Point(x, y));
        };
    }

    // ---------- Ciclo de vida ----------------------------------------------

    public void Attach(WidgetQuantiXClient client)
    {
        _client = client;
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(PollMs), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_client == null) return;
        // Un timeout de HTTP tira TaskCanceledException aunque nadie haya
        // cancelado: si se tratara como cancelacion, el overlay se congelaria
        // con el ultimo dato bueno y nadie se enteraria.
        try { _estado = await _client.GetStateAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { _estado = null; }
        await Dispatcher.UIThread.InvokeAsync(Render);
    }

    // ---------- Render ------------------------------------------------------

    private List<QxWidgetMotor> MotoresVisibles()
    {
        var lista = new List<QxWidgetMotor>();
        var nodos = _estado?.Nodos;
        if (nodos == null) return lista;
        foreach (var n in nodos)
        {
            if (n?.Motores == null) continue;
            foreach (var m in n.Motores)
            {
                // Un motor sin dosis y sin actividad es un motor que no se está
                // usando: mostrarlo llena el overlay de ceros y tapa el mapa.
                if (m == null) continue;
                if (!m.Activo && m.Objetivo <= 0 && !m.ManualMode) continue;
                lista.Add(m);
            }
        }
        return lista;
    }

    private void Render()
    {
        var nodos = _estado?.Nodos;
        bool algunOnline = false;
        string titulo = "QuantiX";
        if (nodos != null && nodos.Count > 0)
        {
            foreach (var n in nodos) if (n != null && n.Online) { algunOnline = true; break; }
            titulo = nodos.Count == 1 && !string.IsNullOrEmpty(nodos[0].Nombre)
                ? nodos[0].Nombre!
                : "QuantiX · " + nodos.Count.ToString(CultureInfo.InvariantCulture) + " nodos";
        }
        _titulo.Text = titulo;
        _puntoOnline.Fill = _estado == null ? TextoDim : (algunOnline ? Acento : Rojo);

        var motores = MotoresVisibles();
        _filas.Children.Clear();

        if (motores.Count == 0)
        {
            _filas.Children.Add(new TextBlock
            {
                Text = _estado == null ? "Sin conexión con el motor" : "Sin motores en uso",
                Foreground = TextoDim,
                FontSize = 12,
            });
            _ctlManual.IsVisible = false;
            PintarModo(false);
            return;
        }

        foreach (var m in motores) _filas.Children.Add(FilaMotor(m));

        // MAN/AUTO y los pasos son globales, así que solo tienen sentido si
        // todos los motores están en el mismo modo y la misma unidad.
        bool todosMan = true, mismaUnidad = true;
        string? unidad = motores[0].Unidad;
        double dosisUniforme = motores[0].ManualDosis;
        bool dosisIgual = true;
        foreach (var m in motores)
        {
            if (!m.ManualMode) todosMan = false;
            if (!string.Equals(m.Unidad, unidad, StringComparison.OrdinalIgnoreCase)) mismaUnidad = false;
            if (Math.Abs(m.ManualDosis - dosisUniforme) > 0.001) dosisIgual = false;
        }

        PintarModo(todosMan);
        _ctlManual.IsVisible = todosMan;
        bool puedePasar = todosMan && mismaUnidad && dosisIgual;
        _btnMas.IsEnabled = puedePasar;
        _btnMenos.IsEnabled = puedePasar;
        _dosisManual.Text = puedePasar
            ? WidgetQuantiXClient.FormatoDosis(dosisUniforme, unidad) + " " + WidgetQuantiXClient.EtiquetaUnidad(unidad)
            : "varios";
    }

    private void PintarModo(bool manual)
    {
        _btnMan.Background  = manual ? Acento : BgFila;
        _btnMan.Foreground  = manual ? new SolidColorBrush(Color.Parse("#101612")) : TextoMid;
        _btnAuto.Background = manual ? BgFila : Acento;
        _btnAuto.Foreground = manual ? TextoMid : new SolidColorBrush(Color.Parse("#101612"));
    }

    private Control FilaMotor(QxWidgetMotor m)
    {
        // Color por desvío contra el objetivo: es lo que hace mirar el widget.
        IBrush color = TextoHi;
        if (m.Objetivo > 0)
        {
            double desvio = Math.Abs(m.Real - m.Objetivo) / m.Objetivo * 100.0;
            color = desvio <= 5 ? Acento : desvio <= 15 ? Ambar : Rojo;
        }
        if (!m.Activo) color = TextoDim;

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var izq = new StackPanel { Spacing = 1 };
        izq.Children.Add(new TextBlock
        {
            Text = m.Nombre ?? ("M" + m.Idx.ToString(CultureInfo.InvariantCulture)),
            Foreground = TextoDim,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
        });
        var linea = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
        linea.Children.Add(new TextBlock
        {
            Text = WidgetQuantiXClient.FormatoDosis(m.Real, m.Unidad),
            Foreground = color,
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        });
        linea.Children.Add(new TextBlock
        {
            Text = WidgetQuantiXClient.EtiquetaUnidad(m.Unidad),
            Foreground = TextoDim,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3),
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        izq.Children.Add(linea);
        Grid.SetColumn(izq, 0);
        g.Children.Add(izq);

        var der = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right };
        der.Children.Add(new TextBlock
        {
            Text = "obj " + WidgetQuantiXClient.FormatoDosis(m.Objetivo, m.Unidad),
            Foreground = TextoMid,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        });
        der.Children.Add(new TextBlock
        {
            Text = m.Rpm.ToString(CultureInfo.InvariantCulture) + " rpm",
            Foreground = TextoDim,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        });
        Grid.SetColumn(der, 1);
        g.Children.Add(der);

        return new Border
        {
            Background = BgFila,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Child = g,
        };
    }

    // ---------- Comandos ----------------------------------------------------

    private async Task CambiarModo(bool manual)
    {
        if (_client == null) return;
        // Al pasar a manual se arranca desde lo que el motor ya tenía como
        // objetivo: si mandáramos 0, la máquina se frenaría de golpe.
        double dosis = 0;
        var motores = MotoresVisibles();
        if (manual && motores.Count > 0)
            dosis = motores[0].ManualDosis > 0 ? motores[0].ManualDosis : motores[0].Objetivo;

        await _client.SetManualAllAsync(manual, dosis).ConfigureAwait(false);
        await TickAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    private async Task Paso(int dir)
    {
        if (_client == null) return;
        var motores = MotoresVisibles();
        if (motores.Count == 0) return;

        double actual = motores[0].ManualDosis;
        double siguiente = Math.Max(0, actual + dir * WidgetQuantiXClient.PasoDosis(actual));
        siguiente = Math.Round(siguiente * 10) / 10.0;

        await _client.SetManualAllAsync(true, siguiente).ConfigureAwait(false);
        await TickAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }
}
