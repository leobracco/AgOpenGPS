// ============================================================================
// SiembraTab.cs — tab Siembra del editor nativo de QuantiX (config + monitor).
//
// QUÉ QUEDÓ NATIVO: la tira de surcos (planter) con pintado por arrastre, la
// toolbar de reparto, la lista de motores en modo Configurar y en modo En
// marcha, la vista Tabla y los botones Guardar / Enviar a nodos.
// QUÉ SIGUE EN HTML: la misma tab en pages/quantix.html, para la PWA.
//
// Dos modos:
//   · Configurar — se reparte la sembradora. NO se reconstruye con el tick:
//     el rebuild a 2 Hz destruía el árbol abajo del dedo (un desplegable
//     recién abierto se cerraba solo; reporte 2026-08-10 sobre el HTML).
//   · En marcha  — dosis real vs objetivo por motor. Acá sí se refresca con
//     cada tick porque no hay nada editable que perder.
//
// 1 surco = 1 motor en TODO el conjunto: pintar un surco lo saca de cualquier
// otro motor de cualquier nodo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.QuantiXEditor;

public sealed class SiembraTab : QxTab
{
    private const int TOPE_MOTORES = 24;

    private string _vista = "planter";     // "planter" | "tabla"

    private readonly WrapPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _lista = new() { Spacing = 6 };
    private readonly StackPanel _tabla = new() { Spacing = 0 };
    private readonly TextBlock _capL = QxUi.Sub("");
    private readonly TextBlock _capR = QxUi.Sub("");
    private readonly TextBlock _modo = QxUi.Titulo("Configurar");
    private readonly TextBlock _huerfanos = new()
    {
        FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false,
    };
    private readonly TextBlock _msg = QxUi.Msg();
    private readonly StackPanel _toolbar = QxUi.Fila();

    /// <summary>Botón "Cargar placas" (se bloquea mientras giran los motores).</summary>
    private Button? _btnCargar;
    /// <summary>Hay una carga de placas en curso: no encadenar otra.</summary>
    private bool _cargando;
    private readonly Border _brushChip;
    private readonly TextBlock _brushLbl = new()
    {
        FontSize = 12, Foreground = QxUi.Texto, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Border _brushSw = new()
    {
        Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private Button? _btnConfig;
    private Button? _segPlanter;
    private Button? _segTabla;
    private Border? _planterWrap;

    private bool _pintando;
    private int _ultimaCelda = -1;

    public SiembraTab(QxEditorCtx c) : base(c)
    {
        var sw = QxUi.Fila(6);
        sw.Children.Add(_brushSw);
        sw.Children.Add(_brushLbl);
        _brushChip = new Border
        {
            Background = QxUi.BgFila, BorderBrush = QxUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 6, 12, 6),
            Child = sw, VerticalAlignment = VerticalAlignment.Center,
        };

        // Transparent, no null: con Background null el WrapPanel no es
        // hit-testable y un toque que cae en el hueco entre celdas no
        // arrancaría el arrastre.
        _strip.Background = Brushes.Transparent;
        _strip.PointerPressed += OnStripPressed;
        _strip.PointerMoved += OnStripMoved;
        _strip.PointerReleased += OnStripReleased;
        _strip.PointerCaptureLost += (_, __) => TerminarPintado();
    }

    // =======================================================================
    //  Rebuild completo
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        // Los reusados que viven adentro de contenedores que se rearman:
        // hay que desengancharlos o Avalonia tira "already has a visual parent".
        QxUi.Soltar(_modo); QxUi.Soltar(_capL); QxUi.Soltar(_capR);
        QxUi.Soltar(_strip); QxUi.Soltar(_msg);

        bool live = C.SiembraEnMarcha;

        // ---- cabecera: modo + toggle Configurar/En vivo + segmento vista ----
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        _modo.Text = PilotX.Cockpit.Bars.Traductor.T(live ? "En marcha" : "Configurar");
        Grid.SetColumn(_modo, 0);
        head.Children.Add(_modo);

        // El umbral de velocidad no alcanza en el banco: la placa GPS mete
        // 1-2 km/h fantasma y la pantalla se iba sola a "En marcha". Este
        // botón es la palanca manual, y solo aparece con detección viva.
        _btnConfig = QxUi.Boton(C.ConfigForzado ? "Volver a en vivo" : "Configurar", () =>
        {
            C.ConfigForzado = !C.ConfigForzado;
            C.ComputeEnMarcha();
            Rebuild();
        });
        _btnConfig.IsVisible = C.EnMarchaDetectada;
        _btnConfig.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_btnConfig, 1);
        _btnConfig.HorizontalAlignment = HorizontalAlignment.Left;
        head.Children.Add(_btnConfig);

        var seg = QxUi.Fila(4);
        _segPlanter = QxUi.Boton("Planter", () => { _vista = "planter"; Rebuild(); });
        _segTabla   = QxUi.Boton("Tabla",   () => { _vista = "tabla";   Rebuild(); });
        PintarSegmento();
        seg.Children.Add(_segPlanter);
        seg.Children.Add(_segTabla);
        Grid.SetColumn(seg, 2);
        head.Children.Add(seg);
        Children.Add(head);

