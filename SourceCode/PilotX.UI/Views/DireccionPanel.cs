// ============================================================================
// DireccionPanel.cs — panel de DIRECCIÓN nativo (17vo port), CHICO, para la
// tablet de 10". Reemplaza el uso diario de la ventana HTML "Dirección —
// Autoguiado" (1040px de WebView): lo que el operario toca en cabina son
// cuatro cosas — probar el motor, el sensor de ángulo, la fuerza y guardar.
// Todo lo demás (PP/Stanley/avanzado/velocidades/barra guía) sigue en la
// página HTML completa, detrás del botón "Todo…".
//
// Patrón GuiasPanel/LotePanel: Border flotante sobre el mapa VIVO, paleta
// clara PilotX, HTTP contra los MISMOS endpoints que usa la página:
//   GET/POST /api/steer/config      (objeto completo — el POST NO es merge:
//                                    siempre se reenvía el JSON entero leído,
//                                    con solo los campos tocados cambiados)
//   POST     /api/steer/zero-was
//   GET/POST /api/steer/freedrive[/angle|/zero]
//   GET      /api/aog/graph-steer   (Objetivo/Actual en vivo, 2 Hz)
//
// Seguridad: cerrar el panel (✕, u otro panel que lo pise) APAGA el manejo
// libre si quedó prendido — nunca queda el volante bajo control manual sin
// nadie mirándolo (misma doctrina que la página HTML).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PilotX.Desktop.Views;

