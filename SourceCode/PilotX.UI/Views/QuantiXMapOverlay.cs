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
    // Fondos tintados de las celdas de la grilla (alpha ~0x33 sobre el mapa):
    // la mancha de color se ve de un vistazo, el número queda claro encima.
    private static readonly IBrush VerdeTenue = new SolidColorBrush(Color.Parse("#334ABA3E"));
    private static readonly IBrush AmbarTenue = new SolidColorBrush(Color.Parse("#33E2B53E"));
    private static readonly IBrush RojoTenue  = new SolidColorBrush(Color.Parse("#33E15A5A"));
    private static readonly IBrush TextoHi = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush TextoMid= new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush TextoDim= new SolidColorBrush(Color.Parse("#8FA092"));

    private const double PollMs = 500;

    private readonly StackPanel _raiz;
    private readonly TextBlock _titulo;
    private readonly Ellipse _puntoOnline;
    private readonly StackPanel _filas;

    // Rediseño 2026-08-05 (pantalla del taller, 10" = tamaño real de trabajo):
    // el overlay pasó a MONITOR compacto — lista vertical con real/objetivo de
    // todos los motores, sin botones por fila. Los controles del motor elegido
    // viven en la QuantiXControlBar horizontal (abajo, sobre la pasada); acá
    // solo se marca la selección y se la alimenta en cada poll.
    /// <summary>La barra horizontal de control. La monta MainWindow en el
    /// canvas (la posición es suya); el overlay la alimenta y la muestra.</summary>
    public QuantiXControlBar? Barra
    {
        get => _barra;
        set
        {
            _barra = value;
            if (_barra == null) return;
            _barra.OnAuto = () => _ = ComandoSeleccion(manual: false);
            _barra.OnMan = () => _ = ComandoSeleccion(manual: true);
            _barra.OnOff = () => _ = ApagarSeleccion();
            _barra.OnPaso = dir => _ = PasoSeleccion(dir);
            _barra.OnCerrar = Deseleccionar;
        }
    }
    private QuantiXControlBar? _barra;
    private string? _selUid;
    private int _selIdx = -1;

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
        Padding = new Thickness(10, 8, 10, 8);
        // Monitor compacto: en la 10" del taller cada pixel de mapa cuenta.
        MinWidth = 172;
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
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headIzq.Children.Add(_puntoOnline);
        headIzq.Children.Add(_titulo);
        Grid.SetColumn(headIzq, 0);
        head.Children.Add(headIzq);

        _raiz.Children.Add(head);

        // ---- Filas por motor (solo lectura + tap para elegir) ----
        _filas = new StackPanel { Spacing = 4 };
        _raiz.Children.Add(_filas);

        Child = _raiz;
        HabilitarArrastre();
    }

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
            // Clamp a los CUATRO bordes del canvas: sin el tope derecho/abajo
            // el widget se podía arrastrar fuera de pantalla y "perderse"
            // (pasaba en la pantalla del taller, 1024x768).
            double maxX = double.MaxValue, maxY = double.MaxValue;
            if (Parent is Control host && host.Bounds.Width > 0 && Bounds.Width > 0)
            {
                maxX = Math.Max(0, host.Bounds.Width - Bounds.Width);
                maxY = Math.Max(0, host.Bounds.Height - Bounds.Height);
            }
            Canvas.SetLeft(this, Math.Min(Math.Max(0, nuevo.X), maxX));
            Canvas.SetTop(this, Math.Min(Math.Max(0, nuevo.Y), maxY));
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
        // Overlay apagado (toggle del Hub) = barra afuera también: una barra
        // comandando motores sin su monitor a la vista es un control ciego.
        _selUid = null;
        _selIdx = -1;
        if (_barra != null) _barra.IsVisible = false;
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

    /// <summary>Un motor con el nodo al que pertenece: el comando por motor
    /// necesita el UID, y la lista plana lo perdía.</summary>
    private readonly struct MotorRef
    {
        public readonly string? Uid;
        /// <summary>Nombre del nodo (la tolva). Con dos tolvas los motores se
        /// llaman igual — "Producto 1" y "Producto 1" — y sin esto el operario
        /// no sabe a cuál le está tocando la dosis.</summary>
        public readonly string? NodoNombre;
        public readonly QxWidgetMotor Motor;
        public MotorRef(string? uid, string? nodoNombre, QxWidgetMotor motor)
        {
            Uid = uid; NodoNombre = nodoNombre; Motor = motor;
        }
    }

    private List<MotorRef> MotoresVisibles()
    {
        var lista = new List<MotorRef>();
        var nodos = _estado?.Nodos;
        if (nodos == null) return lista;
        foreach (var n in nodos)
        {
            if (n?.Motores == null) continue;
            foreach (var m in n.Motores)
            {
                // TODOS los motores, SIEMPRE. Antes se ocultaba el que no
                // estaba activo y tenía objetivo 0 ("no llenar el overlay de
                // ceros"), y eso mentía en cabina: bastaba un objetivo que
                // caía a 0 un instante (cabecera, anti-solape, secciones
                // cerradas) o un frame de estado con activo=false por un hueco
                // de MQTT para que el motor DESAPARECIERA estando andando.
                // Peor: al cambiar la cantidad se rearmaba la grilla y los que
                // quedaban se renumeraban y saltaban de lugar. El estado
                // (parado/andando) se muestra atenuando la celda, nunca
                // sacándola. Pedido de cabina 2026-09-04.
                if (m == null) continue;
                lista.Add(new MotorRef(n.Uid, n.Nombre, m));
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

        if (motores.Count == 0)
        {
            if (_celdas.Count > 0 || _filas.Children.Count == 0)
            {
                _filas.Children.Clear();
                _celdas.Clear();
                _filas.Children.Add(new TextBlock
                {
                    // Ya no se filtra por actividad: si no hay ninguna celda es
                    // porque no hay motores CONFIGURADOS (o no hay estado), no
                    // porque estén parados.
                    Text = _estado == null ? "Sin conexión con el motor" : "Sin motores configurados",
                    Foreground = TextoDim,
                    FontSize = 15,
                });
            }
            // Sin motores no hay nada que comandar: barra afuera.
            _selUid = null;
            _selIdx = -1;
            if (_barra != null) _barra.IsVisible = false;
            return;
        }

        // Las celdas se REUSAN entre polls: recrear todos los controles cada
        // 500 ms genera texturas/vertex buffers nuevos en Skia sin parar y en
        // esta GPU (Intel con el driver flojo) termina en "Could not allocate
        // vertices" + crash del compositor. Solo se rearma la estructura si
        // cambió la cantidad de motores; el resto es actualizar textos/brushes.
        if (_celdas.Count != motores.Count) ConstruirGrilla(motores);
        for (int i = 0; i < motores.Count; i++)
            ActualizarCelda(_celdas[i], motores[i], i + 1);

        // Alimentar la barra horizontal con el estado FRESCO del seleccionado.
        // Si el motor elegido desapareció (nodo offline, se reconfiguró la
        // sembradora), la selección se suelta: una barra comandando un motor
        // que ya no está es peor que ninguna barra.
        if (_selIdx >= 0)
        {
            var sel = BuscarSeleccion(motores);
            // Limpieza directa y no Deseleccionar(): ese re-llama Render y
            // estamos DENTRO de Render — un nivel de recursión gratis.
            if (sel == null)
            {
                _selUid = null; _selIdx = -1;
                if (_barra != null) _barra.IsVisible = false;
            }
            else if (_barra != null)
            {
                var m = sel.Value.Motor;
                double dosis = m.ManualMode ? ObjetivoDePartida(m) : m.Objetivo;
                _barra.Actualizar(
                    NombreDeFila(sel.Value),
                    m.ManualMode,
                    WidgetQuantiXClient.FormatoDosis(dosis, m.Unidad),
                    WidgetQuantiXClient.EtiquetaUnidad(m.Unidad),
                    m.Rpm,
                    m.Apagado);
                _barra.IsVisible = true;
            }
        }
    }

    private MotorRef? BuscarSeleccion(List<MotorRef> motores)
    {
        foreach (var r in motores)
            if (r.Motor.Idx == _selIdx && string.Equals(r.Uid, _selUid, StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    private void Seleccionar(MotorRef r)
    {
        // Tocar el mismo motor lo deselecciona: el toque es un toggle.
        if (_selIdx == r.Motor.Idx && string.Equals(_selUid, r.Uid, StringComparison.OrdinalIgnoreCase))
        {
            Deseleccionar();
            return;
        }
        _selUid = r.Uid;
        _selIdx = r.Motor.Idx;
        Render();
    }

    private void Deseleccionar()
    {
        _selUid = null;
        _selIdx = -1;
        if (_barra != null) _barra.IsVisible = false;
        Render();
    }

    // Grilla de celdas numeradas: una celda por motor, número + real + rpm,
    // pintada por desvío. Con N motores (14 en la venta de esta semana) las
    // filas ya no escalan; la mancha de color deja ver el estado de todos de
    // un vistazo, y el control del que se toca sale en la barra de abajo.
    //
    // Reparto: hasta 7 columnas, filas balanceadas (14 -> 2x7, 8 -> 2x4,
    // 3 -> 1x3, 15 -> 3x5). El ancho del overlay crece con las columnas — es
    // arrastrable, el mapa se sigue viendo alrededor.
    //
    // Las celdas son PERSISTENTES (ver Render): acá solo se crea la
    // estructura; los valores los pinta ActualizarCelda en cada poll.
    private sealed class Celda
    {
        public Button Btn = null!;
        public TextBlock Num = null!;
        public TextBlock Dosis = null!;
        public TextBlock Rpm = null!;
        /// <summary>El MotorRef FRESCO del último poll; el click lee de acá.</summary>
        public MotorRef Ref;
    }

    private readonly List<Celda> _celdas = new();

    private void ConstruirGrilla(List<MotorRef> motores)
    {
        _filas.Children.Clear();
        _celdas.Clear();

        int n = motores.Count;
        int filas = (int)Math.Ceiling(n / 7.0);
        int cols  = (int)Math.Ceiling(n / (double)filas);

        int i = 0;
        for (int f = 0; f < filas; f++)
        {
            var filaH = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            for (int c = 0; c < cols && i < n; c++, i++)
            {
                var celda = CrearCelda();
                _celdas.Add(celda);
                filaH.Children.Add(celda.Btn);
            }
            _filas.Children.Add(filaH);   // _filas tiene Spacing=4: separa las filas
        }
    }

    // Estructura de la celda: número grande arriba; debajo lo que el motor
    // está ENTREGANDO (real, en sem/m o kg/ha) y sus rpm (pedido 2026-08-21).
    // El MANUAL NO se marca acá: se ve en la barra del elegido. Button y no
    // Border: el arrastre del overlay ignora los toques sobre Button, así que
    // elegir un motor no "agarra" el panel.
    private Celda CrearCelda()
    {
        var celda = new Celda();

        // Tipografía dimensionada para leerse de un vistazo desde el asiento,
        // sin anteojos y con el tractor moviéndose (pedido de cabina
        // 2026-09-04): antes 15/10/10 px era ilegible a esa distancia.
        celda.Num = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        celda.Dosis = new TextBlock
        {
            Foreground = TextoMid,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        celda.Rpm = new TextBlock
        {
            Foreground = TextoDim,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var contenido = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        contenido.Children.Add(celda.Num);
        contenido.Children.Add(celda.Dosis);
        contenido.Children.Add(celda.Rpm);

        celda.Btn = new Button
        {
            CornerRadius = new CornerRadius(6),
            // Aire acorde a la tipografía nueva y blanco táctil decente.
            Padding = new Thickness(8, 5),
            MinWidth = 78,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = contenido,
        };
        celda.Btn.Click += (_, __) => Seleccionar(celda.Ref);
        return celda;
    }

    // Pinta una celda con el estado fresco del motor: fondo/borde por desvío
    // (verde ±5%, ámbar ±15%, rojo más allá — mismo criterio que la barra),
    // apagado/sin dosis = gris y sin detalle.
    private void ActualizarCelda(Celda celda, MotorRef r, int numero)
    {
        var m = r.Motor;
        celda.Ref = r;

        IBrush borde = Borde, fondo = BgFila;
        if (m.Apagado)
        {
            // Apagado a mano: se ve de lejos y NO se confunde con un motor
            // parado por cabecera. Sigue apagado tras reiniciar, así que tiene
            // que cantar — un surco sin sembrar toda la jornada cuesta plata.
            borde = Rojo; fondo = RojoTenue;
        }
        else if (m.Activo && m.Objetivo > 0)
        {
            double desvio = Math.Abs(m.Real - m.Objetivo) / m.Objetivo * 100.0;
            if (desvio <= 5)       { borde = Acento; fondo = VerdeTenue; }
            else if (desvio <= 15) { borde = Ambar;  fondo = AmbarTenue; }
            else                   { borde = Rojo;   fondo = RojoTenue; }
        }

        bool sel = m.Idx == _selIdx
            && string.Equals(r.Uid, _selUid, StringComparison.OrdinalIgnoreCase);

        celda.Num.Text = numero.ToString(CultureInfo.InvariantCulture);
        celda.Num.Foreground = m.Activo ? TextoHi : TextoDim;

        // Dosis y rpm SIEMPRE ocupan su lugar: si se ocultan, la celda cambia
        // de alto y la grilla entera salta cuando un motor arranca o para.
        // Motor parado = guiones, no un número viejo que mienta.
        if (m.Activo)
        {
            celda.Dosis.Text = WidgetQuantiXClient.FormatoDosis(m.Real, m.Unidad)
                             + " " + WidgetQuantiXClient.EtiquetaUnidad(m.Unidad);
            celda.Rpm.Text = m.Rpm.ToString(CultureInfo.InvariantCulture) + " rpm";
        }
        else if (m.Apagado)
        {
            // Apagado a mano: se dice con todas las letras. No hay dosis que
            // mostrar y el operario tiene que entender que ese surco NO se
            // está sembrando (ni se pinta).
            celda.Dosis.Text = "APAGADO";
            celda.Rpm.Text = "— rpm";
        }
        else
        {
            // Con objetivo pero sin girar (secciones cerradas, en cabecera):
            // se muestra a cuánto está pedido, atenuado, para que el operario
            // sepa que el motor está configurado y esperando.
            celda.Dosis.Text = m.Objetivo > 0
                ? WidgetQuantiXClient.FormatoDosis(m.Objetivo, m.Unidad)
                  + " " + WidgetQuantiXClient.EtiquetaUnidad(m.Unidad)
                : "—";
            celda.Rpm.Text = "— rpm";
        }
        celda.Dosis.Foreground = m.Apagado ? Rojo : (m.Activo ? TextoMid : TextoDim);
        celda.Num.Foreground = m.Apagado ? Rojo : (m.Activo ? TextoHi : TextoDim);

        celda.Btn.Background = sel ? SelFondo : fondo;
        celda.Btn.BorderBrush = sel ? Acento : borde;
        celda.Btn.BorderThickness = new Thickness(sel ? 2 : 1);
        celda.Btn.MinWidth = m.Activo ? 66 : 40;
        celda.Btn.MinHeight = m.Activo ? 52 : 36;   // tocable con guante
    }

    private static readonly IBrush SelFondo = new SolidColorBrush(Color.Parse("#26404A34"));

    /// <summary>Cómo se identifica la fila. Con una sola tolva alcanza el
    /// nombre del motor; con varias hay que decir de qué tolva es, porque los
    /// motores suelen llamarse igual en las dos.</summary>
    private string NombreDeFila(MotorRef r)
    {
        string motor = r.Motor.Nombre ?? ("M" + r.Motor.Idx.ToString(CultureInfo.InvariantCulture));
        int nodos = _estado?.Nodos?.Count ?? 0;
        if (nodos <= 1 || string.IsNullOrEmpty(r.NodoNombre)) return motor;
        return r.NodoNombre + " · " + motor;
    }

    // 40 px de alto: entra en la fila del motor y se sigue pudiendo tocar con
    // ---------- Comandos (del motor SELECCIONADO, vía la barra) -------------
    //
    // La barra no conoce el estado: pide "modo" o "paso" y acá se resuelve
    // contra la selección FRESCA del último poll — el MotorRef capturado al
    // tocar la fila puede tener datos de hace varios segundos.

    private async Task ComandoSeleccion(bool manual)
    {
        if (_client == null) return;
        var sel = BuscarSeleccion(MotoresVisibles());
        if (sel == null) return;
        var m = sel.Value.Motor;
        // Al pasar a manual se arranca desde lo que el motor ya tenía como
        // objetivo: si mandáramos 0, la máquina se frenaría de golpe.
        double dosis = manual ? ObjetivoDePartida(m) : 0;
        await _client.SetManualAsync(sel.Value.Uid, m.Idx, manual, dosis).ConfigureAwait(false);
        await TickAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>OFF / volver a prender el motor elegido. Es un interruptor: si
    /// está apagado lo prende, si no lo apaga. El motor queda fuera de la
    /// siembra hasta que lo prendan —también después de reiniciar—, así que en
    /// el overlay queda marcado en rojo para que no pase inadvertido.</summary>
    private async Task ApagarSeleccion()
    {
        if (_client == null) return;
        var sel = BuscarSeleccion(MotoresVisibles());
        if (sel == null) return;
        var m = sel.Value.Motor;
        await _client.SetApagadoAsync(sel.Value.Uid, m.Idx, !m.Apagado).ConfigureAwait(false);
        await TickAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PasoSeleccion(int dir)
    {
        if (_client == null) return;
        var sel = BuscarSeleccion(MotoresVisibles());
        if (sel == null) return;
        var m = sel.Value.Motor;
        double actual = m.ManualDosis > 0 ? m.ManualDosis : m.Objetivo;
        await _client.SetManualAsync(sel.Value.Uid, m.Idx, true, Siguiente(actual, dir)).ConfigureAwait(false);
        await TickAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Con qué dosis entra en manual: la última que se dejó a mano, o
    /// la que está aplicando ahora. Nunca 0 — eso frenaría la máquina de golpe.</summary>
    private static double ObjetivoDePartida(QxWidgetMotor m)
        => m.ManualDosis > 0 ? m.ManualDosis : m.Objetivo;

    private static double Siguiente(double actual, int dir)
    {
        double v = Math.Max(0, actual + dir * WidgetQuantiXClient.PasoDosis(actual));
        return Math.Round(v * 10) / 10.0;
    }
}
