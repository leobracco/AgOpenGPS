// ============================================================================
// CalculadoraSiembraPanel.axaml.cs — las CUENTAS de pages/calculadora-siembra.html.
//
// Portadas UNA A UNA desde js/calculadora-siembra.js: mismo orden de
// operaciones, mismos factores, mismos redondeos y mismos decimales. Acá un
// decimal de más o de menos es plata en el lote, así que nada se "mejoró":
// lo que en el JS estaba raro quedó igual de raro y anotado con "PARIDAD".
//
// Método del documento del INTA "Cálculos distanciamiento entre semillas de
// gruesa":
//
//   metros lineales de surco por ha = 10.000 m² / e
//   densidad (sem/ha)               = N (sem por metro lineal) × 10.000 / e
//   distanciamiento Dref (cm)       = 100 / N
//   falla        → separación > 1,5 Dref   (doble: entre 2,5 y 3,5 Dref)
//   duplicación  → separación < 0,5 Dref
//   eventos corregidos = totales + fallas + fallas dobles × 2 − duplicaciones
//   densidad corregida = eventos corregidos × (metros por ha / metros medidos)
//   población en tolerancia = eventos entre 0,75 y 1,25 Dref sobre las bien
//                             sembradas (las que caen entre 0,5 y 1,5 Dref)
//
// TRAMPAS DE PORTEO cubiertas acá (cada una rompía un número):
//   · Math.round de JS redondea la mitad HACIA ARRIBA; Math.Round de .NET es
//     bancario (2,5 → 2). Se usa JsRound = Math.Floor(v + 0.5).
//   · toLocaleString('es-AR') separa miles con punto. Se arma el
//     NumberFormatInfo a mano para no depender de que la máquina tenga los
//     datos de la cultura (ICU en Linux).
//   · parseFloat toma el prefijo numérico ("12abc" → 12) y devuelve NaN si no
//     hay ninguno; double.TryParse no. Se replica con JsParseFloat.
//   · Escribir .value en JS NO dispara el evento 'input'; asignar .Text en
//     Avalonia SÍ dispara TextChanged. Sin el guard _sync, escribir el
//     resultado en un campo volvía a disparar la cuenta en bucle.
//
// QUÉ SIGUE EN HTML: la página y su JS quedan intactos — los usa la PWA del
// celular. El wire tampoco cambia (ver Services/CalculadoraSiembraClient.cs).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class CalculadoraSiembraPanel : UserControl, IPanelEmbebible
{
    private CalculadoraSiembraClient? _client;
    private CancellationTokenSource? _cts;

    /// <summary>El operario tocó la ✕ — el host cierra el panel flotante.</summary>
    public Action? OnRequestCerrar { get; set; }

    // ── estado de la pantalla (el `state` + los `manda*` del JS) ────────────
    private readonly List<CalcMotorItem> _motores = new List<CalcMotorItem>();
    private string _mandaDensidad = "dens";
    private string _mandaPms = "kg";
    private string _tab = "densidad";

    /// <summary>Estamos escribiendo nosotros en los campos: el TextChanged que
    /// eso dispara NO es del operario y no tiene que recalcular ni cambiar cuál
    /// campo manda. En el JS esto sale gratis (asignar .value no dispara
    /// 'input'); en Avalonia hay que decirlo.</summary>
    private bool _sync;

    /// <summary>Último texto que este panel vio en cada campo. Avalonia dispara
    /// TextChanged también al armar/desarmar el template del TextBox (ver la
    /// nota de ConfigEditor/MaquinaTab), y esos eventos espurios llegan FUERA
    /// del _sync: si se los tomara por tecleo, abrir la pantalla movería solo
    /// "el campo que manda" y la densidad se iría a cero sin que el operario
    /// tocara nada. Por eso un evento cuenta como del operario solo si el texto
    /// realmente cambió.</summary>
    private readonly Dictionary<TextBox, string> _ultimo = new Dictionary<TextBox, string>();

    // ── controles ───────────────────────────────────────────────────────────
    private Ellipse? _implDot; private TextBlock? _implTxt;
    private Button? _tabDensidad; private Button? _tabCampo; private Button? _tabPms; private Button? _tabMotor;
    private Border? _secDensidad; private Border? _secCampo; private Border? _secPms; private Border? _secMotor;

    private TextBox? _dEsp; private TextBox? _dDens; private TextBox? _dSemM; private TextBox? _dDist;
    private TextBlock? _oSemM; private TextBlock? _oSemMU;
    private TextBlock? _oDens; private TextBlock? _oDensU;
    private TextBlock? _oDist; private TextBlock? _oDistU;
    private TextBlock? _oMetros; private TextBlock? _oMetrosU;
    private TextBlock? _oFalla; private TextBlock? _oFallaU;
    private TextBlock? _oDup; private TextBlock? _oDupU;
    private TextBlock? _oTol; private TextBlock? _oTolU;

    private TextBox? _cDref; private TextBox? _cMetros; private TextBox? _cEsp; private TextBox? _cEventos;
    private TextBlock? _cMsg;
    private TextBlock? _eTot; private TextBlock? _eTotU;
    private TextBlock? _eOk; private TextBlock? _eOkU;
    private TextBlock? _eFallas; private TextBlock? _eFallasU;
    private TextBlock? _eDup; private TextBlock? _eDupU;
    private TextBlock? _eCorr; private TextBlock? _eCorrU;
    private TextBlock? _eDreal; private TextBlock? _eDrealU;
    private TextBlock? _eDcorr; private TextBlock? _eDcorrU;
    private TextBlock? _eEnRango; private TextBlock? _eEnRangoU;
    private TextBlock? _ePobl; private TextBlock? _ePoblU;
    private TextBlock? _eDesv; private TextBlock? _eDesvU;
    private TextBlock? _ePctF; private TextBlock? _ePctFU;
    private TextBlock? _ePctD; private TextBlock? _ePctDU;

    private TextBox? _pPms; private TextBox? _pKg; private TextBox? _pSem; private TextBox? _pPg;
    private TextBlock? _oPSem; private TextBlock? _oPSemU;
    private TextBlock? _oPKg; private TextBlock? _oPKgU;
    private TextBlock? _oPLog; private TextBlock? _oPLogU;

    private ComboBox? _mMotor;
    private TextBox? _mSemM; private TextBox? _mVel; private TextBox? _mSurcos;
    private TextBox? _mSemVuelta; private TextBox? _mPpr; private TextBox? _mMaxHz;
    private TextBlock? _oMSemS; private TextBlock? _oMSemSU;
    private TextBlock? _oMRpm; private TextBlock? _oMRpmU;
    private TextBlock? _oMHz; private TextBlock? _oMHzU;
    private TextBlock? _oMUso; private TextBlock? _oMUsoU;
    private TextBlock? _oMVelMax; private TextBlock? _oMVelMaxU;

    // ── pinceles: salen de los tokens del tema (Theme/PilotXTheme.axaml).
    //    El fallback es el mismo valor del token y está solo para que un
    //    recurso ausente no voltee la pantalla. ─────────────────────────────
    private IBrush _texto = Brushes.Black;
    private IBrush _dim = Brushes.Gray;
    private IBrush _ok = Brushes.Green;
    private IBrush _warn = Brushes.Orange;
    private IBrush _err = Brushes.Red;
    private IBrush _acento = Brushes.Green;
    private IBrush _superficie2 = Brushes.WhiteSmoke;
    private IBrush _borde = Brushes.LightGray;
    private IBrush _textoMedio = Brushes.DimGray;

    public CalculadoraSiembraPanel()
    {
        InitializeComponent();
        ResolverPinceles();
        ResolverControles();
        CablearEventos();
        PintarTabs();
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ------------------------------------------------------------------ ciclo

    /// <summary>Inyecta el cliente y dispara la precarga desde la máquina real
    /// (el cargarContexto() del JS). Se rehace en cada apertura: la página HTML
    /// también se recarga entera cada vez que se entra.</summary>
    public void Attach(CalculadoraSiembraClient client)
    {
        _client = client;
        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();
        _ = CargarContextoAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        // El teclado nativo no puede quedar abierto arriba del mapa.
        if (_client != null) _ = _client.TecladoAsync(false);
    }

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
        var raiz = this.FindControl<DockPanel>("ContenidoRaiz");
        if (raiz != null) raiz.Margin = new Thickness(0);
    }

    /// <summary>La pill con la geometría que trajo PilotX va a la barra de
    /// contexto del shell.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.Desprender(this.FindControl<Border>("HeaderPill"));

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    // ------------------------------------------------------------- resolución

    private void ResolverPinceles()
    {
        _texto       = Rec("PilotXPanelText",       Color.Parse("#101612"));
        _textoMedio  = Rec("PilotXPanelTextMid",    Color.Parse("#303B33"));
        _dim         = Rec("PilotXPanelTextDim",    Color.Parse("#535E54"));
        _ok          = Rec("PilotXPanelOk",         Color.Parse("#2F7A26"));
        _warn        = Rec("PilotXPanelWarn",       Color.Parse("#8A6100"));
        _err         = Rec("PilotXPanelErr",        Color.Parse("#C0261F"));
        _acento      = Rec("PilotXPanelAccent",     Color.Parse("#4ABA3E"));
        _superficie2 = Rec("PilotXPanelSurface2",   Color.Parse("#EDF1EC"));
        _borde       = Rec("PilotXPanelBorderHigh", Color.Parse("#C5CFC5"));
    }

    private IBrush Rec(string clave, Color fallback)
    {
        try
        {
            if (this.TryFindResource(clave, out var v) && v is IBrush b) return b;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }

    private void ResolverControles()
    {
        _implDot = this.FindControl<Ellipse>("ImplDot");
        _implTxt = this.FindControl<TextBlock>("ImplTxt");

        _tabDensidad = this.FindControl<Button>("TabDensidad");
        _tabCampo    = this.FindControl<Button>("TabCampo");
        _tabPms      = this.FindControl<Button>("TabPms");
        _tabMotor    = this.FindControl<Button>("TabMotor");

        _secDensidad = this.FindControl<Border>("SecDensidad");
        _secCampo    = this.FindControl<Border>("SecCampo");
        _secPms      = this.FindControl<Border>("SecPms");
        _secMotor    = this.FindControl<Border>("SecMotor");

        _dEsp  = this.FindControl<TextBox>("DEsp");
        _dDens = this.FindControl<TextBox>("DDens");
        _dSemM = this.FindControl<TextBox>("DSemM");
        _dDist = this.FindControl<TextBox>("DDist");
        _oSemM   = this.FindControl<TextBlock>("OSemM");   _oSemMU   = this.FindControl<TextBlock>("OSemMUni");
        _oDens   = this.FindControl<TextBlock>("ODens");   _oDensU   = this.FindControl<TextBlock>("ODensUni");
        _oDist   = this.FindControl<TextBlock>("ODist");   _oDistU   = this.FindControl<TextBlock>("ODistUni");
        _oMetros = this.FindControl<TextBlock>("OMetros"); _oMetrosU = this.FindControl<TextBlock>("OMetrosUni");
        _oFalla  = this.FindControl<TextBlock>("OFalla");  _oFallaU  = this.FindControl<TextBlock>("OFallaUni");
        _oDup    = this.FindControl<TextBlock>("ODup");    _oDupU    = this.FindControl<TextBlock>("ODupUni");
        _oTol    = this.FindControl<TextBlock>("OTol");    _oTolU    = this.FindControl<TextBlock>("OTolUni");

        _cDref    = this.FindControl<TextBox>("CDref");
        _cMetros  = this.FindControl<TextBox>("CMetros");
        _cEsp     = this.FindControl<TextBox>("CEsp");
        _cEventos = this.FindControl<TextBox>("CEventos");
        _cMsg     = this.FindControl<TextBlock>("CMsg");
        _eTot     = this.FindControl<TextBlock>("ETot");     _eTotU     = this.FindControl<TextBlock>("ETotUni");
        _eOk      = this.FindControl<TextBlock>("EOk");      _eOkU      = this.FindControl<TextBlock>("EOkUni");
        _eFallas  = this.FindControl<TextBlock>("EFallas");  _eFallasU  = this.FindControl<TextBlock>("EFallasUni");
        _eDup     = this.FindControl<TextBlock>("EDup");     _eDupU     = this.FindControl<TextBlock>("EDupUni");
        _eCorr    = this.FindControl<TextBlock>("ECorr");    _eCorrU    = this.FindControl<TextBlock>("ECorrUni");
        _eDreal   = this.FindControl<TextBlock>("EDreal");   _eDrealU   = this.FindControl<TextBlock>("EDrealUni");
        _eDcorr   = this.FindControl<TextBlock>("EDcorr");   _eDcorrU   = this.FindControl<TextBlock>("EDcorrUni");
        _eEnRango = this.FindControl<TextBlock>("EEnRango"); _eEnRangoU = this.FindControl<TextBlock>("EEnRangoUni");
        _ePobl    = this.FindControl<TextBlock>("EPobl");    _ePoblU    = this.FindControl<TextBlock>("EPoblUni");
        _eDesv    = this.FindControl<TextBlock>("EDesv");    _eDesvU    = this.FindControl<TextBlock>("EDesvUni");
        _ePctF    = this.FindControl<TextBlock>("EPctF");    _ePctFU    = this.FindControl<TextBlock>("EPctFUni");
        _ePctD    = this.FindControl<TextBlock>("EPctD");    _ePctDU    = this.FindControl<TextBlock>("EPctDUni");

        _pPms = this.FindControl<TextBox>("PPms");
        _pKg  = this.FindControl<TextBox>("PKg");
        _pSem = this.FindControl<TextBox>("PSem");
        _pPg  = this.FindControl<TextBox>("PPg");
        _oPSem = this.FindControl<TextBlock>("OPSem"); _oPSemU = this.FindControl<TextBlock>("OPSemUni");
        _oPKg  = this.FindControl<TextBlock>("OPKg");  _oPKgU  = this.FindControl<TextBlock>("OPKgUni");
        _oPLog = this.FindControl<TextBlock>("OPLog"); _oPLogU = this.FindControl<TextBlock>("OPLogUni");

        _mMotor     = this.FindControl<ComboBox>("MMotor");
        _mSemM      = this.FindControl<TextBox>("MSemM");
        _mVel       = this.FindControl<TextBox>("MVel");
        _mSurcos    = this.FindControl<TextBox>("MSurcos");
        _mSemVuelta = this.FindControl<TextBox>("MSemVuelta");
        _mPpr       = this.FindControl<TextBox>("MPpr");
        _mMaxHz     = this.FindControl<TextBox>("MMaxHz");
        _oMSemS   = this.FindControl<TextBlock>("OMSemS");   _oMSemSU   = this.FindControl<TextBlock>("OMSemSUni");
        _oMRpm    = this.FindControl<TextBlock>("OMRpm");    _oMRpmU    = this.FindControl<TextBlock>("OMRpmUni");
        _oMHz     = this.FindControl<TextBlock>("OMHz");     _oMHzU     = this.FindControl<TextBlock>("OMHzUni");
        _oMUso    = this.FindControl<TextBlock>("OMUso");    _oMUsoU    = this.FindControl<TextBlock>("OMUsoUni");
        _oMVelMax = this.FindControl<TextBlock>("OMVelMax"); _oMVelMaxU = this.FindControl<TextBlock>("OMVelMaxUni");
    }

    private void CablearEventos()
    {
        // Densidad: el campo tocado pasa a mandar (dEsp no manda, igual que en
        // el JS: solo recalcula).
        Cablear(_dEsp,  "Distancia entre hileras (m)",           OnDensidadChanged);
        Cablear(_dDens, "Densidad objetivo (sem/ha)",            OnDensidadChanged);
        Cablear(_dSemM, "Semillas por metro de surco",           OnDensidadChanged);
        Cablear(_dDist, "Distanciamiento entre semillas (cm)",   OnDensidadChanged);

        // Evaluación a campo: los campos NO recalculan solos (el JS recalcula
        // recién con el botón Calcular).
        Cablear(_cDref,   "Distanciamiento de referencia (cm)", null);
        Cablear(_cMetros, "Metros de surco evaluados",          null);
        Cablear(_cEsp,    "Distancia entre hileras (m)",        null);
        Cablear(_cEventos, "Separaciones entre semillas (cm)",  null, numerico: false);

        Cablear(_pPms, "Peso de mil semillas (g)",  OnPmsChanged);
        Cablear(_pKg,  "Kilos por hectárea",        OnPmsChanged);
        Cablear(_pSem, "Semillas por hectárea",     OnPmsChanged);
        Cablear(_pPg,  "Poder germinativo (%)",     OnPmsChanged);

        Cablear(_mSemM,      "Semillas por metro (objetivo)",      OnMotorChanged);
        Cablear(_mVel,       "Velocidad de trabajo (km/h)",        OnMotorChanged);
        Cablear(_mSurcos,    "Surcos que alimenta",                OnMotorChanged);
        Cablear(_mSemVuelta, "Semillas por vuelta del dosificador",OnMotorChanged);
        Cablear(_mPpr,       "Pulsos por vuelta del sensor",       OnMotorChanged);
        Cablear(_mMaxHz,     "Tope medido del motor (Hz)",         OnMotorChanged);
    }

    /// <summary>Engancha el teclado nativo de PilotX (misma señal HTTP que
    /// mandan las páginas del Hub) y, si corresponde, el recálculo.</summary>
    private void Cablear(TextBox? t, string titulo, EventHandler<TextChangedEventArgs>? alCambiar,
                         bool numerico = true)
    {
        if (t == null) return;
        _ultimo[t] = t.Text ?? "";
        t.GotFocus  += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, numerico, PilotX.Cockpit.Bars.Traductor.T(titulo)); };
        t.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
        if (alCambiar != null) t.TextChanged += alCambiar;
    }

    /// <summary>Escritura HECHA POR EL PANEL (el <c>el(id).value = …</c> del
    /// JS): no cuenta como tecleo del operario ni cambia cuál campo manda.</summary>
    private void Escribir(TextBox? t, string s)
    {
        if (t == null) return;
        _ultimo[t] = s;
        if (string.Equals(t.Text ?? "", s, StringComparison.Ordinal)) return;
        _sync = true;
        try { t.Text = s; }
        finally { _sync = false; }
    }

    /// <summary>¿El TextChanged que llegó es tecleo de verdad? Ver _ultimo.</summary>
    private bool EsDelOperario(object? sender)
    {
        if (sender is not TextBox t) return false;
        string actual = t.Text ?? "";
        if (_ultimo.TryGetValue(t, out var prev) && string.Equals(prev, actual, StringComparison.Ordinal))
            return false;
        _ultimo[t] = actual;
        return true;
    }

    // ------------------------------------------------------------------- tabs

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t) MostrarTab(t);
    }

    private void MostrarTab(string tab)
    {
        _tab = tab;
        if (_secDensidad != null) _secDensidad.IsVisible = tab == "densidad";
        if (_secCampo    != null) _secCampo.IsVisible    = tab == "campo";
        if (_secPms      != null) _secPms.IsVisible      = tab == "pms";
        if (_secMotor    != null) _secMotor.IsVisible    = tab == "motor";
        PintarTabs();
    }

    private void PintarTabs()
    {
        PintarTab(_tabDensidad, _tab == "densidad");
        PintarTab(_tabCampo,    _tab == "campo");
        PintarTab(_tabPms,      _tab == "pms");
        PintarTab(_tabMotor,    _tab == "motor");
    }

    /// <summary>Activa = el ".btn.primary" del HTML (relleno verde de marca con
    /// texto oscuro encima: 7,3:1); inactiva = el ".btn" gris.</summary>
    private void PintarTab(Button? b, bool activa)
    {
        if (b == null) return;
        b.Background  = activa ? _acento : _superficie2;
        b.Foreground  = activa ? _texto : _textoMedio;
        b.BorderBrush = activa ? _acento : _borde;
        b.FontWeight  = activa ? FontWeight.SemiBold : FontWeight.Normal;
    }

    // -------------------------------------------------------------- precarga

    private async Task CargarContextoAsync(CancellationToken ct)
    {
        if (_client == null) return;
        CalcContexto? ctx = null;
        try
        {
            ctx = await _client.CargarContextoAsync(ct).ConfigureAwait(false);
        }
        // TaskCanceledException hereda de OperationCanceledException: sin este
        // filtro un timeout se confunde con la cancelación del panel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { ctx = null; }

        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => AplicarContexto(ctx));
    }

    private void AplicarContexto(CalcContexto? ctx)
    {
        // Toda la geometría sale de la configuración de secciones de PilotX:
        // ancho de labor, cantidad de surcos (= secciones) y distancia entre
        // hileras (= ancho / secciones).
        double ancho = ctx?.Ancho ?? 0;
        int surcos = ctx?.Surcos ?? 0;
        double espaciamiento = 0;
        if (ancho > 0 && surcos > 0) espaciamiento = ancho / surcos;

        if (espaciamiento > 0)
        {
            if (_implDot != null) _implDot.Fill = _ok;
            if (_implTxt != null)
            {
                _implTxt.Foreground = _ok;
                _implTxt.Text = surcos.ToString(Inv) + " " + PilotX.Cockpit.Bars.Traductor.T("surcos a") + " "
                              + Fixed(espaciamiento, 2) + " m · " + PilotX.Cockpit.Bars.Traductor.T("ancho") + " "
                              + Fixed(ancho, 2) + " m " + PilotX.Cockpit.Bars.Traductor.T("(de PilotX)");
            }
            Escribir(_dEsp, Fixed(espaciamiento, 3));
            Escribir(_cEsp, Fixed(espaciamiento, 3));
        }
        else
        {
            if (_implDot != null) _implDot.Fill = _warn;
            if (_implTxt != null)
            {
                _implTxt.Foreground = _warn;
                _implTxt.Text = PilotX.Cockpit.Bars.Traductor.T("PilotX sin secciones configuradas");
            }
        }

        // Motores QuantiX configurados.
        _motores.Clear();
        var etiquetas = new List<string>();
        if (ctx != null)
        {
            foreach (var m in ctx.Motores) { _motores.Add(m); etiquetas.Add(m.Etiqueta); }
        }
        if (_mMotor != null)
        {
            _sync = true;
            try
            {
                _mMotor.ItemsSource = etiquetas;
                _mMotor.SelectedIndex = -1;
            }
            finally { _sync = false; }
            // El JS dispara 'change' a mano si hay motores: eso aplica el
            // primero. Acá lo hace la selección (fuera del guard, a propósito).
            if (_motores.Count > 0) _mMotor.SelectedIndex = 0;
        }

        CalcDensidad();
        CalcPms();
        CalcMotor();
    }

    // ============ 1. Densidad ↔ sem/m ↔ distanciamiento ====================
    //
    // Tres formas de decir lo mismo. Se guarda cuál fue el último campo tocado
    // y ese manda; los otros dos se recalculan. Sin esto, escribir en uno
    // pisaría lo que el operario acaba de escribir en otro.

    private void OnDensidadChanged(object? sender, TextChangedEventArgs e)
    {
        if (_sync || !EsDelOperario(sender)) return;
        if (ReferenceEquals(sender, _dDens)) _mandaDensidad = "dens";
        else if (ReferenceEquals(sender, _dSemM)) _mandaDensidad = "semm";
        else if (ReferenceEquals(sender, _dDist)) _mandaDensidad = "dist";
        CalcDensidad();
    }

    private void CalcDensidad()
    {
        double e = Num(_dEsp, 0);
        if (!(e > 0)) return;
        double metrosHa = 10000 / e;
        double semM;

        if (_mandaDensidad == "dens")
        {
            semM = Num(_dDens, 0) / metrosHa;
        }
        else if (_mandaDensidad == "semm")
        {
            semM = Num(_dSemM, 0);
        }
        else
        {
            double dist = Num(_dDist, 0);
            semM = dist > 0 ? (100 / dist) : 0;
        }

        double dens = semM * metrosHa;
        double dref = semM > 0 ? (100 / semM) : 0;

        if (_mandaDensidad != "dens") Escribir(_dDens, dens > 0 ? JsInt(dens) : "");
        if (_mandaDensidad != "semm") Escribir(_dSemM, semM > 0 ? Fixed(semM, 2) : "");
        if (_mandaDensidad != "dist") Escribir(_dDist, dref > 0 ? Fixed(dref, 1) : "");

        Set(_oSemM, _oSemMU, Fmt(semM, 2), "sem/m");
        Set(_oDens, _oDensU, Miles(dens), "sem/ha");
        Set(_oDist, _oDistU, Fmt(dref, 1), "cm");
        Set(_oMetros, _oMetrosU, Miles(metrosHa), "m/ha");
        Set(_oFalla, _oFallaU, Fmt(dref * 1.5, 1), "cm");
        Set(_oDup, _oDupU, Fmt(dref * 0.5, 1), "cm");
        if (dref > 0) Set(_oTol, _oTolU, Fmt(dref * 0.75, 1) + "–" + Fmt(dref * 1.25, 1), "cm");
        else          Set(_oTol, _oTolU, "—", null);
    }

    // ============ 2. Evaluación a campo ====================================

    /// <summary>PARIDAD con el JS: la coma es SEPARADOR, no decimal. "20,5"
    /// entra como dos eventos (20 y 5). El replace(',', '.') que hace el JS
    /// después del split ya no encuentra ninguna coma — se replica igual.</summary>
    private static List<double> ParseEventos(string? txt)
    {
        var salida = new List<double>();
        if (string.IsNullOrEmpty(txt)) return salida;
        // El \s del JS: espacios (incluido el duro, U+00A0), tabs y saltos de
        // línea; más la coma y el punto y coma del split original.
        var partes = (txt ?? "").Split(new[] { ' ', '\t', '\r', '\n', '\f', '\v', '\u00a0', ',', ';' },
                                       StringSplitOptions.None);
        foreach (var p in partes)
        {
            double v = JsParseFloat(p);
            if (!double.IsNaN(v) && !double.IsInfinity(v) && v > 0) salida.Add(v);
        }
        return salida;
    }

    private void OnCalcularClick(object? sender, RoutedEventArgs e) => CalcCampo();

    private void CalcCampo()
    {
        var ev = ParseEventos(_cEventos?.Text);
        if (ev.Count < 2)
        {
            SetMsg("✕ cargá al menos 2 separaciones", _err);
            return;
        }
        double dref = Num(_cDref, 0);
        double metros = Num(_cMetros, 0);
        double e = Num(_cEsp, 0);
        if (!(dref > 0))
        {
            SetMsg("✕ falta el distanciamiento de referencia", _err);
            return;
        }

        int fallas = 0, fallasDobles = 0, dups = 0, enTol = 0;
        var correctos = new List<double>();
        foreach (double x in ev)
        {
            if (x < 0.5 * dref) { dups++; continue; }
            if (x > 1.5 * dref)
            {
                // Entre 2,5 y 3,5 Dref el hueco equivale a dos semillas faltantes.
                if (x > 2.5 * dref) fallasDobles++; else fallas++;
                continue;
            }
            correctos.Add(x);
            if (x >= 0.75 * dref && x <= 1.25 * dref) enTol++;
        }

        int total = ev.Count;
        int corregidos = total + fallas + fallasDobles * 2 - dups;
        double metrosHa = e > 0 ? (10000 / e) : 0;
        double factor = (metros > 0 && metrosHa > 0) ? (metrosHa / metros) : 0;
        double dReal = total * factor;
        double dCorr = corregidos * factor;
        double densRef = (dref > 0 && metrosHa > 0) ? (100 / dref) * metrosHa : 0;

        double suma = 0;
        foreach (double b in correctos) suma += b;
        double media = suma / (correctos.Count == 0 ? 1 : correctos.Count);
        double acum = 0;
        foreach (double b in correctos) acum += (b - media) * (b - media);
        double varianza = acum / (correctos.Count == 0 ? 1 : correctos.Count);
        double desvio = Math.Sqrt(varianza);

        Set(_eTot, _eTotU, total.ToString(Inv), null);
        Set(_eOk, _eOkU, correctos.Count.ToString(Inv), null);
        Set(_eFallas, _eFallasU, (fallas + fallasDobles).ToString(Inv),
            fallasDobles != 0 ? "(" + fallasDobles.ToString(Inv) + " dobles)" : null);
        Set(_eDup, _eDupU, dups.ToString(Inv), null);
        Set(_eCorr, _eCorrU, corregidos.ToString(Inv), null);
        Set(_eDreal, _eDrealU, Miles(dReal), "sem/ha");
        Set(_eDcorr, _eDcorrU, Miles(dCorr), "sem/ha");

        // El doc pide que la corregida no difiera más de ±5% de la de referencia.
        if (densRef > 0 && dCorr > 0)
        {
            double desvPct = ((dCorr - densRef) / densRef) * 100;
            Set(_eEnRango, _eEnRangoU, (desvPct >= 0 ? "+" : "") + Fixed(desvPct, 1), "%",
                Math.Abs(desvPct) <= 5 ? _ok : _err);
        }
        else
        {
            Set(_eEnRango, _eEnRangoU, "—", null);
        }

        double pobl = correctos.Count > 0 ? (enTol * 100.0 / correctos.Count) : 0;
        Set(_ePobl, _ePoblU, Fmt(pobl, 1), "%", pobl >= 70 ? _ok : pobl >= 50 ? _warn : _err);
        if (correctos.Count != 0) Set(_eDesv, _eDesvU, Fmt(desvio, 2), "cm");
        else                      Set(_eDesv, _eDesvU, "—", null);
        if (corregidos > 0) Set(_ePctF, _ePctFU, Fmt((fallas + fallasDobles * 2) * 100.0 / corregidos, 1), "%");
        else                Set(_ePctF, _ePctFU, "—", null);
        if (corregidos > 0) Set(_ePctD, _ePctDU, Fmt(dups * 100.0 / corregidos, 1), "%");
        else                Set(_ePctD, _ePctDU, "—", null);

        SetMsg("✓ " + total.ToString(Inv) + " " + PilotX.Cockpit.Bars.Traductor.T("separaciones evaluadas"), _ok);
    }

    private void OnLimpiarClick(object? sender, RoutedEventArgs e)
    {
        Escribir(_cEventos, "");
        if (_cMsg != null) { _cMsg.Text = ""; _cMsg.Foreground = _dim; }
        Set(_eTot, _eTotU, "—", null);
        Set(_eOk, _eOkU, "—", null);
        Set(_eFallas, _eFallasU, "—", null);
        Set(_eDup, _eDupU, "—", null);
        Set(_eCorr, _eCorrU, "—", null);
        Set(_eDreal, _eDrealU, "—", null);
        Set(_eDcorr, _eDcorrU, "—", null);
        Set(_eEnRango, _eEnRangoU, "—", null);
        Set(_ePobl, _ePoblU, "—", null);
        Set(_eDesv, _eDesvU, "—", null);
        Set(_ePctF, _ePctFU, "—", null);
        Set(_ePctD, _ePctDU, "—", null);
    }

    // ============ 3. kg/ha ↔ sem/ha ========================================

    private void OnPmsChanged(object? sender, TextChangedEventArgs e)
    {
        if (_sync || !EsDelOperario(sender)) return;
        if (ReferenceEquals(sender, _pKg)) _mandaPms = "kg";
        else if (ReferenceEquals(sender, _pSem)) _mandaPms = "sem";
        CalcPms();
    }

    private void CalcPms()
    {
        double pms = Num(_pPms, 0);
        if (!(pms > 0)) return;
        double kg, sem;
        if (_mandaPms == "kg")
        {
            kg = Num(_pKg, 0);
            sem = kg * 1000000 / pms;
            Escribir(_pSem, sem > 0 ? JsInt(sem) : "");
        }
        else
        {
            sem = Num(_pSem, 0);
            kg = sem * pms / 1000000;
            Escribir(_pKg, kg > 0 ? Fixed(kg, 2) : "");
        }

        double pg = Num(_pPg, 100) / 100;
        Set(_oPSem, _oPSemU, Miles(sem), "sem/ha");
        Set(_oPKg, _oPKgU, Fmt(kg, 2), "kg/ha");
        Set(_oPLog, _oPLogU, Miles(sem * pg), "pl/ha");
    }

    // ============ 4. Motor: sem/vuelta y rpm ===============================

    private void OnMotorChanged(object? sender, TextChangedEventArgs e)
    {
        if (_sync || !EsDelOperario(sender)) return;
        CalcMotor();
    }

    private void OnMotorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_sync) return;
        int i = _mMotor?.SelectedIndex ?? -1;
        if (i < 0 || i >= _motores.Count) return;
        var m = _motores[i];

        // El "|| 24" / "|| 600" / "|| 0" / "|| 1" del JS: un 0 guardado cae al
        // default, no queda en 0.
        Escribir(_mSemVuelta, JsNum(m.SemillasVuelta != 0 ? m.SemillasVuelta : 24));
        Escribir(_mPpr,       JsNum(m.DientesEngranaje != 0 ? m.DientesEngranaje : 600));
        Escribir(_mMaxHz,     JsNum(m.MaxHz != 0 ? m.MaxHz : 0));
        Escribir(_mSurcos,    JsNum(m.Cortes > 0 ? m.Cortes : 1));
        if (m.UnidadDosis == "sem_m" && m.DosisFija > 0)
            Escribir(_mSemM, JsNum(m.DosisFija));

        CalcMotor();
    }

    private void CalcMotor()
    {
        double semM = Num(_mSemM, 0);
        double vel = Num(_mVel, 0);
        double surcos = Math.Max(1, Num(_mSurcos, 1));
        double semVuelta = Num(_mSemVuelta, 0);
        double ppr = Num(_mPpr, 0);
        double maxHz = Num(_mMaxHz, 0);

        double vMs = vel / 3.6;
        double semS = semM * vMs * surcos;                 // semillas por segundo
        double rpm = semVuelta > 0 ? (semS / semVuelta) * 60 : 0;
        double hz = ppr > 0 ? (rpm * ppr / 60) : 0;

        Set(_oMSemS, _oMSemSU, Fmt(semS, 1), "sem/s");
        Set(_oMRpm, _oMRpmU, Fmt(rpm, 1), "rpm");
        Set(_oMHz, _oMHzU, Fmt(hz, 0), "Hz");

        double uso = maxHz > 0 ? (hz * 100 / maxHz) : 0;
        IBrush? colorUso = uso <= 0 ? null : uso <= 85 ? _ok : uso <= 100 ? _warn : _err;
        if (uso > 0) Set(_oMUso, _oMUsoU, Fmt(uso, 0), "% del tope", colorUso);
        else         Set(_oMUso, _oMUsoU, "—", null, colorUso);

        // A qué velocidad el motor se queda sin vueltas con esta dosis.
        double velMax = 0;
        if (maxHz > 0 && ppr > 0 && semVuelta > 0 && semM > 0)
        {
            double rpmMax = maxHz * 60 / ppr;
            double semSMax = rpmMax * semVuelta / 60;
            velMax = (semSMax / (semM * surcos)) * 3.6;
        }
        IBrush? colorVel = velMax <= 0 ? null : velMax >= vel ? _ok : _err;
        if (velMax > 0) Set(_oMVelMax, _oMVelMaxU, Fmt(velMax, 1), "km/h", colorVel);
        else            Set(_oMVelMax, _oMVelMaxU, "—", null, colorVel);
    }

    // ------------------------------------------------------------- pintado

    /// <summary>Escribe un KPI: valor + unidad (la unidad se esconde cuando el
    /// JS no la ponía, p. ej. cuando el resultado es "—" solo).</summary>
    private void Set(TextBlock? v, TextBlock? u, string texto, string? unidad, IBrush? color = null)
    {
        if (v != null)
        {
            v.Text = texto;
            v.Foreground = color ?? _texto;
        }
        if (u == null) return;
        bool hay = !string.IsNullOrEmpty(unidad);
        if (hay) u.Text = PilotX.Cockpit.Bars.Traductor.T(unidad!);
        u.IsVisible = hay;
    }

    private void SetMsg(string texto, IBrush color)
    {
        if (_cMsg == null) return;
        _cMsg.Text = PilotX.Cockpit.Bars.Traductor.T(texto);
        _cMsg.Foreground = color;
    }

    // -------------------------------------------------- números "a la JS"

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Formato de miles de es-AR ("70.000"), armado a mano para no
    /// depender de que la máquina tenga los datos de la cultura.</summary>
    private static readonly NumberFormatInfo EsAr = new NumberFormatInfo
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = new[] { 3 },
    };

    private static bool Finito(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>Math.round de JS: la mitad va HACIA ARRIBA. El de .NET es
    /// bancario (2,5 → 2) y cambiaría densidades enteras.</summary>
    private static double JsRound(double v) => Math.Floor(v + 0.5);

    /// <summary>Number.prototype.toFixed, en la ÚNICA implementación del repo
    /// (NumeroJs.Fijo, compartida con los cuatro gráficos).
    ///
    /// No sirve ni "F"+dec ni el formato custom "0.000": el primero redondea al
    /// par (0,5625 con 3 da 0.562 y el celular muestra 0.563 — con 18 m / 32
    /// secciones el espaciamiento es 0,5625 justo y el desvío se propaga a
    /// metros/ha y a todo lo de abajo), y el segundo redondea sobre la forma
    /// decimal corta y sube de más (0,145 con 2 da 0.15 y JS da 0.14). El
    /// porqué, con los 8 casos verificados, está en NumeroJs.cs.</summary>
    private static string Fixed(double v, int dec) => NumeroJs.Fijo(v, dec);

    /// <summary>El fmt() del JS: "—" si no es finito o es &lt;= 0.</summary>
    private static string Fmt(double v, int dec) => (!Finito(v) || v <= 0) ? "—" : Fixed(v, dec);

    /// <summary>El miles() del JS: redondeo + separador de miles es-AR.</summary>
    private static string Miles(double v) => (!Finito(v) || v <= 0) ? "—" : JsRound(v).ToString("N0", EsAr);

    /// <summary>Entero como lo escribe el JS en un input (sin separadores).</summary>
    private static string JsInt(double v) => JsRound(v).ToString("F0", Inv);

    /// <summary>Number → String de JS para precargar un input ("600", "40.5").</summary>
    private static string JsNum(double v) => v.ToString("R", Inv);

    /// <summary>El num(id, def) del JS: parseFloat del contenido, y si no da un
    /// número finito se usa el default.</summary>
    private static double Num(TextBox? t, double def)
    {
        double v = JsParseFloat(t?.Text);
        return Finito(v) ? v : def;
    }

    /// <summary>parseFloat de JS: toma el PREFIJO numérico ("12abc" → 12) y
    /// devuelve NaN si no hay ninguno. double.TryParse rechazaría el string
    /// entero y devolvería 0, que acá se confundiría con "el operario escribió
    /// cero".
    ///
    /// La coma se acepta como decimal (los campos son type=number en el HTML y
    /// el navegador nunca deja pasar una coma; el teclado nativo sí). El
    /// textarea de separaciones NO usa este atajo: ahí la coma sigue siendo
    /// separador, como en el JS.</summary>
    private static double JsParseFloat(string? s)
    {
        if (string.IsNullOrEmpty(s)) return double.NaN;
        string t = s!.Replace(',', '.').Trim();
        int i = 0, n = t.Length;
        if (i < n && (t[i] == '+' || t[i] == '-')) i++;
        int inicio = i;
        while (i < n && t[i] >= '0' && t[i] <= '9') i++;
        bool huboEnteros = i > inicio;
        bool huboDecimales = false;
        if (i < n && t[i] == '.')
        {
            i++;
            int d0 = i;
            while (i < n && t[i] >= '0' && t[i] <= '9') i++;
            huboDecimales = i > d0;
        }
        if (!huboEnteros && !huboDecimales) return double.NaN;

        if (i < n && (t[i] == 'e' || t[i] == 'E'))
        {
            int j = i + 1;
            if (j < n && (t[j] == '+' || t[j] == '-')) j++;
            int k = j;
            while (k < n && t[k] >= '0' && t[k] <= '9') k++;
            if (k > j) i = k;   // exponente válido; si no, se corta antes de la 'e'
        }

        return double.TryParse(t.Substring(0, i), NumberStyles.Float, Inv, out double v)
             ? v : double.NaN;
    }
}