public sealed class DireccionPanel : Border
{
    // ---- paleta PilotX (misma que GuiasPanel/LotePanel) --------------------
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgCard     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgSel      = new SolidColorBrush(Color.Parse("#DCEFD8"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush Ambar      = new SolidColorBrush(Color.Parse("#B9802E"));

    private HttpClient? _http;
    private string _base = "";

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;

    // ---- estado -------------------------------------------------------------
    private JsonObject? _cfg;           // config completa del GET (se reenvía entera)
    private bool _sucio;
    private bool _fdOn;                 // manejo libre prendido (dice el motor)
    private DispatcherTimer? _timer;    // 500 ms: graph-steer siempre + freedrive si Probar

    // header en vivo
    private readonly TextBlock _liveObj = Num("—");
    private readonly TextBlock _liveAct = Num("—");
    private readonly TextBlock _liveErr = Num("—");

    // pantallas
    private readonly StackPanel _scProbar;
    private readonly StackPanel _scSensor;
    private readonly StackPanel _scFuerza;
    private readonly StackPanel _scGuiado;
    private readonly StackPanel _scModulo;
    private readonly StackPanel _scPantalla;
    private readonly Dictionary<string, Button> _tabs = new();

    // Ayuda contextual: banner bajo las tabs que muestra la explicación del
    // control cuyo "?" se tocó. Nada de Flyouts (no se dibujan sobre el mapa
    // GL): es un Border de la misma card, se cierra tocándolo.
    private readonly Border _tip;
    private readonly TextBlock _tipTitulo;
    private readonly TextBlock _tipCuerpo;

    // Refresco de las filas construidas con los helpers genéricos: cada fila
    // registra cómo repintarse desde _cfg (PintarConfig las corre todas).
    private readonly List<Action> _refrescos = new();

    // Probar
    private readonly Button _fdPower;
    private readonly Button _fdIzq;
    private readonly Button _fdDer;
    private readonly Button _fdCero;
    private readonly TextBlock _fdAngulo = new()
    {
        Text = "0°", FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Texto,
        MinWidth = 72, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _fdNota;

    // Sensor
    private readonly Button _segRty;
    private readonly Button _segEncoder;
    private readonly TextBlock _valCuentas = Num("—");
    private readonly Button _tglInvWas;
    private readonly Button _tglInvMotor;
    // vúmetro del ángulo en vivo (tocar la barra = poner en cero)
    private readonly Border _wasFillIzq;
    private readonly Border _wasFillDer;
    private readonly TextBlock _wasAng;


    // Fuerza
    private readonly TextBlock _valPwmMin  = Num("—");
    private readonly TextBlock _valPwmAlto = Num("—");
    private readonly TextBlock _valGanP    = Num("—");

    // pie
    private readonly Button _btnGuardar;
    private readonly Button _btnDescartar;
    private bool _descartarArmado;
    private readonly TextBlock _estado;

    public DireccionPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(10);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        Width = 470;
        IsVisible = false;

        // ---------- header: título + Obj/Act/Err + ✕ ----------
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(2, 0, 0, 6) };
        var titulo = new TextBlock
        {
            Text = "Dirección", FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titulo, 0);

        var live = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        live.Children.Add(LiveCelda("OBJ", _liveObj));
        live.Children.Add(LiveCelda("ACT", _liveAct));
        live.Children.Add(LiveCelda("ERR", _liveErr));
        Grid.SetColumn(live, 1);

        var btnCerrar = BotonChico("✕");
        btnCerrar.Width = 44;
        btnCerrar.Click += (_, _) => Cerrar();
        Grid.SetColumn(btnCerrar, 2);

        header.Children.Add(titulo);
        header.Children.Add(live);
        header.Children.Add(btnCerrar);

        // ---------- tabs (2 filas de 3 — TODO el FormSteer vive acá) ----------
        var tabs = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (id, txt) in new[]
        {
            ("probar", "Probar"), ("sensor", "Sensor"), ("fuerza", "Fuerza"),
            ("guiado", "Guiado"), ("modulo", "Módulo"), ("pantalla", "Pantalla"),
        })
        {
            var b = new Button
            {
                Content = txt, Height = 46, FontSize = 13, FontWeight = FontWeight.SemiBold,
                Background = BgCard, Foreground = TextoMuted,
                BorderBrush = Borde, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            string mio = id;
            b.Click += (_, _) => MostrarTab(mio);
            _tabs[id] = b;
            tabs.Children.Add(b);
        }

        // ---------- pantalla PROBAR ----------
        _fdPower = new Button
        {
            Content = "Prender", Height = 54, MinWidth = 96, FontSize = 13.5, FontWeight = FontWeight.Bold,
            Background = BgCard, Foreground = Texto, BorderBrush = Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10), HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _fdPower.Click += async (_, _) => await FdPost("/api/steer/freedrive", "{\"on\":" + (!_fdOn ? "true" : "false") + "}");

        _fdIzq  = BotonFd("−1°");
        _fdDer  = BotonFd("+1°");
        _fdCero = BotonFd("0↔5°");
        _fdCero.MinWidth = 84;
        _fdIzq.Click  += async (_, _) => await FdPost("/api/steer/freedrive/angle", "{\"dir\":-1}");
        _fdDer.Click  += async (_, _) => await FdPost("/api/steer/freedrive/angle", "{\"dir\":1}");
        _fdCero.Click += async (_, _) => await FdPost("/api/steer/freedrive/zero", "{}");

        var fdFila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fdFila.Children.Add(_fdPower);
        fdFila.Children.Add(_fdIzq);
        fdFila.Children.Add(_fdAngulo);
        fdFila.Children.Add(_fdDer);
        fdFila.Children.Add(_fdCero);

        _fdNota = new TextBlock
        {
            Text = "Con el tractor parado. Se apaga solo si el tractor arranca.",
            FontSize = 12.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 8, 0, 0),
        };

        _scProbar = new StackPanel { Spacing = 4 };
        _scProbar.Children.Add(SubTituloAyuda("Manejo libre — flecha derecha = ruedas a la derecha",
            "Prueba manual del motor, sin guía y con el tractor PARADO. Prender habilita las flechas: cada toque corre el objetivo 1° y el motor va hasta ahí y FRENA. 0↔5° salta el objetivo para ver la respuesta. Si en vez de frenar se va al tope, el sensor o el motor están invertidos (pestaña Sensor). Se apaga solo si el tractor arranca, y también al cerrar este panel."));
        _scProbar.Children.Add(fdFila);
        _scProbar.Children.Add(_fdNota);

        // ---------- pantalla SENSOR ----------
        _segRty     = BotonSeg("Sensor RTY (simple)");
        _segEncoder = BotonSeg("Encoder del motor");
        _segRty.Click     += (_, _) => { SetSeg(rty: true);  MarcarSucio(); };
        _segEncoder.Click += (_, _) => { SetSeg(rty: false); MarcarSucio(); };
        var segFila = new UniformGrid { Columns = 2 };
        segFila.Children.Add(Envolver(_segRty, 0, 4));
        segFila.Children.Add(Envolver(_segEncoder, 4, 0));

        var filaCuentas = FilaNumerica("Cuentas por grado", _valCuentas,
            () => Nudge("counts_per_degree", -1, 1, 255),
            () => Nudge("counts_per_degree", +1, 1, 255),
            "La escala del sensor: cuántas cuentas equivalen a 1° de rueda. Si el ángulo en pantalla " +
            "exagera, subí el número; si se queda corto, bajalo. RTY: 59. Encoder: se mide de tope a tope. " +
            "Interfiere en TODO el guiado — con la escala mal, el piloto gira de más o de menos.");

        // Vúmetro horizontal del ángulo EN VIVO (pedido 2026-08-10): girás el
        // volante y VES para dónde va la barra — si va al revés del volante,
        // el WAS está invertido. Tocar la barra con las ruedas derechas = 0°.
        _wasFillIzq = new Border
        {
            Background = Verde, CornerRadius = new CornerRadius(4, 0, 0, 4),
            Width = 0, HorizontalAlignment = HorizontalAlignment.Right,
        };
        _wasFillDer = new Border
        {
            Background = Verde, CornerRadius = new CornerRadius(0, 4, 4, 0),
            Width = 0, HorizontalAlignment = HorizontalAlignment.Left,
        };
        _wasAng = new TextBlock
        {
            Text = "—", FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Texto,
            MinWidth = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        var mitadIzq = new Border
        {
            Child = _wasFillIzq, BorderBrush = Borde, BorderThickness = new Thickness(0, 0, 1, 0),
            ClipToBounds = true,
        };
        var mitadDer = new Border { Child = _wasFillDer, ClipToBounds = true };
        var barraGrid = new Grid();
        barraGrid.ColumnDefinitions = new ColumnDefinitions("*,*");
        Grid.SetColumn(mitadIzq, 0);
        Grid.SetColumn(mitadDer, 1);
        barraGrid.Children.Add(mitadIzq);
        barraGrid.Children.Add(mitadDer);
        var track = new Border
        {
            Child = barraGrid, Height = 34, Background = BgCard,
            BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), ClipToBounds = true,
            // toque = cero (el track entero es el objetivo, 34px + ancho total)
        };
        track.PointerPressed += async (_, _) => await ZeroWas();
        var filaVu = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetColumn(track, 0);
        _wasAng.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_wasAng, 1);
        filaVu.Children.Add(track);
        filaVu.Children.Add(_wasAng);
        var vuNota = new TextBlock
        {
            Text = "Girá a la DERECHA: la barra va a la derecha (si va al revés → Invertir sensor).",
            FontSize = 11.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 2, 0, 0),
        };

        // Botón explícito de cero (pedido de banco 2026-08-13): el toque en la
        // barra sigue andando como atajo, pero nadie lo descubre solo — la
        // acción principal merece un botón que diga lo que hace.
        var btnCeroWas = new Button
        {
            Content = "Poner en cero — con las ruedas derechas",
            Height = 44, FontSize = 14, FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = BgCard, Foreground = Texto,
            BorderBrush = Verde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6, 0, 0),
        };
        btnCeroWas.Click += async (_, _) => await ZeroWas();

        // Ajuste fino del cero (hsbarWasOffset del FormSteer, ±4000 cuentas):
        // el botón de arriba clava el cero de un saque; esto corre el cero de a
        // pasitos cuando en el lote se ve que siembra corrido siempre para el
        // mismo lado. Se muestra en GRADOS (offset ÷ cuentas/grado), igual que
        // el lblSteerAngleSensorZero original. Paso 20 = LargeChange original.
        var valCeroFino = Num("—");
        Action refCeroFino = () =>
        {
            int cpd = Math.Max(1, Entero("counts_per_degree"));
            double deg = Entero("was_offset") / (double)cpd;
            valCeroFino.Text = deg.ToString("F2", CultureInfo.InvariantCulture) + "°";
        };
        _refrescos.Add(refCeroFino);
        var filaCeroFino = FilaNumerica("Ajuste fino del cero", valCeroFino,
            () => { NudgeCrudo("was_offset", -20, -4000, 4000); refCeroFino(); MarcarSucio(); },
            () => { NudgeCrudo("was_offset", +20, -4000, 4000); refCeroFino(); MarcarSucio(); },
            "Corre el cero del sensor de a poquito, en cuentas crudas (se muestra en grados). " +
            "Usalo cuando el piloto siembra SIEMPRE corrido para el mismo lado: cada toque mueve " +
            "el cero un poco hacia ese lado. Para el cero grueso usá el botón con las ruedas derechas.");

        _tglInvWas   = BotonSeg("Invertir sensor (WAS)");
        _tglInvMotor = BotonSeg("Invertir motor");
        _tglInvWas.Click   += (_, _) => { ToggleBool("invert_was", _tglInvWas); };
        _tglInvMotor.Click += (_, _) => { ToggleBool("invert_steer", _tglInvMotor); };
        var invFila = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
        invFila.Children.Add(Envolver(_tglInvWas, 0, 4));
        invFila.Children.Add(Envolver(_tglInvMotor, 4, 0));

        _scSensor = new StackPanel { Spacing = 4, IsVisible = false };
        _scSensor.Children.Add(SubTituloAyuda("Qué sensor mide el ángulo de las ruedas",
            "El piloto necesita el ángulo REAL de las ruedas. RTY = sensor en el eje (modo simple). Encoder del motor = cuenta las vueltas del Keya (modo diferencial); andando a más de 1,2 km/h se autocorrige contra el GPS. Es uno o el otro: al cambiar, poné en cero y recalibrá las cuentas."));
        _scSensor.Children.Add(segFila);
        _scSensor.Children.Add(filaCuentas);
        var chipCero = ChipAyuda("Vúmetro del ángulo y cero",
            "La barra muestra el ángulo EN VIVO: girá el volante a la derecha y tiene que llenarse hacia " +
            "la derecha — si va al revés, prendé Invertir sensor. Con las ruedas BIEN derechas, tocá " +
            "Poner en cero. Un cero corrido hace que el piloto siembre corrido de la línea.");
        chipCero.VerticalAlignment = VerticalAlignment.Center;
        var filaVuConChip = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(filaVu, 0);
        Grid.SetColumn(chipCero, 1);
        filaVuConChip.Children.Add(filaVu);
        filaVuConChip.Children.Add(chipCero);
        _scSensor.Children.Add(filaVuConChip);
        _scSensor.Children.Add(btnCeroWas);
        _scSensor.Children.Add(vuNota);
        _scSensor.Children.Add(filaCeroFino);
        var filaInv = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(invFila, 0);
        var chipInv = ChipAyuda("Invertir sensor / motor",
            "Sensor invertido: girás a la derecha y el ángulo marca izquierda. Motor invertido: la flecha " +
            "derecha mueve las ruedas a la izquierda. Con algo al revés el piloto EMPUJA para el lado " +
            "equivocado y se va al tope. En modo encoder, Invertir sensor además apaga la corrección por " +
            "GPS (útil en el banco de pruebas).");
        chipInv.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chipInv, 1);
        filaInv.Children.Add(invFila);
        filaInv.Children.Add(chipInv);
        _scSensor.Children.Add(filaInv);

        // ---------- pantalla FUERZA ----------
        _scFuerza = new StackPanel { Spacing = 4, IsVisible = false };
        _scFuerza.Children.Add(SubTitulo("Fuerza del motor de dirección"));
        _scFuerza.Children.Add(FilaNumerica("Mínima para mover (PWM mín)", _valPwmMin,
            () => Nudge("min_pwm", -1, 0, 255), () => Nudge("min_pwm", +1, 0, 255),
            "La fuerza justa para que el motor ARRANQUE a moverse. Muy baja: se planta cerca del objetivo " +
            "y nunca llega. Muy alta: tironea al centrar. Subila de a 1 hasta que arranque parejo."));
        _scFuerza.Children.Add(FilaNumerica("Máxima (PWM alto)", _valPwmAlto,
            () => Nudge("high_steer_pwm", -5, 20, 255), () => Nudge("high_steer_pwm", +5, 20, 255),
            "El tope de fuerza: limita qué tan violento puede girar el volante. Alto = correcciones " +
            "bruscas; dejalo moderado hasta validar en el lote."));
        _scFuerza.Children.Add(FilaNumerica("Fuerza de corrección (Ganancia P)", _valGanP,
            () => Nudge("proportional_gain", -5, 0, 200), () => Nudge("proportional_gain", +5, 0, 200),
            "Cuánto empuja por cada grado de error. Alta: llega rápido pero puede pasarse y oscilar. " +
            "Baja: anda dormido y deja error. Se afina mirando el salto 0↔5° de la pestaña Probar."));

        // ---------- pantalla GUIADO (PP + Stanley + general + avanzado) ----------
        // El wire guarda los ENTEROS crudos del slider original; la escala es
        // solo de display (hold_look_ahead=29 → 2,9 s). Igual que la página.
        _scGuiado = new StackPanel { Spacing = 2, IsVisible = false };
        _scGuiado.Children.Add(SubTitulo("Modo suave (Pure Pursuit)"));
        _scGuiado.Children.Add(FilaAjuste("Qué tan adelante mira", "hold_look_ahead", 10, 70, 0.1, 1, "s",
            "Cuántos segundos adelante mira el modo suave para decidir el giro. Más = anda tranquilo y hace curvas amplias; menos = se pega a la línea pero puede serpentear."));
        _scGuiado.Children.Add(FilaAjuste("Multiplicador por velocidad", "look_ahead_mult", 5, 60, 0.1, 1, "",
            "Cuánto crece la mirada al aumentar la velocidad. Si a alta velocidad serpentea, subilo."));
        _scGuiado.Children.Add(FilaAjuste("Entrada a la línea", "acquire_factor", 20, 300, 0.01, 2, "",
            "Qué tan agresivo entra a la guía desde lejos. Alto = entra derecho y rápido; bajo = entra en una curva suave y larga."));
        _scGuiado.Children.Add(FilaAjuste("Integral (PP)", "integral_pp", 0, 100, 1, 0, "",
            "Corrige el error que queda pegado (viento, ladera, implemento que tira). Demasiado alto = balanceo lento de un lado al otro."));
        _scGuiado.Children.Add(SubTituloSep("Modo firme (Stanley)"));
        _scGuiado.Children.Add(FilaAjuste("Ganancia Stanley", "stanley_gain", 1, 40, 0.1, 1, "",
            "Cuánto pesa la distancia a la línea en el modo firme. Alto = vuelve rápido pero puede ponerse nervioso."));
        _scGuiado.Children.Add(FilaAjuste("Ganancia de rumbo", "heading_error_gain", 1, 15, 0.1, 1, "",
            "Cuánto pesa el error de rumbo (apuntar torcido). Subilo si cruza la línea en ángulo en vez de enderezarse antes."));
        _scGuiado.Children.Add(FilaAjuste("Integral (Stanley)", "integral_stanley", 0, 100, 1, 0, "",
            "Igual que la integral de PP pero para el modo firme: mata el corrimiento constante."));
        _scGuiado.Children.Add(FilaToggle("Usar siempre Stanley (puro)", "stanley_pure",
            "Usa el modo firme también para entrar a la línea (sin la entrada suave de PP). Para implementos que exigen precisión desde el primer metro."));
        _scGuiado.Children.Add(SubTituloSep("General"));
        _scGuiado.Children.Add(FilaAjuste("Ángulo máximo de giro", "max_steer_angle", 10, 80, 1, 0, "°",
            "Tope de giro que el piloto puede pedir. Ponelo igual al tope físico real de tus ruedas: más que eso, el motor empuja contra el tope mecánico."));
        _scGuiado.Children.Add(FilaAjuste("Ackerman", "ackerman", 1, 200, 1, 0, "%",
            "Compensa que la rueda de adentro gira más que la de afuera. 100% = geometría ideal. Ajustá si midiendo el mismo giro a izquierda y derecha el ángulo difiere."));
        _scGuiado.Children.Add(FilaToggle("Guiar en marcha atrás", "steer_in_reverse",
            "Permite que el piloto siga guiando en reversa (maniobras de cabecera). Ojo: el GPS detecta la reversa con menos certeza."));
        _scGuiado.Children.Add(SubTituloSep("Avanzado"));
        _scGuiado.Children.Add(FilaAjusteD("Zona muerta de rumbo", "dead_zone_heading", 0.1, 0, 5, 1, "°",
            "Errores de rumbo más chicos que esto se ignoran: evita el zigzagueo fino cuando ya está arriba de la línea."));
        _scGuiado.Children.Add(FilaAjuste("Demora de zona muerta", "dead_zone_delay", 1, 50, 1, 0, "",
            "Cuántos ciclos espera antes de aplicar la zona muerta."));
        _scGuiado.Children.Add(FilaAjuste("Compensación en cabecera (U)", "u_turn_comp", 2, 20, 1, 0, "",
            "Cuánto anticipa el giro en la vuelta en U de la cabecera. 0 = neutro; positivo si la U queda abierta, negativo si muerde la pasada.",
            offDisplay: -10));
        // El wire guarda el entero crudo del slider (×0.01 al persistir): 15 son
        // 0,15°, no "15" de nada. Mostrarlo crudo hacía creer que el número era
        // grados — se muestra con la MISMA escala y unidad que el original.
        _scGuiado.Children.Add(FilaAjuste("Compensación de ladera", "side_hill_comp", 0, 30, 0.01, 2, "°",
            "Usa el rolido del IMU para compensar la deriva cuesta abajo en laderas. 0 = apagado."));

        // ---------- pantalla MÓDULO (placa + corte por volante) ----------
        // Con el motor Keya por CAN, el driver PWM (Cytron/IBT2) y la válvula
        // Danfoss no aplican — quedan fuera de la UI a pedido (2026-08-08).
        // Los valores siguen viajando intactos en el objeto de config: no se
        // tocan, no se pierden.
        _scModulo = new StackPanel { Spacing = 2, IsVisible = false };
        _scModulo.Children.Add(SubTitulo("Placa de dirección"));
        _scModulo.Children.Add(FilaSeg("Activación del piloto", "steer_enable",
            new[] { ("None", "Ninguno"), ("Switch", "Interruptor"), ("Button", "Botón") },
            "Cómo se prende el piloto desde el hardware: Interruptor físico (cerrado = ON), Botón " +
            "(un toque prende, otro apaga) o Ninguno (solo desde la pantalla)."));
        _scModulo.Children.Add(FilaSeg("Eje del IMU", "imu_axis",
            new[] { ("X", "X"), ("Y", "Y") },
            "En qué eje quedó montada la placa del IMU. Si el rolido aparece como cabeceo (o al " +
            "revés), cambiá el eje."));
        _scModulo.Children.Add(FilaToggle("Invertir relés", "invert_relays",
            "Invierte la lógica de los relés de sección (activo-alto ↔ activo-bajo). Solo si el corte de secciones anda al revés."));
        _scModulo.Children.Add(SubTituloAyuda("Corte al agarrar el volante (uno solo)",
            "Tu seguridad: el sensor que APAGA el piloto cuando agarrás el volante. Encoder cuenta pulsos de giro; presión y corriente detectan el esfuerzo contra el motor. Uno solo a la vez. Probalo SIEMPRE antes de salir al lote.", sep: true));
        _scModulo.Children.Add(FilaSensoresCorte());
        _scModulo.Children.Add(FilaAjuste("Cuentas máximas (encoder)", "max_counts", 1, 255, 1, 0, "",
            "Pulsos del encoder para cortar: menos = corta con un toque más suave del volante."));
        _scModulo.Children.Add(FilaAjuste("Límite presión/corriente", "sensor_limit", 0, 255, 100.0 / 255, 0, "%",
            "Umbral del sensor de presión o corriente para cortar, en % del rango. Menos = más sensible al toque."));

        // ---------- pantalla PANTALLA (velocidades + barra guía) ----------
        _scPantalla = new StackPanel { Spacing = 2, IsVisible = false };
        _scPantalla.Children.Add(SubTitulo("Velocidades de guiado"));
        _scPantalla.Children.Add(FilaAjusteD("Velocidad mínima", "min_steer_speed", 0.5, 0, 10, 1, "km/h",
            "Debajo de esto el piloto no engancha: evita volantazos con el tractor casi parado."));
        _scPantalla.Children.Add(FilaAjusteD("Velocidad máxima", "max_steer_speed", 1, 1, 40, 0, "km/h",
            "Arriba de esto el piloto se apaga solo, por seguridad."));
        // Techo 20 km/h, el del original (nudGuidanceSpeedLimit 0..20). El panel
        // permitía 40: es el límite que AUTORIZA el manejo libre, o sea el que
        // decide a qué velocidad se puede mover el volante sin guía. Duplicarlo
        // sin motivo aflojaba una guarda de seguridad, no un ajuste de gusto.
        _scPantalla.Children.Add(FilaAjusteD("Límite de funciones de guiado", "guidance_speed_limit", 1, 1, 20, 0, "km/h",
            "Techo general de las funciones de guiado — incluye el manejo libre de la pestaña Probar."));
        _scPantalla.Children.Add(SubTituloSep("Barra de guiado"));
        _scPantalla.Children.Add(FilaSeg("Tipo de barra", "guidance_bar",
            new[] { ("lightbar", "Lightbar"), ("steerbar", "Steer Bar") },
            "Lightbar: luces de desvío clásicas (a cuántos cm estás de la línea). Steer Bar: muestra " +
            "además el ángulo que el piloto está pidiendo."));
        _scPantalla.Children.Add(FilaToggle("Mostrar barra en pantalla", "display_lightbar",
            "Muestra u oculta la barra de guiado arriba del mapa."));
        _scPantalla.Children.Add(FilaAjuste("Grosor de línea", "line_width", 1, 8, 1, 0, "px",
            "Grosor de la línea de guiado dibujada en el mapa."));
        _scPantalla.Children.Add(FilaAjusteD("Distancia de enganche", "snap_distance", 1, 1, 100, 0, "",
            "A menos de esta distancia de la guía, el piloto la engancha de un salto."));
        _scPantalla.Children.Add(FilaAjusteD("Mirada de la barra", "guidance_look_ahead", 0.1, 0.1, 5, 1, "s",
            "Cuántos segundos adelante calcula la barra el desvío que muestra."));
        _scPantalla.Children.Add(FilaAjuste("Sensibilidad de la barra", "cm_per_pixel", 2, 20, 1, 0, "cm/px",
            "Cuántos cm de desvío representa cada pixel de la barra. Menos = barra más sensible."));

        // ---------- banner de ayuda contextual (los "?" de cada control) ----------
        _tipTitulo = new TextBlock
        {
            Text = "", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Texto,
        };
        _tipCuerpo = new TextBlock
        {
            Text = "", FontSize = 12.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var tipCont = new StackPanel();
        tipCont.Children.Add(_tipTitulo);
        tipCont.Children.Add(_tipCuerpo);
        _tip = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFF9E8")),
            BorderBrush = Ambar, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8), IsVisible = false,
            Child = tipCont,
        };
        // tocar el banner lo cierra (y también tocar de nuevo el mismo "?")
        _tip.PointerPressed += (_, _) => _tip.IsVisible = false;

        // ---------- pie: estado + Guardar ----------
        _estado = new TextBlock
        {
            Text = "", FontSize = 12.5, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _btnGuardar = new Button
        {
            Content = "Sin cambios", Height = 50, MinWidth = 132, FontSize = 13.5, FontWeight = FontWeight.Bold,
            Background = BgCard, Foreground = Texto, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = false,
        };
        _btnGuardar.Click += async (_, _) => await Guardar();

        // "Restablecer" del original (btnReset del FormSteer / de la página):
        // acá descarta lo tocado y relee lo que tiene el módulo. Dos toques a
        // propósito — un roce no puede tirar diez ajustes a la basura.
        _btnDescartar = new Button
        {
            Content = "Descartar", Height = 50, MinWidth = 104, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Background = BgCard, Foreground = TextoMuted, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), IsEnabled = false,
        };
        _btnDescartar.Click += async (_, _) =>
        {
            if (!_sucio) return;
            if (!_descartarArmado)
            {
                _descartarArmado = true;
                _btnDescartar.Content = "¿Descartar?";
                _btnDescartar.Foreground = Rojo;
                _btnDescartar.BorderBrush = Rojo;
                return;
            }
            _descartarArmado = false;
            await CargarConfig();
            _estado.Text = "Cambios descartados — se releyó la config guardada";
            _estado.Foreground = TextoMuted;
        };

        var pie = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetColumn(_estado, 0);
        Grid.SetColumn(_btnDescartar, 1);
        Grid.SetColumn(_btnGuardar, 2);
        pie.Children.Add(_estado);
        pie.Children.Add(_btnDescartar);
        pie.Children.Add(_btnGuardar);

        // ---------- árbol ----------
        var stage = new Panel();
        stage.Children.Add(_scProbar);
        stage.Children.Add(_scSensor);
        stage.Children.Add(_scFuerza);
        stage.Children.Add(_scGuiado);
        stage.Children.Add(_scModulo);
        stage.Children.Add(_scPantalla);
        // Las pantallas largas (Guiado) scrollean adentro: la card no crece
        // más allá de lo que entra en la tablet de 10".
        var stageScroll = new ScrollViewer
        {
            Content = stage, MaxHeight = 392,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(tabs);
        root.Children.Add(_tip);
        root.Children.Add(stageScroll);
        root.Children.Add(pie);
        Child = root;

        MostrarTab("probar");
    }

    // ---- ciclo de vida -------------------------------------------------------

    public void Attach(HttpClient http, string baseUrl)
    {
        _http = http;
        _base = (baseUrl ?? "").TrimEnd('/');
    }

    public async void Abrir()
    {
        IsVisible = true;
        MostrarTab("probar");
        _estado.Text = "";
        await CargarConfig();
        await FdRefrescar();
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += async (_, _) => await Latido();
        }
        _timer.Start();
    }

    public void Cerrar()
    {
        // Nunca dejar el volante bajo control manual sin nadie mirándolo.
        if (_fdOn) _ = FdPost("/api/steer/freedrive", "{\"on\":false}");

        // Cambios sin guardar: se GUARDAN al cerrar, no se tiran.
        //
        // Los dos predecesores hacían exactamente esto y el operario ya lo tiene
        // aprendido: el FormSteer nativo guardaba en FormClosing y la página
        // direccion.html auto-guardaba en `visibilitychange`. El panel, en
        // cambio, descartaba en silencio — y el ✕ no es el único camino: abrir
        // Guías o Lote también cierra este panel desde afuera (MainWindow), sin
        // que el operario siquiera piense que está saliendo de Dirección. Diez
        // toques de ajuste evaporados sin un cartel.
        //
        // Nada de diálogo modal: el panel no tiene ese patrón, y un "¿guardar?"
        // que aparece cuando el cierre lo dispara OTRO panel no tiene a quién
        // preguntarle. Se guarda y listo, en el mismo gesto que el apagado del
        // manejo libre de arriba. Para tirar los cambios está "Descartar"
        // (doble toque anti-roce), que es donde el gesto destructivo tiene que
        // ser explícito.
        if (_sucio && _cfg != null && _http != null) _ = Guardar();

        _timer?.Stop();
        IsVisible = false;
        Cerrado?.Invoke();
    }

    // ---- HTTP ------------------------------------------------------------------

    private async Task CargarConfig()
    {
        try
        {
            var json = await _http!.GetStringAsync(_base + "/api/steer/config").ConfigureAwait(false);
            var node = JsonNode.Parse(json.TrimStart('﻿')) as JsonObject;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cfg = node;
                _sucio = false;
                PintarConfig();
                PintarGuardar();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _estado.Text = "Sin módulo de dirección: " + ex.Message);
        }
    }

    private async Task Guardar()
    {
        if (_cfg == null || _http == null) return;
        try
        {
            // SIEMPRE el objeto entero: el POST /steer/config NO es merge — un
            // body parcial pone en default todo lo ausente (visto 2026-08-08).
            var copia = (JsonObject)_cfg.DeepClone();
            copia.Remove("ok");
            var body = new StringContent(copia.ToJsonString(), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(_base + "/api/steer/config", body).ConfigureAwait(false);
            bool ok = resp.IsSuccessStatusCode;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _sucio = !ok;
                _estado.Text = ok ? "Guardado y enviado al módulo ✔" : "No se pudo guardar (HTTP " + (int)resp.StatusCode + ")";
                _estado.Foreground = ok ? Verde : Rojo;
                PintarGuardar();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _estado.Text = "Sin conexión: " + ex.Message;
                _estado.Foreground = Rojo;
            });
        }
    }

    // ┌──────────────────────────────────────────────────────────────────────┐
    // │ TRAMPA CONOCIDA — no reintroducir (auditoría de paridad 2026-08-15). │
    // └──────────────────────────────────────────────────────────────────────┘
    // El cero NO se calcula acá: lo hace el motor en SteerConfigService.ZeroWas(),
    // con la fórmula del FormSteer original
    //     was_offset += setAS_countsPerDegree × (−ángulo actual)
    // y ese counts_per_degree es el PERSISTIDO en los settings, no el que el
    // operario tiene en pantalla. La secuencia natural de calibración es
    // justamente "cambio las cuentas por grado → pongo en cero": si se ceraba
    // con la escala vieja, el cero salía corrido, y un cero corrido en el lote
    // es sembrar corrido toda la jornada. Encima el CargarConfig() del final
    // releía la config y le borraba la edición sin decir nada, así que el
    // operario ni se enteraba de que su número no se había aplicado.
    //
    // Arreglo (el más simple que es correcto): con cambios pendientes se GUARDA
    // primero — así el motor cera con exactamente lo que el operario está
    // viendo — y recién ahí se cera. Si el guardado falla, no se cera nada: es
    // preferible no cerar a cerar con la escala equivocada. Y el CargarConfig()
    // del final ya no puede pisar nada, porque a esa altura no quedan ediciones.
    //
    // Si algún día el cero se calcula del lado de la UI o el endpoint acepta el
    // counts_per_degree en el body, este guardado previo se puede sacar; hasta
    // entonces, sacarlo reintroduce el bug.
    private async Task ZeroWas()
    {
        if (_http == null) return;

        if (_sucio)
        {
            await Guardar();
            if (_sucio)
            {
                // Guardar() ya dejó el motivo del fallo en _estado; se agrega la
                // consecuencia, que es lo que le importa al que está calibrando.
                _estado.Text = "NO se puso en cero: primero hay que guardar los cambios";
                _estado.Foreground = Rojo;
                return;
            }
        }

        try
        {
            var resp = await _http.PostAsync(_base + "/api/steer/zero-was",
                new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
            // El endpoint contesta 200 incluso cuando NO ceró (ok=false +
            // motivo): mirar solo el código HTTP daba "Sensor en cero ✔" con el
            // sensor sin cerar.
            var cuerpo = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonObject? j = null;
            try { j = JsonNode.Parse(cuerpo.TrimStart('﻿')) as JsonObject; } catch { }
            bool ok = resp.IsSuccessStatusCode && (j?["ok"]?.GetValue<bool>() ?? false);
            string? motivo = j?["error"]?.GetValue<string>();

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _estado.Text = ok ? "Sensor en cero ✔" : motivo switch
                {
                    // "Excessive Steer Angle" del FormSteer: más de ±3900 cuentas
                    // de corrimiento no es un cero, es un sensor mal montado.
                    "fuera-de-rango"      => "No se puso en cero: el ángulo es excesivo, revisá el montaje del sensor",
                    "service-unavailable" => "Sin módulo de dirección conectado",
                    _                     => "No se pudo poner en cero (AGP-SYS-009)",
                };
                _estado.Foreground = ok ? Verde : Rojo;
                // el cero cambia was_offset en el módulo: releer para no pisarlo
                // (seguro: arriba nos aseguramos de que no queden ediciones)
                await CargarConfig();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _estado.Text = "Sin conexión: " + ex.Message;
                _estado.Foreground = Rojo;
            });
        }
    }

    private async Task FdPost(string path, string json)
    {
        if (_http == null) return;
        try
        {
            var resp = await _http.PostAsync(_base + path,
                new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var j = JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
            await Dispatcher.UIThread.InvokeAsync(() => FdRender(j));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _fdNota.Text = "Sin conexión con PilotX: " + ex.Message;
                _fdNota.Foreground = Rojo;
            });
        }
    }

    private async Task FdRefrescar()
    {
        if (_http == null) return;
        try
        {
            var body = await _http.GetStringAsync(_base + "/api/steer/freedrive").ConfigureAwait(false);
            var j = JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
            await Dispatcher.UIThread.InvokeAsync(() => FdRender(j));
        }
        catch { }
    }

    private async Task Latido()
    {
        if (!IsVisible || _http == null) return;
        // Obj/Act/Err del header, siempre (misma fuente que el gráfico).
        // SIN ConfigureAwait(false): el tick del DispatcherTimer corre en el
        // hilo de UI y la continuación tiene que VOLVER ahí — tocar TextBlocks
        // desde el pool mata el proceso entero (crash 2026-08-08, mismo caso
        // que el RequestNextFrameRendering del mapa).
        try
        {
            var body = await _http.GetStringAsync(_base + "/api/aog/graph-steer");
            var j = JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
            double set = j?["set_steer_deg"]?.GetValue<double>() ?? double.NaN;
            double act = j?["actual_steer_deg"]?.GetValue<double>() ?? double.NaN;
            _liveObj.Text = double.IsNaN(set) ? "—" : set.ToString("F1", CultureInfo.InvariantCulture) + "°";
            _liveAct.Text = double.IsNaN(act) ? "—" : act.ToString("F1", CultureInfo.InvariantCulture) + "°";
            _liveErr.Text = (double.IsNaN(set) || double.IsNaN(act)) ? "—"
                : (set - act).ToString("F1", CultureInfo.InvariantCulture) + "°";
            ActualizarVuWas(act);
        }
        catch { _liveObj.Text = _liveAct.Text = _liveErr.Text = "—"; }

        // Estado del manejo libre mientras está prendido (ve el apagado solo).
        if (_fdOn) await FdRefrescar();

    }

    // ---- render ------------------------------------------------------------------

    /// <summary>Vúmetro del ángulo en vivo (pestaña Sensor): la barra se llena
    /// desde el centro hacia el lado del giro. Escala = ángulo máximo config.</summary>
    private void ActualizarVuWas(double act)
    {
        if (_wasAng == null) return;
        bool ok = !double.IsNaN(act);
        _wasAng.Text = ok ? act.ToString("F1", CultureInfo.InvariantCulture) + "°" : "—";

        double max = Entero("max_steer_angle");
        if (max < 5) max = 40;
        double mitadIzqW = (_wasFillIzq.Parent as Border)?.Bounds.Width ?? 0;
        double mitadDerW = (_wasFillDer.Parent as Border)?.Bounds.Width ?? 0;
        double pct = ok ? Math.Min(1.0, Math.Abs(act) / max) : 0;
        _wasFillIzq.Width = (ok && act < -0.2) ? pct * mitadIzqW : 0;
        _wasFillDer.Width = (ok && act > 0.2) ? pct * mitadDerW : 0;
    }

    private void FdRender(JsonObject? j)
    {
        bool on = j?["on"]?.GetValue<bool>() ?? false;
        double ang = j?["angle"]?.GetValue<double>() ?? 0;
        string? error = j?["error"]?.GetValue<string>();

        bool seApagoSolo = _fdOn && !on && error == null;
        _fdOn = on;
        _fdPower.Content = on ? "Prendido" : "Prender";
        _fdPower.BorderBrush = on ? Verde : Borde;
        _fdPower.Background = on ? BgSel : BgCard;
        _fdIzq.IsEnabled = _fdDer.IsEnabled = _fdCero.IsEnabled = on;
        _fdAngulo.Text = (ang > 0 ? "+" : "") + ang.ToString("F0", CultureInfo.InvariantCulture) + "°";

        string? motivo = error switch
        {
            "velocidad"       => "No se puede prender: el tractor está andando.",
            "sin-velocidad"   => "No se puede prender: PilotX no informa velocidad.",
            "apagado"         => "Prendé el manejo libre antes de mover el ángulo.",
            "service-unavailable" => "Sin módulo de dirección conectado.",
            null              => seApagoSolo ? "Se apagó solo: el tractor superó el límite de velocidad." : null,
            _                 => "No se pudo completar la acción (AGP-SYS-009).",
        };
        _fdNota.Text = motivo ?? "Con el tractor parado. Se apaga solo si el tractor arranca.";
        _fdNota.Foreground = motivo != null ? Rojo : TextoMuted;
    }

    private void PintarConfig()
    {
        if (_cfg == null) return;
        SetSeg(rty: !string.Equals(Str("conv_type"), "Differential", StringComparison.OrdinalIgnoreCase), pintar: true);
        _valCuentas.Text = Entero("counts_per_degree").ToString(CultureInfo.InvariantCulture);
        PintarToggle(_tglInvWas, Bool("invert_was"));
        PintarToggle(_tglInvMotor, Bool("invert_steer"));
        _valPwmMin.Text  = Entero("min_pwm").ToString(CultureInfo.InvariantCulture);
        _valPwmAlto.Text = Entero("high_steer_pwm").ToString(CultureInfo.InvariantCulture);
        _valGanP.Text    = Entero("proportional_gain").ToString(CultureInfo.InvariantCulture);
        foreach (var r in _refrescos) r();
    }

    private void PintarGuardar()
    {
        _btnGuardar.IsEnabled = _sucio;
        _btnGuardar.Content = _sucio ? "Guardar" : "Sin cambios";
        _btnGuardar.Background = _sucio ? Verde : BgCard;
        _btnGuardar.Foreground = _sucio ? Brushes.White : Texto;
        _btnGuardar.BorderBrush = _sucio ? Verde : Borde;

        // Descartar acompaña: solo tiene sentido con cambios sin guardar, y
        // cualquier repintado lo desarma (un toque viejo no queda "cargado").
        _descartarArmado = false;
        _btnDescartar.IsEnabled = _sucio;
        _btnDescartar.Content = "Descartar";
        _btnDescartar.Foreground = _sucio ? Texto : TextoMuted;
        _btnDescartar.BorderBrush = Borde;
    }

    private void MostrarTab(string id)
    {
        _tip.IsVisible = false;
        _scProbar.IsVisible = id == "probar";
        _scSensor.IsVisible = id == "sensor";
        _scFuerza.IsVisible = id == "fuerza";
        _scGuiado.IsVisible = id == "guiado";
        _scModulo.IsVisible = id == "modulo";
        _scPantalla.IsVisible = id == "pantalla";
        foreach (var (k, b) in _tabs)
        {
            bool sel = k == id;
            b.Background = sel ? BgSel : BgCard;
            b.Foreground = sel ? Texto : TextoMuted;
            b.BorderBrush = sel ? Verde : Borde;
        }
        _tabActual = id;
    }

    private string _tabActual = "probar";


    // ---- edición de config ---------------------------------------------------

    private void SetSeg(bool rty, bool pintar = false)
    {
        if (!pintar && _cfg != null) _cfg["conv_type"] = rty ? "Single" : "Differential";
        PintarToggle(_segRty, rty);
        PintarToggle(_segEncoder, !rty);
    }

    private void ToggleBool(string clave, Button tgl)
    {
        if (_cfg == null) return;
        bool nuevo = !Bool(clave);
        _cfg[clave] = nuevo;
        PintarToggle(tgl, nuevo);
        MarcarSucio();
    }

    private void Nudge(string clave, int paso, int min, int max)
    {
        if (_cfg == null) return;
        int v = Entero(clave) + paso;
        if (v < min) v = min;
        if (v > max) v = max;
        _cfg[clave] = v;
        PintarConfig();
        MarcarSucio();
    }

    private void MarcarSucio()
    {
        _sucio = true;
        _estado.Text = "";
        _estado.Foreground = TextoMuted;
        PintarGuardar();
    }

    // ---- helpers de lectura ----------------------------------------------------

    private string Str(string k)  => _cfg?[k]?.GetValue<string>() ?? "";
    private bool Bool(string k)   => _cfg?[k]?.GetValue<bool>() ?? false;
    private int Entero(string k)
    {
        var n = _cfg?[k];
        if (n == null) return 0;
        try { return n.GetValue<int>(); }
        catch { try { return (int)Math.Round(n.GetValue<double>()); } catch { return 0; } }
    }

    // ---- fábrica de controles ----------------------------------------------------

    private static TextBlock Num(string t) => new()
    {
        Text = t, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Texto,
        MinWidth = 56, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Control LiveCelda(string cap, TextBlock val)
    {
        var sp = new StackPanel { Spacing = 0 };
        sp.Children.Add(new TextBlock
        {
            Text = cap, FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = TextoMuted, TextAlignment = TextAlignment.Center,
        });
        val.FontSize = 17;
        sp.Children.Add(val);
        return sp;
    }

    private static Button BotonChico(string txt) => new()
    {
        Content = txt, Height = 44, MinWidth = 64, FontSize = 13, FontWeight = FontWeight.SemiBold,
        Background = BgCard, Foreground = TextoMuted, BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static Button BotonFd(string txt) => new()
    {
        Content = txt, Width = 54, Height = 54, FontSize = 14.5, FontWeight = FontWeight.Bold,
        Background = BgCard, Foreground = Texto, BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = false,
    };

    private static Button BotonSeg(string txt) => new()
    {
        Content = txt, Height = 46, FontSize = 12.5, FontWeight = FontWeight.SemiBold,
        Background = BgCard, Foreground = TextoMuted, BorderBrush = Borde, BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static void PintarToggle(Button b, bool on)
    {
        b.Background = on ? BgSel : BgCard;
        b.Foreground = on ? Texto : TextoMuted;
        b.BorderBrush = on ? Verde : Borde;
    }

    private static Control Envolver(Control c, double izq, double der)
    {
        return new Border { Child = c, Padding = new Thickness(izq, 0, der, 0), Background = Brushes.Transparent };
    }

    private Control FilaNumerica(string etiqueta, TextBlock valor, Action menos, Action mas,
                                 string ayuda = null)
    {
        var fila = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(0, 3, 0, 0),
        };
        var lbl = new TextBlock
        {
            Text = etiqueta, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        Control cabecera = lbl;
        if (ayuda != null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(lbl, 0);
            var chip = ChipAyuda(etiqueta, ayuda);
            Grid.SetColumn(chip, 1);
            g.Children.Add(lbl);
            g.Children.Add(chip);
            cabecera = g;
        }
        Grid.SetColumn(cabecera, 0);

        var bMenos = BotonChico("−");
        bMenos.Width = 48; bMenos.Height = 48; bMenos.FontSize = 21; bMenos.Foreground = Texto;
        bMenos.Click += (_, _) => menos();
        Grid.SetColumn(bMenos, 1);

        valor.MinWidth = 60;
        Grid.SetColumn(valor, 2);

        var bMas = BotonChico("+");
        bMas.Width = 48; bMas.Height = 48; bMas.FontSize = 21; bMas.Foreground = Texto;
        bMas.Click += (_, _) => mas();
        Grid.SetColumn(bMas, 3);

        fila.Children.Add(cabecera);
        fila.Children.Add(bMenos);
        fila.Children.Add(valor);
        fila.Children.Add(bMas);
        return fila;
    }

    private TextBlock SubTitulo(string t) => new()
    {
        Text = t, FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = TextoMuted,
        Margin = new Thickness(2, 0, 0, 4),
    };

    private TextBlock SubTituloSep(string t)
    {
        var tb = SubTitulo(t);
        tb.Margin = new Thickness(2, 10, 0, 4);
        return tb;
    }

    // ---- ayuda contextual ("?" al lado de cada control) -----------------------

    private void MostrarAyuda(string titulo, string texto)
    {
        // tocar el mismo "?" con el tip abierto lo cierra
        if (_tip.IsVisible && _tipTitulo.Text == titulo) { _tip.IsVisible = false; return; }
        _tipTitulo.Text = titulo;
        _tipCuerpo.Text = texto;
        _tip.IsVisible = true;
    }

    private Button ChipAyuda(string titulo, string texto)
    {
        var b = new Button
        {
            Content = "?", Width = 34, Height = 34, FontSize = 14, FontWeight = FontWeight.Bold,
            Background = BgCard, Foreground = TextoMuted,
            BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17), Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => MostrarAyuda(titulo, texto);
        return b;
    }

    /// <summary>Subtitulo de seccion con su "?" al lado.</summary>
    private Control SubTituloAyuda(string t, string ayuda, bool sep = false)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var tb = sep ? SubTituloSep(t) : SubTitulo(t);
        tb.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tb, 0);
        var chip = ChipAyuda(t, ayuda);
        Grid.SetColumn(chip, 1);
        g.Children.Add(tb);
        g.Children.Add(chip);
        return g;
    }

    // ---- helpers genéricos de filas (registran su repintado en _refrescos) ----

    /// <summary>Fila de ajuste sobre un ENTERO crudo del wire, con escala solo
    /// de display (igual que los sliders del FormSteer: 29 → "2,9 s").
    /// offDisplay corre el número mostrado sin tocar el wire (u_turn_comp
    /// guarda 2..20 pero se muestra −8..+10, el "minus10" del original).</summary>
    private Control FilaAjuste(string etiqueta, string clave, int min, int max,
                               double escala = 1, int dec = 0, string unidad = "",
                               string ayuda = null, double offDisplay = 0)
    {
        var val = Num("—");
        Action refrescar = () =>
        {
            double v = (Entero(clave) + offDisplay) * escala;
            val.Text = v.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                     + (unidad.Length > 0 ? " " + unidad : "");
        };
        _refrescos.Add(refrescar);
        return FilaNumerica(etiqueta, val,
            () => { NudgeCrudo(clave, -1, min, max); refrescar(); MarcarSucio(); },
            () => { NudgeCrudo(clave, +1, min, max); refrescar(); MarcarSucio(); },
            ayuda);
    }

    /// <summary>Fila de ajuste sobre un DOUBLE real del wire (km/h, segundos).</summary>
    private Control FilaAjusteD(string etiqueta, string clave, double paso,
                                double min, double max, int dec, string unidad = "",
                                string ayuda = null)
    {
        var val = Num("—");
        Action refrescar = () =>
        {
            val.Text = Doble(clave).ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                     + (unidad.Length > 0 ? " " + unidad : "");
        };
        _refrescos.Add(refrescar);
        Action<double> mover = d =>
        {
            if (_cfg == null) return;
            double v = Math.Round(Doble(clave) + d, 2);
            if (v < min) v = min;
            if (v > max) v = max;
            _cfg[clave] = v;
            refrescar();
            MarcarSucio();
        };
        return FilaNumerica(etiqueta, val, () => mover(-paso), () => mover(+paso), ayuda);
    }

    /// <summary>Toggle de un bool del wire, ancho completo.</summary>
    private Control FilaToggle(string etiqueta, string clave, string ayuda = null)
    {
        var b = BotonSeg(etiqueta);
        b.Margin = new Thickness(0, 6, 0, 0);
        Action refrescar = () => PintarToggle(b, Bool(clave));
        _refrescos.Add(refrescar);
        b.Click += (_, _) =>
        {
            if (_cfg == null) return;
            _cfg[clave] = !Bool(clave);
            refrescar();
            MarcarSucio();
        };
        if (ayuda == null) return b;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        b.Margin = new Thickness(0);
        Grid.SetColumn(b, 0);
        var chip = ChipAyuda(etiqueta, ayuda);
        Grid.SetColumn(chip, 1);
        g.Children.Add(b);
        g.Children.Add(chip);
        return g;
    }

    /// <summary>Segmentado de un string del wire (valor exacto que espera el módulo).</summary>
    private Control FilaSeg(string etiqueta, string clave, (string Valor, string Texto)[] opciones,
                            string ayuda = null)
    {
        var cont = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        var lbl = new TextBlock
        {
            Text = etiqueta, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Texto,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (ayuda != null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(lbl, 0);
            var chip = ChipAyuda(etiqueta, ayuda);
            Grid.SetColumn(chip, 1);
            g.Children.Add(lbl);
            g.Children.Add(chip);
            cont.Children.Add(g);
        }
        else cont.Children.Add(lbl);
        var fila = new UniformGrid { Columns = opciones.Length };
        var botones = new List<(string Valor, Button Btn)>();
        foreach (var (valor, texto) in opciones)
        {
            var b = BotonSeg(texto);
            string mio = valor;
            b.Click += (_, _) =>
            {
                if (_cfg == null) return;
                _cfg[clave] = mio;
                foreach (var (v2, b2) in botones) PintarToggle(b2, v2 == mio);
                MarcarSucio();
            };
            botones.Add((valor, b));
            fila.Children.Add(Envolver(b, 0, 4));
        }
        _refrescos.Add(() =>
        {
            var actual = Str(clave);
            foreach (var (v2, b2) in botones)
                PintarToggle(b2, string.Equals(v2, actual, StringComparison.OrdinalIgnoreCase));
        });
        cont.Children.Add(fila);
        return cont;
    }

    /// <summary>Los 3 sensores de corte al agarrar el volante son EXCLUYENTES
    /// (misma regla que la página y el FormSteer): prender uno apaga los otros.</summary>
    private Control FilaSensoresCorte()
    {
        var claves = new[] { ("encoder", "Encoder"), ("pressure_sensor", "Presión"), ("current_sensor", "Corriente") };
        var fila = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
        var botones = new List<(string Clave, Button Btn)>();
        foreach (var (clave, texto) in claves)
        {
            var b = BotonSeg(texto);
            string mia = clave;
            b.Click += (_, _) =>
            {
                if (_cfg == null) return;
                bool nuevo = !Bool(mia);
                _cfg[mia] = nuevo;
                if (nuevo)
                    foreach (var (c2, _) in botones)
                        if (c2 != mia) _cfg[c2] = false;
                foreach (var (c2, b2) in botones) PintarToggle(b2, Bool(c2));
                MarcarSucio();
            };
            botones.Add((clave, b));
            fila.Children.Add(Envolver(b, 0, 4));
        }
        _refrescos.Add(() => { foreach (var (c2, b2) in botones) PintarToggle(b2, Bool(c2)); });
        return fila;
    }

    private void NudgeCrudo(string clave, int paso, int min, int max)
    {
        if (_cfg == null) return;
        int v = Entero(clave) + paso;
        if (v < min) v = min;
        if (v > max) v = max;
        _cfg[clave] = v;
    }

    private double Doble(string k)
    {
        var n = _cfg?[k];
        if (n == null) return 0;
        try { return n.GetValue<double>(); }
        catch { try { return n.GetValue<int>(); } catch { return 0; } }
    }
}