        // Sin secciones configuradas no hay geometría para calcular dosis por
        // hectárea. Se avisa; no se inventa un ancho por defecto.
        if (C.Impl != null && C.AnchoPilotX <= 0)
        {
            var av = QxUi.Sub("PilotX no tiene ancho de labor configurado. Cargalo en Implemento "
                            + "PilotX (ancho y cantidad de secciones): de ahí salen los surcos, la "
                            + "distancia entre hileras y la dosis por hectárea.");
            av.Foreground = QxUi.Warn;
            Children.Add(av);
        }

        // ---- planter ----
        _capL.Text = PilotX.Cockpit.Bars.Traductor.T(live
            ? "Sembradora en vivo · gris = surco cortado"
            : "Sembradora · color = motor");
        ActualizarCaptionDerecha();

        var caps = new Grid();
        caps.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        caps.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(_capL, 0); caps.Children.Add(_capL);
        _capR.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_capR, 1); caps.Children.Add(_capR);

        var wrapSp = new StackPanel { Spacing = 6 };
        wrapSp.Children.Add(caps);
        wrapSp.Children.Add(_strip);
        _planterWrap = QxUi.Card(wrapSp);
        _planterWrap.IsVisible = _vista == "planter";
        Children.Add(_planterWrap);

        _huerfanos.Foreground = QxUi.Warn;
        Children.Add(_huerfanos);

        // ---- toolbar (solo en config) ----
        _toolbar.Children.Clear();
        _toolbar.IsVisible = !live && _vista == "planter";
        if (_toolbar.IsVisible)
        {
            _toolbar.Children.Add(_brushChip);
            _toolbar.Children.Add(QxUi.Boton("+ Motor", AgregarMotor));
            _toolbar.Children.Add(QxUi.Boton("Auto-repartir", AutoRepartir));
            _toolbar.Children.Add(QxUi.Boton("Quitar surco", QuitarSurco));
        }
        Children.Add(_toolbar);

        // ---- lista de motores / tabla ----
        _lista.IsVisible = _vista == "planter";
        _tabla.IsVisible = _vista == "tabla";
        Children.Add(_lista);
        Children.Add(_tabla);

        // ---- acciones ----
        var acciones = QxUi.Fila();
        acciones.Children.Add(QxUi.Boton("Guardar", () => _ = GuardarAsync(), primario: true));
        acciones.Children.Add(QxUi.Boton("Enviar a nodos", () => _ = EnviarATodosAsync()));
        _btnCargar = QxUi.Boton("Cargar placas", () => _ = CargarPlacasAsync());
        ToolTip.SetTip(_btnCargar, PilotX.Cockpit.Bars.Traductor.T(
            "Gira una vuelta cada dosificador para llenar las placas antes de sembrar"));
        acciones.Children.Add(_btnCargar);
        acciones.Children.Add(_msg);
        Children.Add(acciones);

        RenderStrip();
        RenderListaMotores();
        RenderTabla();
        ActualizarHuerfanos();
        ActualizarBrushChip();
    }

    private void PintarSegmento()
    {
        if (_segPlanter == null || _segTabla == null) return;
        bool p = _vista == "planter";
        _segPlanter.Background = p ? QxUi.Verde : QxUi.BgFila;
        _segPlanter.Foreground = p ? Brushes.White : QxUi.Texto;
        _segTabla.Background = !p ? QxUi.Verde : QxUi.BgFila;
        _segTabla.Foreground = !p ? Brushes.White : QxUi.Texto;
    }

    // =======================================================================
    //  Tick live
    // =======================================================================

    public override void Live()
    {
        ActualizarCaptionDerecha();
        if (C.SiembraEnMarcha)
        {
            // Nada editable en pantalla: reconstruir es gratis y mantiene el
            // color de los surcos cortados al día.
            RenderStrip();
            RenderListaMotores();
            RenderTabla();
        }
        else if (_vista == "tabla")
        {
            RenderTabla();
        }
    }

    private void ActualizarCaptionDerecha()
    {
        _capR.Text = C.SiembraEnMarcha
            ? C.AogSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " km/h · "
              + C.AogAreaHa.ToString("0.0", CultureInfo.InvariantCulture) + " ha"
            : PilotX.Cockpit.Bars.Traductor.T("tocá un surco para pintarlo con el motor activo");
    }

    // =======================================================================
    //  Tira de surcos
    // =======================================================================

    private void RenderStrip()
    {
        _strip.Children.Clear();
        var all = C.AllMotors();
        if (all.Count == 0)
        {
            _strip.Children.Add(QxUi.Sub("No hay nodos QuantiX configurados"));
            return;
        }
        int total = C.TotalSurcos();
        for (int s = 1; s <= total; s++)
            _strip.Children.Add(Celda(s));
    }

    private Border Celda(int surco)
    {
        bool cortado = C.SiembraEnMarcha && C.SectionOn != null
                       && surco - 1 < C.SectionOn.Length && !C.SectionOn[surco - 1];
        int owner = C.SurcoOwner(surco);

        IBrush fondo;
        IBrush texto = Brushes.White;
        IBrush borde = QxUi.Borde;
        if (cortado) { fondo = QxUi.Dim; }
        else if (owner < 0) { fondo = new SolidColorBrush(Color.Parse("#D6DCD6")); texto = QxUi.TextoMuted; }
        else
        {
            fondo = QxEditorCtx.MotorBrush(owner);
            if (owner == C.BrushMotor) borde = Brushes.White;
        }

        return new Border
        {
            Width = 30, Height = 40,
            Margin = new Thickness(0, 0, 3, 3),
            CornerRadius = new CornerRadius(4),
            Background = fondo,
            BorderBrush = borde,
            BorderThickness = new Thickness(owner >= 0 && owner == C.BrushMotor ? 2 : 1),
            Tag = surco,
            Child = new TextBlock
            {
                Text = surco.ToString(CultureInfo.InvariantCulture),
                Foreground = texto, FontSize = 11, FontFamily = QxUi.Mono,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>Repinta SOLO los fondos de las celdas ya creadas. Durante el
    /// arrastre no se reconstruye el árbol: con 48 celdas y la lista de
    /// motores abajo, rebuildear en cada PointerMoved satura el UI thread de
    /// una pantalla lenta.</summary>
    private void RepintarCeldas()
    {
        foreach (var ch in _strip.Children)
        {
            if (ch is not Border b || b.Tag is not int surco) continue;
            int owner = C.SurcoOwner(surco);
            if (owner < 0)
            {
                b.Background = new SolidColorBrush(Color.Parse("#D6DCD6"));
                b.BorderBrush = QxUi.Borde;
                b.BorderThickness = new Thickness(1);
                if (b.Child is TextBlock t0) t0.Foreground = QxUi.TextoMuted;
            }
            else
            {
                b.Background = QxEditorCtx.MotorBrush(owner);
                bool esPincel = owner == C.BrushMotor;
                b.BorderBrush = esPincel ? Brushes.White : QxUi.Borde;
                b.BorderThickness = new Thickness(esPincel ? 2 : 1);
                if (b.Child is TextBlock t1) t1.Foreground = Brushes.White;
            }
        }
    }

    private void OnStripPressed(object? s, PointerPressedEventArgs e)
    {
        if (C.SiembraEnMarcha || _vista != "planter") return;
        _pintando = true;
        _ultimaCelda = -1;
        e.Pointer.Capture(_strip);
        AplicarPintado(e.GetPosition(_strip));
        e.Handled = true;
    }

    private void OnStripMoved(object? s, PointerEventArgs e)
    {
        if (!_pintando) return;
        AplicarPintado(e.GetPosition(_strip));
    }

    private void OnStripReleased(object? s, PointerReleasedEventArgs e) => TerminarPintado();

    private void TerminarPintado()
    {
        if (!_pintando) return;
        _pintando = false;
        _ultimaCelda = -1;
        // El rebuild de la lista se hace UNA vez al soltar, no por celda.
        RenderListaMotores();
        RenderTabla();
        ActualizarHuerfanos();
    }

    private void AplicarPintado(Point p)
    {
        var hit = _strip.InputHitTest(p);
        var b = BuscarCelda(hit as Visual);
        if (b?.Tag is not int surco) return;
        if (surco == _ultimaCelda) return;    // throttle: solo al cambiar de celda
        _ultimaCelda = surco;
        C.PintarSurco(surco);
        RepintarCeldas();
    }

    private static Border? BuscarCelda(Visual? v)
    {
        while (v != null)
        {
            if (v is Border b && b.Tag is int) return b;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void ActualizarHuerfanos()
    {
        var h = C.SurcosHuerfanos();
        if (h.Count == 0) { _huerfanos.IsVisible = false; _huerfanos.Text = ""; return; }
        _huerfanos.IsVisible = true;
        _huerfanos.Text = h.Count + PilotX.Cockpit.Bars.Traductor.T(h.Count == 1 ? " surco sin motor: " : " surcos sin motor: ")
                        + string.Join(", ", h);
    }

    private void ActualizarBrushChip()
    {
        var all = C.AllMotors();
        _brushSw.Background = QxEditorCtx.MotorBrush(C.BrushMotor);
        string label = "—";
        if (C.BrushMotor >= 0 && C.BrushMotor < all.Count)
        {
            var e = all[C.BrushMotor];
            label = (string.IsNullOrEmpty(e.Motor.Nombre) ? "Motor " + (C.BrushMotor + 1) : e.Motor.Nombre)
                  + C.NodoTag(e);
        }
        _brushLbl.Text = PilotX.Cockpit.Bars.Traductor.T("Pincel: ") + label;
    }

    // =======================================================================
    //  Toolbar
    // =======================================================================

    private void AgregarMotor()
    {
        var all = C.AllMotors();
        if (all.Count >= TOPE_MOTORES)
        {
            QxUi.SetMsg(_msg, "Máximo 24 motores", "err");
            return;
        }
        QxNodoConfig? nodo = (C.BrushMotor >= 0 && C.BrushMotor < all.Count)
            ? all[C.BrushMotor].Nodo
            : (all.Count > 0 ? all[all.Count - 1].Nodo : null);
        if (nodo == null)
        {
            for (int i = C.Cfg.Nodos.Count - 1; i >= 0; i--)
                if (C.Cfg.Nodos[i] != null && C.Cfg.Nodos[i].Habilitado) { nodo = C.Cfg.Nodos[i]; break; }
        }
        if (nodo == null) return;

        nodo.Motores ??= new List<QxMotorConfig>();
        var m = new QxMotorConfig { Nombre = "Motor " + (nodo.Motores.Count + 1), Cortes = new List<int>() };
        nodo.Motores.Add(m);

        var after = C.AllMotors();
        for (int i = 0; i < after.Count; i++)
            if (ReferenceEquals(after[i].Motor, m)) { C.BrushMotor = i; break; }

        RenderStrip(); RenderListaMotores(); ActualizarBrushChip();
    }

    // ── Copiar dosis a toda la sembradora ───────────────────────────────────
    // Copia SOLO dosis + unidad. Los alvéolos / semillas por vuelta NO se
    // tocan: son dato físico del dosificador de cada motor y copiarlos haría
    // que un motor con otra placa dosifique mal sin que se note.
    // No guarda ni envía: quedan como cambios pendientes para revisar y tocar
    // Guardar, así un toque accidental no pisa una config de dosis variable.
    private void CopiarDosisATodos(QxMotorConfig origen)
    {
        if (origen == null) return;

        int n = 0;
        foreach (var e in C.AllMotors())
        {
            var m = e.Motor;
            if (m == null || ReferenceEquals(m, origen)) continue;
            m.DosisFija = origen.DosisFija;
            m.UnidadDosis = origen.UnidadDosis;
            n++;
        }

        RenderListaMotores();
        RenderTabla();

        string unidad = string.Equals(origen.UnidadDosis, "sem_m", StringComparison.Ordinal)
            ? "sem/m" : "kg/ha";
        QxUi.SetMsg(_msg, n == 0
            ? "No hay otros motores para copiar."
            : $"Dosis {origen.DosisFija.ToString("0.##", CultureInfo.InvariantCulture)} {unidad} "
              + $"copiada a {n} motor" + (n == 1 ? "" : "es") + ". Tocá Guardar para aplicar.",
            n == 0 ? "" : "ok");
    }

    // ── Cargar placas ───────────────────────────────────────────────────────
    // Gira UNA vuelta cada dosificador para que las placas queden llenas antes
    // de arrancar la pasada (si no, los primeros metros salen sin semilla).
    //
    // Una vuelta = dientes_engranaje pulsos (pulsos por vuelta del
    // dosificador), contados por el firmware, que frena solo al llegar: el
    // mismo mecanismo del "Girar X pulsos" de la tab Prueba.
    //
    // Sirve con CUALQUIER sensor que cuente pulsos —encoder o inductivo—: al
    // firmware no le importa de dónde vienen, corta al llegar a la meta. Lo
    // único que hace falta es que `dientes_engranaje` (pulsos por vuelta del
    // dosificador) esté bien cargado, y ese número CAMBIA según el sensor:
    // 600 con un encoder LPD3806, 24 con un inductivo que lee los alvéolos de
    // la placa. Si está mal, la vuelta sale mal — por eso se exige > 0 y se
    // avisa de los que quedan afuera en vez de girarlos a ciegas.
    private async Task CargarPlacasAsync()
    {
        if (_cargando) return;

        var conSensor = new List<QxMotorEntry>();
        var sinSensor = new List<string>();
        foreach (var e in C.AllMotors())
        {
            var m = e.Motor;
            if (m == null || !m.Habilitado) continue;
            if (m.DientesEngranaje > 0) conSensor.Add(e);
            else sinSensor.Add(m.Nombre ?? "motor");
        }

        if (conSensor.Count == 0)
        {
            QxUi.SetMsg(_msg, "Ningún motor tiene cargados los pulsos por vuelta: "
                            + "sin ese dato no se puede girar una vuelta exacta.", "err");
            return;
        }

        _cargando = true;
        if (_btnCargar != null) _btnCargar.IsEnabled = false;
        try
        {
            QxUi.SetMsg(_msg, $"Cargando placas… ({conSensor.Count} motores)", "");

            int ok = 0;
            var fallaron = new List<string>();
            foreach (var e in conSensor)
            {
                var m = e.Motor;
                // PWM de carga: el mínimo con el que ese motor arranca, más un
                // margen. Girar al PWM de trabajo para una sola vuelta es
                // innecesariamente brusco sobre la placa.
                int pwm = Math.Min(m.PwmMax, Math.Max(m.PwmMin, (int)(m.PwmMin * 1.5)));
                var r = await C.Client.CalStartAsync(e.Uid, e.MotorIdx, m.DientesEngranaje, pwm)
                                      .ConfigureAwait(true);
                if (r != null && r.Ok) ok++;
                else fallaron.Add(m.Nombre ?? ("motor " + (e.MotorIdx + 1)));
            }

            var partes = new List<string>();
            partes.Add(ok > 0
                ? $"✓ {ok} motor{(ok == 1 ? "" : "es")} girando una vuelta"
                : "✕ ninguno arrancó");
            if (fallaron.Count > 0) partes.Add("fallaron: " + string.Join(", ", fallaron));
            if (sinSensor.Count > 0)
                partes.Add($"{sinSensor.Count} sin pulsos por vuelta cargados "
                           + "(no se giraron): " + string.Join(", ", sinSensor));

            QxUi.SetMsg(_msg, string.Join(" · ", partes),
                        fallaron.Count == 0 && ok > 0 ? "ok" : "err");
        }
        finally
        {
            _cargando = false;
            if (_btnCargar != null) _btnCargar.IsEnabled = true;
        }
    }

    private void AutoRepartir()
    {
        var all = C.AllMotors();
        if (all.Count == 0) return;
        int total = C.TotalSurcos();
        foreach (var e in all) e.Motor.Cortes = new List<int>();
        if (all.Count < total)
        {
            // 1 surco por motor, en orden.
            for (int i = 0; i < all.Count && i < total; i++) all[i].Motor.Cortes.Add(i + 1);
        }
        else
        {
            int n = all.Count;
            for (int s = 1; s <= total; s++)
            {
                int g = (int)Math.Floor((s - 1) * (double)n / total);
                if (g >= 0 && g < n) all[g].Motor.Cortes.Add(s);
            }
        }
        RenderStrip(); RenderListaMotores(); ActualizarHuerfanos(); RenderTabla();
    }

    private void QuitarSurco()
    {
        if (C.UltimoSurcoTocado is not int s) return;
        foreach (var e in C.AllMotors())
            e.Motor.Cortes?.RemoveAll(c => c == s);
        RenderStrip(); RenderListaMotores(); ActualizarHuerfanos(); RenderTabla();
    }

    // =======================================================================
    //  Lista de motores
    // =======================================================================

    private void RenderListaMotores()
    {
        _lista.Children.Clear();
        var all = C.AllMotors();
        if (all.Count == 0) return;

        if (C.SiembraEnMarcha) { RenderListaLive(all); return; }

        // Nota única: el tren de cada fila es solo-lectura, derivado del
        // implemento central. SIN link (en HTML un <a> navegaba el propio
        // diálogo WebView y la Configuración quedaba incrustada adentro).
        _lista.Children.Add(QxUi.Sub("Tren: derivado del implemento — se configura en Configuración › Secciones"));

        for (int i = 0; i < all.Count; i++)
            _lista.Children.Add(FilaConfig(all, i));
    }

    private Control FilaConfig(List<QxMotorEntry> all, int i)
    {
        var e = all[i];
        var m = e.Motor;
        bool hab = m.Habilitado;

        // --- renglón 1: quién es y qué surcos alimenta ---
        var l1 = QxUi.Fila(8);
        l1.Children.Add(QxUi.Swatch(i));

        var chk = QxUi.Check("", hab);
        chk.MinWidth = 26;
        ToolTip.SetTip(chk, PilotX.Cockpit.Bars.Traductor.T("Motor conectado. Destildado no recibe dosis."));
        chk.IsCheckedChanged += (_, __) =>
        {
            m.Habilitado = chk.IsChecked == true;
            RenderListaMotores();
        };
        l1.Children.Add(chk);

        var nombre = QxUi.Entrada(C.Client, m.Nombre ?? ("Motor " + (i + 1)), false, "Nombre del motor", 150);
        int idxNombre = i;
        // No se re-renderiza en vivo para no perder el foco mientras se tipea.
        nombre.LostFocus += (_, __) =>
        {
            var v = (nombre.Text ?? "").Trim();
            m.Nombre = string.IsNullOrEmpty(v) ? ("Motor " + (idxNombre + 1)) : v;
            ActualizarBrushChip();
        };
        l1.Children.Add(nombre);

        string cortes = (m.Cortes == null || m.Cortes.Count == 0)
            ? "sin surcos"
            : "surcos " + string.Join(",", m.Cortes.OrderBy(x => x));
        l1.Children.Add(QxUi.Sub(cortes + C.NodoTag(e)));

        l1.Children.Add(PillTren(m));

        var del = QxUi.Boton("×", () => _ = BorrarMotorAsync(i), peligro: true);
        del.Width = 44;
        l1.Children.Add(del);

        // --- renglón 2: cómo dosifica ---
        var l2 = QxUi.Fila(8);
        bool esSem = string.Equals(m.UnidadDosis, "sem_m", StringComparison.Ordinal);

        var dosis = QxUi.Entrada(C.Client, m.DosisFija.ToString("0.0", CultureInfo.InvariantCulture),
                               true, "Dosis fija", 90);
        dosis.LostFocus += (_, __) => m.DosisFija = QxUi.LeerDouble(dosis, m.DosisFija);
        l2.Children.Add(dosis);

        var uni = QxUi.Boton(esSem ? "sem/m" : "kg/ha", () =>
        {
            m.UnidadDosis = esSem ? "kg_ha" : "sem_m";
            RenderListaMotores();
        });
        ToolTip.SetTip(uni, PilotX.Cockpit.Bars.Traductor.T("Cambiar unidad (kg/ha ↔ sem/m)"));
        l2.Children.Add(uni);

        // Copiar ESTA dosis al resto de la sembradora. Lo normal es sembrar
        // todo a la misma dosis y cargarla surco por surco es un suplicio en
        // una máquina de 24 motores.
        var copiar = QxUi.Boton("⇊ a todos", () => CopiarDosisATodos(m));
        ToolTip.SetTip(copiar, PilotX.Cockpit.Bars.Traductor.T(
            "Copiar esta dosis y su unidad a TODOS los motores (no guarda solo)"));
        l2.Children.Add(copiar);

        if (esSem)
        {
            // Placa neumática: los alvéolos son dato de chapa, se cargan
            // directo. Otro dosificador: sale de calibrar por conteo.
            var tipos = new[] { ("", "Dosificador…"), ("placa", "Placa neumática"), ("calibrado", "A calibrar") };
            var cbTipo = QxUi.Combo();
            cbTipo.ItemsSource = tipos.Select(t => t.Item2).ToList();
            string tipoAct = m.TipoDosificacion ?? "";
            cbTipo.SelectedIndex = Math.Max(0, Array.FindIndex(tipos, t => t.Item1 == tipoAct));
            cbTipo.SelectionChanged += (_, __) =>
            {
                int k = cbTipo.SelectedIndex;
                if (k >= 0 && k < tipos.Length) m.TipoDosificacion = tipos[k].Item1;
                RenderListaMotores();
            };
            l2.Children.Add(cbTipo);

            bool esPlaca = tipoAct == "placa";
            var sv = QxUi.Entrada(C.Client, m.SemillasVuelta.ToString("0.##", CultureInfo.InvariantCulture),
                                true, esPlaca ? "Alvéolos de la placa" : "Semillas por vuelta", 90);
            sv.LostFocus += (_, __) => m.SemillasVuelta = QxUi.LeerDouble(sv, m.SemillasVuelta);
            l2.Children.Add(sv);
            l2.Children.Add(QxUi.Sub(esPlaca ? "alvéolos" : "sem/vuelta"));

            if (tipoAct == "calibrado")
                l2.Children.Add(QxUi.Boton("Calibrar →", () => C.IrATab?.Invoke("calibrar")));
        }

        l2.Children.Add(ComboMapa(m));

        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(l1);
        sp.Children.Add(l2);

        var fila = new Border
        {
            Background = i == C.BrushMotor ? QxUi.BgFilaSel : QxUi.BgFila,
            BorderBrush = QxUi.Borde,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Opacity = hab ? 1.0 : 0.55,
            Child = sp,
        };
        // Tap en la fila = fijar pincel, SIN robar el foco a los controles.
        fila.PointerPressed += (s, ev) =>
        {
            if (EsInteractivo(ev.Source as Visual)) return;
            C.BrushMotor = i;
            ActualizarBrushChip();
            RepintarCeldas();
            RenderListaMotores();
        };
        return fila;
    }

    private static bool EsInteractivo(Visual? v)
    {
        while (v != null)
        {
            if (v is Button || v is CheckBox || v is TextBox || v is ComboBox) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    /// <summary>Tren SOLO LECTURA: se deriva del implemento central (espejo de
    /// TrenResolver.cs). Ya no se elige acá.</summary>
    private Control PillTren(QxMotorConfig m)
    {
        var d = C.DerivarTrenMotor(m.Cortes);
        if (d == null)
        {
            var p = QxUi.Chip(PilotX.Cockpit.Bars.Traductor.T("Tren: manual (nodo)"));
            ToolTip.SetTip(p, PilotX.Cockpit.Bars.Traductor.T(
                "El implemento no tiene trenes configurados: se usa el valor manual del nodo"));
            return p;
        }
        if (d.Conflicto)
        {
            var p = QxUi.Chip("⚠ " + PilotX.Cockpit.Bars.Traductor.T("surcos de trenes distintos"), QxUi.Warn);
            ToolTip.SetTip(p, PilotX.Cockpit.Bars.Traductor.T(
                "Los surcos de este motor pertenecen a trenes distintos del implemento — se usa el del primero"));
            return p;
        }
        var pill = QxUi.Chip(PilotX.Cockpit.Bars.Traductor.T("Tren: ") + d.Nombre);
        ToolTip.SetTip(pill, PilotX.Cockpit.Bars.Traductor.T(
            "Tren derivado del implemento — se configura en Configuración › Secciones"));
        return pill;
    }

    /// <summary>"Dosis fija" + cada columna numérica del shape. Si el motor
    /// apunta a una columna que el shape actual no tiene, se conserva VISIBLE
    /// pero deshabilitada: así no se pierde la config.</summary>
    private ComboBox ComboMapa(QxMotorConfig m)
    {
        var campos = C.ShapeDoseFields();
        string cur = m.CampoDosis ?? "";
        bool falta = !string.IsNullOrEmpty(cur) && !campos.Contains(cur);

        var items = new List<ComboBoxItem>
        {
            new ComboBoxItem { Content = PilotX.Cockpit.Bars.Traductor.T("Dosis fija"), Tag = "" },
        };
        foreach (var nm in campos)
            items.Add(new ComboBoxItem { Content = PilotX.Cockpit.Bars.Traductor.T("Mapa: ") + nm, Tag = nm });
        if (falta)
            items.Add(new ComboBoxItem
            {
                Content = PilotX.Cockpit.Bars.Traductor.T("Mapa: ") + cur + " " + PilotX.Cockpit.Bars.Traductor.T("(sin shape)"),
                Tag = cur, IsEnabled = false,
            });

        var cb = QxUi.Combo();
        cb.ItemsSource = items;
        cb.SelectedIndex = Math.Max(0, items.FindIndex(it => (string)(it.Tag ?? "") == cur));
        ToolTip.SetTip(cb, PilotX.Cockpit.Bars.Traductor.T("Dosis fija o mapa (columna del shapefile)"));
        cb.SelectionChanged += (_, __) =>
        {
            if (cb.SelectedItem is ComboBoxItem it) m.CampoDosis = (string)(it.Tag ?? "");
        };
        return cb;
    }

    private async Task BorrarMotorAsync(int flatIdx)
    {
        var all = C.AllMotors();
        if (flatIdx < 0 || flatIdx >= all.Count) return;
        var e = all[flatIdx];
        string nombre = string.IsNullOrEmpty(e.Motor.Nombre) ? ("Motor " + (flatIdx + 1)) : e.Motor.Nombre;
        bool ok = C.Confirmar == null || await C.Confirmar("Borrar motor",
            "¿Borrar " + nombre + "? Sus surcos quedan sin motor.").ConfigureAwait(true);
        if (!ok) return;

        e.Nodo.Motores?.RemoveAt(e.MotorIdx);
        int n = C.AllMotors().Count;
        if (C.BrushMotor >= n) C.BrushMotor = n > 0 ? n - 1 : 0;
        RenderStrip(); RenderListaMotores(); ActualizarBrushChip(); ActualizarHuerfanos(); RenderTabla();
    }

    // ---- lista en modo EN MARCHA ------------------------------------------

    private void RenderListaLive(List<QxMotorEntry> all)
    {
        var ctx = C.AgroCtx();
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            var m = e.Motor;
            var live = C.LiveMotor(e.Uid, e.MotorIdx);
            double real = live?.PpsReal ?? 0;
            double target = live?.PpsTarget ?? 0;
            int rpm = live?.Rpm ?? 0;
            bool cutAll = C.MotorAllCut(m);

            string badge = "OK";
            IBrush badgeColor = QxUi.Ok;
            double pct = target > 0 ? Math.Min(100, real / target * 100.0) : 0;
            bool desvio = false;
            if (cutAll) { badge = "corte"; badgeColor = QxUi.Dim; pct = 0; }
            else if (target > 0 && Math.Abs(real - target) / target > 0.15)
            { badge = "desvío"; badgeColor = QxUi.Warn; desvio = true; }

            var uObj = QxAgro.Primary(QxAgro.Units(m, target, ctx));
            string objVal = cutAll ? "—" : uObj.V;
            string realLine = cutAll ? ("— " + uObj.U) : QxAgro.Label(QxAgro.Units(m, real, ctx));

            var fila = QxUi.Fila(10);
            fila.Children.Add(QxUi.Swatch(i));
            fila.Children.Add(new TextBlock
            {
                Text = (string.IsNullOrEmpty(m.Nombre) ? "Motor " + (i + 1) : m.Nombre) + C.NodoTag(e),
                Foreground = QxUi.Texto, FontSize = 13, MinWidth = 130,
                VerticalAlignment = VerticalAlignment.Center,
            });
            fila.Children.Add(QxUi.Etiqueta("obj " + uObj.U));
            fila.Children.Add(new TextBlock
            {
                Text = objVal, Foreground = QxUi.Verde, FontSize = 15, FontWeight = FontWeight.Bold,
                FontFamily = QxUi.Mono, VerticalAlignment = VerticalAlignment.Center, MinWidth = 54,
            });
            fila.Children.Add(new TextBlock
            {
                Text = realLine, Foreground = QxUi.TextoMuted, FontSize = 12, FontFamily = QxUi.Mono,
                VerticalAlignment = VerticalAlignment.Center, MinWidth = 170,
            });
            fila.Children.Add(new TextBlock
            {
                Text = rpm.ToString(CultureInfo.InvariantCulture) + " rpm",
                Foreground = QxUi.TextoMuted, FontSize = 12, FontFamily = QxUi.Mono,
                VerticalAlignment = VerticalAlignment.Center, MinWidth = 64,
            });
            fila.Children.Add(Barra(pct / 100.0, desvio ? QxUi.Warn : QxUi.Verde));
            fila.Children.Add(QxUi.Chip(badge, badgeColor));

            _lista.Children.Add(new Border
            {
                Background = QxUi.BgFila, BorderBrush = QxUi.Borde, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6),
                Child = fila,
            });
        }
    }

    private static Control Barra(double frac, IBrush color)
    {
        double safe = Math.Clamp(frac, 0, 1);
        var g = new Grid { Width = 120, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E2E7E2")),
            CornerRadius = new CornerRadius(5),
        });
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(safe, GridUnitType.Star)));
        fill.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - safe, GridUnitType.Star)));
        var b = new Border { Background = color, CornerRadius = new CornerRadius(5) };
        Grid.SetColumn(b, 0);
        fill.Children.Add(b);
        g.Children.Add(fill);
        return g;
    }

    // =======================================================================
    //  Vista tabla
    // =======================================================================

    private void RenderTabla()
    {
        _tabla.Children.Clear();
        if (_vista != "tabla") return;
        var all = C.AllMotors();
        if (all.Count == 0) return;

        var ctx = C.AgroCtx();
        var g = new Grid();
        for (int i = 0; i < 7; i++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        string[] heads = { "Motor", "Surcos", "Dosis fija", "Efectiva", "Real", "RPM", "Estado" };
        for (int c = 0; c < heads.Length; c++)
        {
            var h = QxUi.Etiqueta(PilotX.Cockpit.Bars.Traductor.T(heads[c]));
            h.Margin = new Thickness(0, 4, 14, 6);
            Grid.SetRow(h, 0); Grid.SetColumn(h, c);
            g.Children.Add(h);
        }

        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i]; var m = e.Motor;
            var live = C.LiveMotor(e.Uid, e.MotorIdx);
            double realPps = live?.PpsReal ?? 0;
            string real = live != null ? QxAgro.Label(QxAgro.Units(m, realPps, ctx)) : "—";
            string rpm = live != null ? live.Rpm.ToString(CultureInfo.InvariantCulture) : "—";
            string unidad = string.Equals(m.UnidadDosis, "sem_m", StringComparison.Ordinal) ? "sem/m" : "kg/ha";
            string fija = m.DosisFija.ToString("0.0", CultureInfo.InvariantCulture) + " " + unidad;
            string ef = string.IsNullOrEmpty(m.CampoDosis)
                ? fija + " " + PilotX.Cockpit.Bars.Traductor.T("fija")
                : PilotX.Cockpit.Bars.Traductor.T("mapa ") + m.CampoDosis;
            string estado = (C.SiembraEnMarcha && C.MotorAllCut(m)) ? "○ corte" : "● dosif.";
            string surcos = (m.Cortes != null && m.Cortes.Count > 0) ? string.Join(",", m.Cortes) : "—";

            int fila = i + 1;
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var nom = QxUi.Fila(6);
            nom.Children.Add(QxUi.Swatch(i));
            nom.Children.Add(new TextBlock
            {
                Text = (string.IsNullOrEmpty(m.Nombre) ? "M" + (i + 1) : m.Nombre) + C.NodoTag(e),
                Foreground = QxUi.Texto, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            });
            nom.Margin = new Thickness(0, 2, 14, 2);
            Grid.SetRow(nom, fila); Grid.SetColumn(nom, 0);
            g.Children.Add(nom);

            Celda(g, fila, 1, surcos);
            Celda(g, fila, 2, fija);
            Celda(g, fila, 3, ef);
            Celda(g, fila, 4, real);
            Celda(g, fila, 5, rpm);
            Celda(g, fila, 6, estado);
        }
        _tabla.Children.Add(QxUi.Card(g));
    }

    private static void Celda(Grid g, int fila, int col, string texto)
    {
        var t = new TextBlock
        {
            Text = texto, Foreground = QxUi.TextoMuted, FontSize = 12, FontFamily = QxUi.Mono,
            Margin = new Thickness(0, 2, 14, 2), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(t, fila); Grid.SetColumn(t, col);
        g.Children.Add(t);
    }

    // =======================================================================
    //  Guardar / enviar
    // =======================================================================

    private async Task GuardarAsync()
    {
        var h = C.SurcosHuerfanos();
        if (h.Count > 0 && C.Confirmar != null)
        {
            bool ok = await C.Confirmar("Surcos sin motor",
                "Hay " + h.Count + (h.Count == 1 ? " surco" : " surcos")
                + " sin motor asignado (" + string.Join(", ", h) + "). ¿Guardar igual?").ConfigureAwait(true);
            if (!ok) { QxUi.SetMsg(_msg, "Guardado cancelado.", ""); return; }
        }
        QxUi.SetMsg(_msg, "Guardando…", "");
        var r = await C.GuardarAsync().ConfigureAwait(true);
        if (r.Ok) QxUi.SetMsg(_msg, "✓ Guardado.", "ok");
        else QxUi.SetMsg(_msg, r.TextoError(), "err");
    }

    private async Task EnviarATodosAsync()
    {
        QxUi.SetMsg(_msg, "Enviando todos…", "");
        await C.GuardarAsync().ConfigureAwait(true);
        int sent = 0, fail = 0;
        foreach (var n in C.Cfg.Nodos)
        {
            if (n == null || !n.Habilitado || string.IsNullOrEmpty(n.Uid)) continue;
            var r = await C.Client.SendNodoAsync(n.Uid).ConfigureAwait(true);
            if (r.Ok) sent++; else fail++;
        }
        QxUi.SetMsg(_msg, PilotX.Cockpit.Bars.Traductor.T("Enviados: ") + sent
                        + PilotX.Cockpit.Bars.Traductor.T(" · Fallos: ") + fail,
                    fail == 0 ? "ok" : "err");
    }
}
