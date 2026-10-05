// GraficoQuantiXPanel.axaml.cs
//
// El gráfico para calibrar el PID de los motores de QuantiX. Contesta la
// pregunta que se hace en el lote, y una sola:
//
//   · velocidad del motor planchada + rpm reales oscilando alrededor del
//     target  →  el PID necesita corrección (bajar Ki, subir Kp, etc.)
//   · carga clavada en 100%          →  el motor NO puede seguir al target y
//     no hay ganancia que lo arregle: es techo mecánico (cadena tensa,
//     rodamiento, producto pesado)
//
// La serie de velocidad es la del MOTOR, no la del tractor. En curva no son la
// misma (MotorSpeedKmh en QuantiXMotorBridge): la sección externa va más rápido
// que la interna, así que el target del motor CAMBIA aunque el GPS marque
// velocidad pareja. Mirando solo la del GPS se le echaría la culpa al PID de
// algo que hizo bien.
//
// Datos: GET /api/quantix/graph-pid cada 200 ms, del buffer en memoria del
// QxPidRecorder — el mismo dato que se está escribiendo al CSV, sin releer el
// disco. Ventana de 300 muestras = 60 s a 5 Hz.
//
// API: Attach(GraficosClient) arranca el muestreo; Detach() lo corta.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public partial class GraficoQuantiXPanel : UserControl, IPanelEmbebible
{
    private const int PollMs = 200;

    /// <summary>300 muestras a 5 Hz = 60 s. Los otros cuatro gráficos usan 120
    /// (24 s), que no alcanza para ver un ciclo de oscilación lento.</summary>
    private const int Ventana = 300;

    /// <summary>Factor con el que la velocidad entra al grafico. Ver el comentario
    /// en Aplicar(): en km/h crudo la curva queda contra el piso.</summary>
    private const double VelFactor = 10.0;

    private const string TxtSinDatos    = "—";
    private const string TxtEnVivo      = "en vivo";
    private const string TxtSinConexion = "sin conexión";
    private const string TxtSinRegistro = "sin registro";

    private static string T(string t) => Traductor.T(t);

    private GraficosClient? _client;
    private CancellationTokenSource? _cts;

    private GraficoLineal? _lienzo;
    private SerieGrafico? _serRpmReal;
    private SerieGrafico? _serRpmTarget;
    private SerieGrafico? _serVel;

    private bool _lastOk;
    private bool _idiomaEnganchado;
    private string _estado = TxtSinDatos;

    // Motor elegido en el selector. Vacío = todavía no se eligió ninguno y se
    // toma el primero que aparezca.
    private string _uid = "";
    private int _motorIdx = -1;
    private bool _poblandoSelector;

    /// <summary>Ultimo diagnostico del motor que se esta mirando, para que el
    /// boton Aplicar sepa que ganancia escribir.</summary>
    private PidDiagnosticoDto? _diagActual;

    /// <summary>Cada cuantos polls se recalcula el diagnostico. El grafico va a
    /// 5 Hz; recalcular el analisis de la corrida entera a esa frecuencia es
    /// tirar CPU al pedo — con una vez por segundo alcanza y sobra.</summary>
    private int _pollsDesdeDiag;

    /// <summary>Lo invoca el ✕ del header.</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Aviso corto para el shell (el toast de MainWindow).</summary>
    public Action<string>? Aviso { get; set; }

    public GraficoQuantiXPanel()
    {
        InitializeComponent();
        Traductor.Aplicar(this);

        ArmarLienzo();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ---------- ciclo de vida ------------------------------------------------

    public void Attach(GraficosClient client)
    {
        _client = client;
        ResolverColores();
        EngancharIdioma();
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = BucleAsync(_cts.Token);
    }

    public void Detach()
    {
        SoltarIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private void OnIdiomaCambio() => Dispatcher.UIThread.Post(PintarEstado);

    private void EngancharIdioma()
    {
        if (_idiomaEnganchado) return;
        Traductor.IdiomaCambio += OnIdiomaCambio;
        _idiomaEnganchado = true;
    }

    private void SoltarIdioma()
    {
        if (!_idiomaEnganchado) return;
        Traductor.IdiomaCambio -= OnIdiomaCambio;
        _idiomaEnganchado = false;
    }

    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        var raiz = this.FindControl<Grid>("ContenidoRaiz");
        if (raiz != null) raiz.Margin = new Thickness(0);
    }

    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<Border>("HeaderPill"));

    private void OnCerrarClick(object? s, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    // ---------- lienzo -------------------------------------------------------

    private void ArmarLienzo()
    {
        _lienzo = this.FindControl<GraficoLineal>("Lienzo");
        if (_lienzo == null) return;

        // RangoAuto y no CentradoEnCero: las rpm son valores absolutos, no un
        // error alrededor de cero como el XTE o el rumbo.
        _lienzo.Modo = ModoEjeGrafico.RangoAuto;
        _lienzo.MaxPuntos = Ventana;
        _lienzo.FraccionesGrilla = new[] { 0.25, 0.5, 0.75 };
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));

        _serRpmReal   = _lienzo.NuevaSerie(RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E")), Ventana);
        _serRpmTarget = _lienzo.NuevaSerie(Color.Parse("#535E54"), Ventana);
        _serVel       = _lienzo.NuevaSerie(Color.Parse("#3D87C6"), Ventana);
    }

    private void ResolverColores()
    {
        if (_lienzo == null) return;
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));
        if (_serRpmReal != null)
            _serRpmReal.Color = RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E"));
        _lienzo.Redibujar();
    }

    // ---------- poll ---------------------------------------------------------

    private async Task BucleAsync(CancellationToken ct)
    {
        await PollAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await PollAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (_client == null) return;
        PidGraphRespDto? d;
        try
        {
            d = await _client.GetPidAsync(_uid, Math.Max(0, _motorIdx), ct).ConfigureAwait(false);
        }
        // Sin este `when`, el timeout del HttpClient mata el bucle entero y el
        // panel se queda congelado hasta que lo cierren y lo abran de nuevo.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { d = null; }

        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Aplicar(d));

        if (++_pollsDesdeDiag < 5) return;
        _pollsDesdeDiag = 0;

        PidDiagnosticoRespDto? diag;
        try { diag = await _client.GetPidDiagnosticoAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { return; }

        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => PintarDiagnostico(diag));
    }

    // ---------- diagnostico --------------------------------------------------

    private void PintarDiagnostico(PidDiagnosticoRespDto? r)
    {
        var tarjeta = this.FindControl<Border>("TarjetaDiag");
        var btn = this.FindControl<Button>("BtnAplicar");
        if (tarjeta == null) return;

        _diagActual = null;
        if (r == null || !r.Ok || r.Motores == null || string.IsNullOrEmpty(_uid))
        {
            tarjeta.IsVisible = false;
            return;
        }

        foreach (var d in r.Motores)
        {
            if (!string.Equals(d.Uid, _uid, StringComparison.OrdinalIgnoreCase)) continue;
            if (d.M != _motorIdx) continue;
            _diagActual = d;
            break;
        }

        if (_diagActual == null) { tarjeta.IsVisible = false; return; }

        tarjeta.IsVisible = true;

        var txtV = this.FindControl<TextBlock>("TxtVeredicto");
        var pill = this.FindControl<Border>("PillVeredicto");
        var txtM = this.FindControl<TextBlock>("TxtMotorDiag");
        var txtE = this.FindControl<TextBlock>("TxtExplicacion");

        if (txtV != null) txtV.Text = T(EtiquetaVeredicto(_diagActual.Veredicto));
        if (pill != null) pill.Background = PincelVeredicto(_diagActual.Veredicto);
        // Cuantos segundos de corrida respaldan el diagnostico: sin eso, "anda
        // bien" sobre 3 segundos de datos se lee igual que sobre dos minutos.
        if (txtM != null)
            txtM.Text = (_diagActual.Nombre ?? "") + "  ·  " +
                (_diagActual.Muestras / 5.0).ToString("0", CultureInfo.InvariantCulture) + " s de corrida";
        if (txtE != null) txtE.Text = _diagActual.Explicacion ?? "";

        if (btn != null)
        {
            btn.IsVisible = _diagActual.HayRecomendacion;
            if (_diagActual.HayRecomendacion)
                btn.Content = (_diagActual.Parametro ?? "").ToUpperInvariant() + "  " +
                    _diagActual.ValorActual.ToString("0.#", CultureInfo.InvariantCulture) + " → " +
                    _diagActual.ValorSugerido.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>El veredicto en criollo, para la pill.</summary>
    private static string EtiquetaVeredicto(string? v)
    {
        switch ((v ?? "").ToLowerInvariant())
        {
            case "anda":               return "Anda bien";
            case "oscila":             return "Oscila";
            case "errorconstante":     return "Queda corto";
            case "lento":              return "Le falta empuje";
            case "saturado":           return "Al maximo";
            case "velocidaddespareja": return "Velocidad despareja";
            default:                   return "Sin datos";
        }
    }

    /// <summary>Verde lo que anda, rojo lo mecanico (que no se arregla tocando
    /// ganancias), gris lo que no se puede juzgar.</summary>
    private IBrush PincelVeredicto(string? v)
    {
        switch ((v ?? "").ToLowerInvariant())
        {
            case "anda":     return new SolidColorBrush(Color.Parse("#DCF0D8"));
            case "saturado": return new SolidColorBrush(Color.Parse("#F6D9D5"));
            case "oscila":
            case "errorconstante":
            case "lento":    return new SolidColorBrush(Color.Parse("#FBEFD6"));
            default:         return (IBrush?)RecursoPincel("PilotXPanelSurface2") ?? Brushes.LightGray;
        }
    }

    private async void OnAplicarClick(object? s, RoutedEventArgs e)
    {
        var d = _diagActual;
        if (d == null || _client == null || !d.HayRecomendacion) return;

        var (ok, motivo) = await _client.AplicarPidAsync(d.Uid ?? "", d.M,
            d.Parametro ?? "", d.ValorSugerido).ConfigureAwait(true);

        if (ok)
        {
            Aviso?.Invoke(T("Listo: ") + (d.Parametro ?? "").ToUpperInvariant() + " " +
                d.ValorActual.ToString("0.#", CultureInfo.InvariantCulture) + " → " +
                d.ValorSugerido.ToString("0.#", CultureInfo.InvariantCulture) +
                T(". Tira otra pasada para ver como quedo."));
            _pollsDesdeDiag = 99;   // refrescar el diagnostico en el proximo poll
            return;
        }

        Aviso?.Invoke(motivo == "motor_girando"
            ? T("Pará el motor antes de cambiarle las ganancias")
            : T("No se pudo aplicar el cambio"));
    }

    private void Aplicar(PidGraphRespDto? d)
    {
        if (d == null)
        {
            if (_lastOk) { _estado = TxtSinConexion; PintarEstado(); _lastOk = false; }
            return;
        }

        if (!d.Ok)
        {
            // El bridge no arrancó (sin nodos configurados): no es un error de
            // red, así que se dice otra cosa.
            _estado = TxtSinRegistro;
            PintarEstado();
            _lastOk = false;
            return;
        }

        if (!_lastOk) { _estado = TxtEnVivo; PintarEstado(); _lastOk = true; }

        PoblarSelector(d.Motores);

        var ms = d.Muestras;
        var vacio = this.FindControl<TextBlock>("Vacio");
        if (ms == null || ms.Count == 0)
        {
            if (vacio != null) vacio.IsVisible = true;
            return;
        }
        if (vacio != null) vacio.IsVisible = false;

        // El buffer del servidor ya es la ventana entera: se repinta completo en
        // vez de empujar de a una, así el gráfico queda igual aunque se cambie
        // de motor en el selector.
        _serRpmReal?.Limpiar();
        _serRpmTarget?.Limpiar();
        _serVel?.Limpiar();

        double ultRpm = double.NaN, ultTarget = 0, ultCarga = 0;
        for (int i = 0; i < ms.Count; i++)
        {
            double real   = ms[i].RpmReal ?? double.NaN;
            double target = ms[i].RpmTarget ?? 0;
            double vel    = ms[i].VelMotor ?? 0;

            // Sin dato del nodo se repite la última rpm conocida en vez de
            // hundir la traza a cero: cero significa "motor quieto".
            _serRpmReal?.Empujar(double.IsNaN(real) ? (double.IsNaN(ultRpm) ? 0 : ultRpm) : real);
            _serRpmTarget?.Empujar(target);
            // La velocidad va x10: en km/h son ~6 y las rpm ~45, asi que en el
            // mismo eje la curva de velocidad quedaria planchada contra el piso
            // y no se veria si esta estable o no — que es justo lo que hay que
            // mirar. El factor esta declarado en la leyenda.
            _serVel?.Empujar(vel * VelFactor);

            if (!double.IsNaN(real)) ultRpm = real;
            ultTarget = target;
            ultCarga  = ms[i].LoadPct ?? 0;
        }

        PintarLecturas(ultRpm, ultTarget, ultCarga);
        EncuadrarEje();
        _lienzo?.Redibujar();
    }

    // ---------- selector -----------------------------------------------------

    private void PoblarSelector(List<PidMotorDto>? motores)
    {
        var sel = this.FindControl<ComboBox>("SelectorMotor");
        if (sel == null || motores == null) return;

        // Solo se repuebla cuando cambió la lista: si no, el ComboBox se
        // resetea 5 veces por segundo y no se puede ni elegir.
        if (sel.ItemCount == motores.Count) return;

        _poblandoSelector = true;
        try
        {
            var items = new List<MotorItem>();
            for (int i = 0; i < motores.Count; i++)
            {
                var m = motores[i];
                string nom = string.IsNullOrWhiteSpace(m.Nombre)
                    ? "Motor " + (m.M + 1).ToString(CultureInfo.InvariantCulture)
                    : m.Nombre!;
                // El UID va en la etiqueta porque el índice de motor se repite
                // entre nodos: "M0" existe una vez por nodo.
                items.Add(new MotorItem
                {
                    Uid = m.Uid ?? "",
                    M = m.M,
                    Etiqueta = nom + "  ·  " + Corto(m.Uid) + " m" + m.M.ToString(CultureInfo.InvariantCulture)
                });
            }
            sel.ItemsSource = items;

            if (_motorIdx < 0 && items.Count > 0)
            {
                _uid = items[0].Uid;
                _motorIdx = items[0].M;
                sel.SelectedIndex = 0;
            }
        }
        finally { _poblandoSelector = false; }
    }

    private static string Corto(string? uid)
        => string.IsNullOrEmpty(uid) ? "?" : (uid!.Length <= 6 ? uid : uid.Substring(uid.Length - 6));

    private void OnMotorCambiado(object? s, SelectionChangedEventArgs e)
    {
        if (_poblandoSelector) return;
        var sel = s as ComboBox;
        if (sel?.SelectedItem is not MotorItem it) return;
        _uid = it.Uid;
        _motorIdx = it.M;
        _serRpmReal?.Limpiar();
        _serRpmTarget?.Limpiar();
        _serVel?.Limpiar();
    }

    private sealed class MotorItem
    {
        public string Uid { get; set; } = "";
        public int M { get; set; }
        public string Etiqueta { get; set; } = "";
        public override string ToString() => Etiqueta;
    }

    // ---------- marca --------------------------------------------------------

    private async void OnMarcarClick(object? s, RoutedEventArgs e)
    {
        if (_client == null) return;
        string texto = "cambio " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        bool ok = await _client.MarcarPidAsync(texto).ConfigureAwait(true);
        Aviso?.Invoke(ok
            ? T("Marca puesta en el registro de todos los motores")
            : T("No se pudo marcar: no hay registro abierto"));
    }

    // ---------- pintado ------------------------------------------------------

    private void PintarLecturas(double rpm, double target, double carga)
    {
        var vRpm = this.FindControl<TextBlock>("ValRpm");
        var vTgt = this.FindControl<TextBlock>("ValTarget");
        var vCar = this.FindControl<TextBlock>("ValCarga");

        if (vRpm != null)
            vRpm.Text = double.IsNaN(rpm) ? TxtSinDatos : rpm.ToString("F0", CultureInfo.InvariantCulture);
        if (vTgt != null)
            vTgt.Text = target.ToString("F0", CultureInfo.InvariantCulture);
        if (vCar != null)
        {
            vCar.Text = carga.ToString("F0", CultureInfo.InvariantCulture);
            // Saturado: el motor está contra el techo y el PID no tiene margen.
            // Se avisa en rojo porque es la diferencia entre "hay que tocar Kp"
            // y "hay que revisar la máquina".
            vCar.Foreground = carga >= 99
                ? new SolidColorBrush(Color.Parse("#C0392B"))
                : (IBrush?)RecursoPincel("PilotXPanelText") ?? Brushes.Black;
        }
    }

    private IBrush? RecursoPincel(string clave)
        => this.TryFindResource(clave, out var v) ? v as IBrush : null;

    /// <summary>Encuadra el eje a los datos con un 10% de aire arriba y abajo.
    /// Piso de 10 para que con el motor parado el grafico no se vuelva un
    /// microscopio del ruido.</summary>
    private void EncuadrarEje()
    {
        if (_lienzo == null) return;
        double alto = Math.Max(_lienzo.MaxAbsoluto(10), 10);
        _lienzo.RangoLo = 0;
        _lienzo.RangoHi = Math.Ceiling(alto * 1.1 / 10.0) * 10.0;

        var max = this.FindControl<TextBlock>("AxisMax");
        var min = this.FindControl<TextBlock>("AxisMin");
        if (max != null) max.Text = _lienzo.RangoHi.ToString("F0", CultureInfo.InvariantCulture);
        if (min != null) min.Text = "0";
    }

    private void PintarEstado()
    {
        var st = this.FindControl<TextBlock>("StatusText");
        if (st != null) st.Text = T(_estado);
    }
}
