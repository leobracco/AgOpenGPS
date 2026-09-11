// ============================================================================
// UturnTab.cs — pestaña "Otros › U-Turn" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=uturn` (la sección data-tab="uturn" +
// tabs.uturn de config.js, líneas 1439-1494).
//
// QUÉ QUEDÓ NATIVO: las dos cartas ("Geometría del giro" y "Suavizado y
// extensión") con sus dos NUD validados (radio y distancia al límite), sus dos
// steppers ± (suavizado y extensión), las dos notas de referencia y el guardado
// (POST /api/aog/config/uturn) con relectura del snapshot.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (Tram y los módulos embebidos).
//
// Qué config toca: /api/aog/config sección `uturn` — la MISMA superficie que
// usan casi todas las pestañas de este panel. NO toca /api/implemento (surcos y
// semillas/ha de la pantalla de siembra) ni /api/tool (ToolConfigDto camelCase
// del editor de QuantiX). Son tres configuraciones distintas y no están
// sincronizadas.
//
// ---------------------------------------------------------------------------
// ACÁ SE CONFIGURA LA GEOMETRÍA DEL GIRO, NO SI EL BOTÓN EXISTE. El toggle que
// muestra u oculta el botón U-Turn de la pantalla principal (`feature_uturn`)
// es de la pestaña "Botones", otra sección del wire. No se duplica acá.
//
// El radio manda la curva del giro en cabecera y la distancia al límite decide
// cuánto antes del boundary arranca: un radio mal cargado es la máquina girando
// fuera del lote o clavándose en el alambrado. Por eso el panel valida con los
// mismos rangos que el HTML y REESCRIBE en el campo el valor con el que se
// guardó — el operario tiene que ver el número final, no el que tipeó.
// ---------------------------------------------------------------------------
//
// Unidades — quirk PROPIO de esta pestaña, distinto del resto del panel:
//   · el wire viene SIEMPRE en metros; acá el display es m | ft con factor
//     3.28 EXACTO (utM2disp/utDisp2m del JS), NO 3.28084 ni los cm|in de
//     CfgCtx.M2Disp. "Unificarlo por prolijidad" cambiaría los números
//     respecto del celular sobre el mismo motor;
//   · `extension_length` vive en METROS ENTEROS y el stepper avanza SIEMPRE de
//     a 1 m, aunque el rótulo muestre pies (otro quirk del original: en
//     imperial el label salta de a ~3 ft por toque). Se conserva tal cual;
//   · `smoothing` es ADIMENSIONAL: no lleva unidad ni conversión.
//
// Trampas cubiertas:
//   · Rangos de la UI: radio [2, 100] m / [6.56, 328] ft · distancia [0.2, 100]
//     m / [0.66, 328] ft. El clamp CORRIGE en silencio (no rechaza), igual que
//     leerNudDec2; solo lo que no es número queda en rojo y BLOQUEA el guardado.
//   · Los clamps del server NO son los mismos: radio sin techo (solo ≥ 2 m) y
//     smoothing sin paridad forzada. Si el celular u otro cliente dejó radio
//     120 m o smoothing 13, el panel PINTA eso tal cual y no lo "corrige" solo:
//     corregirlo al abrir marcaría sucio sin gesto del operario y postearía un
//     reset de giro gratis (ver abajo). El clamp corre recién al guardar.
//   · Coma decimal: el operario rioplatense tipea "8,5". Se reemplaza por punto
//     y se parsea con InvariantCulture (`double.Parse` con cultura es-AR
//     convierte 8.5 en 85 EN SILENCIO).
//   · Guardar sin cambios no manda nada (guard _dirty). Acá importa más que en
//     las otras pestañas: cada POST dispara BuildTurnLines() +
//     ResetCreatedYouTurn() en el motor, o sea DESCARTA el U-turn ya dibujado.
//     Un POST gratis al cerrar el panel = un giro descartado gratis.
//   · _cargando: en Avalonia TextChanged dispara también al escribir por
//     código (la carga inicial, el clamp que reescribe y el repintado posterior
//     al guardado). Sin el flag, el panel queda sucio apenas se abre y el
//     cierre re-postea (ver el punto anterior).
//
// Quirk del HTML que NO se porta (a propósito): config.js pone `ut.dirty =
// false` ANTES del POST, así que un guardado fallido deja el segundo intento
// sin mandar nada. Acá el dirty se limpia SOLO si el POST salió bien.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO Y CON REINICIO REAL DEL MOTOR
// (banco, 2026-08-16). Método válido: POST → mirar el archivo en disco → matar
// el motor → arranque limpio → GET. ("Guardar y releer" NO prueba nada:
// BuildSnapshot arma el snapshot leyendo Settings EN MEMORIA.)
//   POST /api/aog/config/uturn {radius 9.37, distance_from_boundary 3.45,
//   extension_length 27, smoothing 22} → ok
//   G:\Documentos\AgOpenGPS\Vehicles\PilotX.XML quedó con set_youTurnRadius
//   9.37, set_youTurnDistanceFromBoundary 3.45, set_youTurnExtensionLength 27,
//   setAS_uTurnSmoothing 22 → Stop-Process del motor → arranque limpio →
//   GET /api/aog/config devolvió esos MISMOS cuatro números.
//   PERSISTEN LOS 4 DE 4: radius · distance_from_boundary · extension_length ·
//   smoothing.
// QUIÉN persiste: el `Settings.Default.Save()` del dispatcher, que en este
// banco SÍ escribe porque hay perfil de vehículo (RegistrySettings
// .vehicleFileName = "PilotX", con su Vehicles\PilotX.XML). tool.json NO tiene
// ninguno de estos 4 campos (ToolGeometryStore solo espeja geometría de
// implemento), así que acá la red de seguridad es UNA sola y es el perfil: si
// el motor arrancara sin perfil elegido, esta pestaña pasaría a cantar un
// "Guardado ✔" que dura hasta el próximo arranque.
//
// CLAMPS DEL SERVER — verificados en el mismo banco, contra el wire:
//   POST {radius 0.5, distance_from_boundary 0.01, extension_length 99,
//   smoothing 99} → el GET devolvió {2, 0.2, 50, 50}. O sea: el server sube al
//   mínimo y baja al máximo por su cuenta, PERO no tiene techo de radio (los
//   100 m son cortesía de la UI) ni fuerza paridad del suavizado. Por eso el
//   panel pinta lo que llega sin corregirlo.
//
// LO QUE NO SE PUDO PROBAR: la pestaña dibujada en cabina (pantalla táctil,
// teclado nativo, topes de los steppers bajo el dedo). Se verificó el wire, la
// persistencia con reinicio real y la compilación; el uso real va como
// `Prueba:` hasta que alguien la toque en la pantalla.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class UturnTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    // ---- topes del stepper (réplica EXACTA de los listeners del JS) --------
    private const int SmoothMin = 8, SmoothMax = 50, SmoothPaso = 2;
    private const int ExtMin = 3, ExtMax = 50, ExtPaso = 1;

    // ---- rangos de los NUD (réplica EXACTA de rLim/dLim de tabs.uturn.leave)
    private (double min, double max) LimRadio    => C.IsMetric ? (2.0, 100.0) : (6.56, 328.0);
    private (double min, double max) LimDistancia => C.IsMetric ? (0.2, 100.0) : (0.66, 328.0);

    // ---- modelo local (el `ut` de config.js) ------------------------------
    // Suavizado y extensión viven acá porque sus controles son steppers sin
    // campo tipeable; radio y distancia viven en los TextBox.
    private int _smoothing = 14;
    private int _ext = 20;
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: todo queda muerto (anti doble-tap).</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo los TextBox por código (carga inicial,
    /// clamp o repintado post-guardado): ese TextChanged NO es del operario.</summary>
    private bool _cargando;

    private TextBox? _txtRadio, _txtDistancia;
    private TextBlock? _uniRadio, _uniDistancia, _lblSmooth, _lblExt;
    private Button? _btnSmoothDn, _btnSmoothUp, _btnExtDn, _btnExtUp;

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public UturnTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>Sin cambios NO se postea: cada POST de esta sección descarta el
    /// U-turn ya calculado en el motor (ver la cabecera).</summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repinta los cuatro valores desde el snapshot y
    /// descarta cambios sin guardar, igual que el HTML.</summary>
    public override Task AlEntrarAsync()
    {
        _dirty = false;
        Rebuild();
        return Task.CompletedTask;
    }

    /// <summary>`leave()`: valida y guarda si hay cambios. false CANCELA la
    /// navegación — el operario se queda acá con el campo en rojo a la vista.
    /// </summary>
    public override async Task<bool> AlSalirAsync()
    {
        if (!_dirty) return true;
        if (C.Client == null) { C.Estado?.Invoke("Sin conexión con PilotX", "err"); return false; }
        if (_guardando) return false;

        double? radio = LeerNudDec2(_txtRadio, LimRadio);
        double? dist  = LeerNudDec2(_txtDistancia, LimDistancia);
        if (radio == null || dist == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            var r = await C.Client.GuardarAsync("uturn", new
            {
                radius                 = Disp2M(radio.Value),
                distance_from_boundary = Disp2M(dist.Value),
                extension_length       = _ext,
                smoothing              = _smoothing,
            }).ConfigureAwait(true);

            if (r == null)
            {
                C.Estado?.Invoke("Sin conexión con PilotX", "err");
                return false;
            }
            if (!r.Ok)
            {
                C.Estado?.Invoke("Error: " + (string.IsNullOrWhiteSpace(r.Error) ? "desconocido" : r.Error), "err");
                return false;
            }

            _dirty = false;
            C.Estado?.Invoke("Guardado ✔", "ok");

            // Relectura: el motor clampea con SUS reglas (radio sin techo,
            // extensión 3..50, suavizado 8..50) y además reconstruye las líneas
            // de giro. Sin esto el panel mostraría lo que se mandó, no lo que
            // quedó.
            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }
            return true;
        }
        finally
        {
            _guardando = false;
            PintarHabilitado();
        }
    }

    /// <summary>Refresco de fondo (3 s). Esta pestaña tiene campos: reconstruir
    /// abajo del dedo tira el foco y cierra el teclado nativo, así que solo se
    /// rearma si cambió el estado de conexión y nadie está editando.</summary>
    public override void Live()
    {
        if (_estadoPintado == EstadoActual()) return;
        if (_dirty || _guardando) return;
        if (AlgunCampoConFoco()) return;
        Rebuild();
    }

    // =======================================================================
    //  Unidades (utM2disp / utDisp2m del JS — factor 3.28, NO 3.28084)
    // =======================================================================

    private double M2Disp(double m) => C.IsMetric ? m : m * 3.28;
    private double Disp2M(double v) => C.IsMetric ? v : v / 3.28;

    private string Unidad() => C.IsMetric ? "m" : "ft";

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _estadoPintado = EstadoActual();

        // MaxWidth + Left OBLIGATORIOS, las dos cosas: el TabHost cuelga de un
        // ScrollViewer con scroll HORIZONTAL, así que sin tope el panel mide
        // "infinito" (las notas se estiran en una línea larguísima y las cartas
        // se van de la pantalla) y con Stretch un hijo con MaxWidth se CENTRA
        // en el sobrante en vez de arrancar a la izquierda.
        MaxWidth = 686;
        HorizontalAlignment = HorizontalAlignment.Left;

        // Estado del snapshot arriba de todo (mismo criterio que las hermanas).
        if (C.SinDatos)
        {
            Children.Add(CfgUi.Carta(new TextBlock
            {
                Text = PilotX.Cockpit.Bars.Traductor.T("PilotX no responde — todavía no llegaron los datos."),
                Foreground = CfgUi.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            }));
        }
        else if (C.ServicioCaido)
        {
            Children.Add(CfgUi.ChipError("Servicio de configuración no disponible", "AGP-NET-201"));
        }

        var dosCol = CfgUi.Grilla();
        dosCol.HorizontalAlignment = HorizontalAlignment.Left;
        dosCol.Children.Add(CartaGeometria());
        dosCol.Children.Add(CartaSuavizado());
        Children.Add(dosCol);

        PintarValores();
        PintarHabilitado();
    }

    /// <summary>Ancho fijo para que las dos cartas queden lado a lado y, cuando
    /// no entran, la segunda baje entera en vez de encogerse.</summary>
    private static Border Carta(Control contenido)
    {
        var b = CfgUi.Carta(contenido);
        b.Width = 326;
        b.Margin = new Thickness(0, 0, 10, 10);
        return b;
    }

    // ---- carta 1: geometría del giro ---------------------------------------

    private Border CartaGeometria()
    {
        var col = new StackPanel { Spacing = 10 };
        col.Children.Add(CfgUi.Titulo("Geometría del giro"));

        _txtRadio = Nud("Radio");
        _uniRadio = UnidadLbl();
        col.Children.Add(FilaNud("ConU_UturnRadius.png", "Radio", _txtRadio, _uniRadio));

        _txtDistancia = Nud("Distancia al límite");
        _uniDistancia = UnidadLbl();
        col.Children.Add(FilaNud("ConU_UturnDistance.png", "Distancia al límite", _txtDistancia, _uniDistancia));

        return Carta(col);
    }

    /// <summary>El `.nudfila` del HTML: dibujo | etiqueta + campo + unidad. La
    /// etiqueta va ARRIBA del campo (y no a su izquierda como en el HTML ancho)
    /// porque "Distancia al límite" no entra en una carta de 326 px sin
    /// achicar el campo, que es táctil y no se achica.</summary>
    private Control FilaNud(string icono, string etiqueta, TextBox nud, TextBlock unidad)
    {
        var derecha = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        derecha.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(etiqueta),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var campo = CfgUi.Fila(8);
        campo.Children.Add(nud);
        campo.Children.Add(unidad);
        derecha.Children.Add(campo);

        var fila = CfgUi.Fila(10);
        fila.Children.Add(Dibujo(icono, 56, 56));
        fila.Children.Add(derecha);
        return fila;
    }

    /// <summary>El `.nud` del CSS: 130 px, 22 px bold centrado, fondo aliceblue,
    /// alto táctil de 52. Pide el teclado nativo al enfocarse.</summary>
    private TextBox Nud(string titulo)
    {
        var t = new TextBox
        {
            Width = 130, MinHeight = 52,
            FontSize = 22, FontWeight = FontWeight.Bold,
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = BgNud, Foreground = CfgUi.Texto,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 2, 6, 2),
        };
        string tit = titulo;
        t.GotFocus  += (_, __) => _ = C.Client?.TecladoAsync(true, true, tit) ?? Task.CompletedTask;
        t.LostFocus += (_, __) => _ = C.Client?.TecladoAsync(false) ?? Task.CompletedTask;
        t.TextChanged += (_, __) =>
        {
            if (_cargando) return;              // carga/clamp por código, no es del operario
            _dirty = true;
            C.MarcarSucio?.Invoke();
        };
        return t;
    }

    /// <summary>El `span.unidad` del HTML: "m" | "ft" dinámico según is_metric.
    /// Se pinta en PintarValores, no en el footer del shell (el `[data-unidad]`
    /// del footer NO pisa estos dos, igual que en el HTML).</summary>
    private static TextBlock UnidadLbl() => new TextBlock
    {
        Text = "m",
        Foreground = CfgUi.TextoMuted, FontSize = 14, FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 24,
    };

    // ---- carta 2: suavizado y extensión ------------------------------------

    private Border CartaSuavizado()
    {
        var col = new StackPanel { Spacing = 10 };
        col.Children.Add(CfgUi.Titulo("Suavizado y extensión"));

        _lblSmooth = LabelStepper();
        _btnSmoothDn = BotonFlecha("DnArrow64.png", () => PasoSuavizado(-SmoothPaso));
        _btnSmoothUp = BotonFlecha("UpArrow64.png", () => PasoSuavizado(+SmoothPaso));
        col.Children.Add(FilaStepper("ConU_UturnSmooth.png", _btnSmoothDn, _lblSmooth, _btnSmoothUp));
        col.Children.Add(CfgUi.Nota("Suavizado: usar 3 o 4 × radio."));

        _lblExt = LabelStepper();
        _btnExtDn = BotonFlecha("DnArrow64.png", () => PasoExtension(-ExtPaso));
        _btnExtUp = BotonFlecha("UpArrow64.png", () => PasoExtension(+ExtPaso));
        col.Children.Add(FilaStepper("ConU_UturnLength.png", _btnExtDn, _lblExt, _btnExtUp));
        col.Children.Add(CfgUi.Nota("Extensión: usar 2 o 3 × radio."));

        return Carta(col);
    }

    private Control FilaStepper(string icono, Button menos, TextBlock valor, Button mas)
    {
        var fila = CfgUi.Fila(8);
        fila.Children.Add(Dibujo(icono, 56, 56));
        fila.Children.Add(menos);
        fila.Children.Add(valor);
        fila.Children.Add(mas);
        return fila;
    }

    private static TextBlock LabelStepper() => new TextBlock
    {
        Text = "—",
        Foreground = CfgUi.Texto, FontSize = 22, FontWeight = FontWeight.Bold,
        FontFamily = CfgUi.Mono,
        MinWidth = 68, TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>El `.btnZero` del HTML con la flecha: 56×56, táctil.</summary>
    private Button BotonFlecha(string icono, Action alTocar)
    {
        var b = new Button
        {
            Width = 56, Height = 56,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Image
            {
                Source = Icono(icono),
                Width = 34, Height = 34,
                // MaxWidth/MaxHeight EXPLÍCITOS: el Style de BarStyles.axaml
                // limita TODA imagen de la ventana y le gana al Width.
                MaxWidth = 34, MaxHeight = 34,
                Stretch = Stretch.Uniform,
            },
        };
        b.Click += (_, __) => alTocar();
        return b;
    }

    private static Image Dibujo(string nombre, double w, double h) => new Image
    {
        Source = Icono(nombre),
        Width = w, Height = h,
        MaxWidth = w, MaxHeight = h,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // =======================================================================
    //  Steppers (los 4 listeners del JS)
    // =======================================================================

    private void PasoSuavizado(int delta)
    {
        if (_guardando || !Editable()) return;
        int nuevo = Math.Clamp(_smoothing + delta, SmoothMin, SmoothMax);
        if (nuevo == _smoothing) return;        // ya estaba en el tope
        _smoothing = nuevo;
        _dirty = true;
        PintarSteppers();
        C.MarcarSucio?.Invoke();
    }

    private void PasoExtension(int delta)
    {
        if (_guardando || !Editable()) return;
        // El paso es SIEMPRE 1 metro, aunque el rótulo esté en pies (quirk del
        // original: el valor interno vive en metros enteros).
        int nuevo = Math.Clamp(_ext + delta, ExtMin, ExtMax);
        if (nuevo == _ext) return;
        _ext = nuevo;
        _dirty = true;
        PintarSteppers();
        C.MarcarSucio?.Invoke();
    }

    // =======================================================================
    //  Validación (réplica de leerNudDec2)
    // =======================================================================

    /// <summary>
    /// `leerNudDec2(input, min, max)` del JS: acepta coma decimal, marca en rojo
    /// lo que no es número, y para lo válido CLAMPEA a [min,max] y redondea a 2
    /// decimales REESCRIBIENDO el campo, así el operario ve con qué se guardó.
    /// No hay Math.abs: los mínimos positivos se comen los negativos vía clamp.
    /// Divergencia consciente con parseFloat: "12abc" acá es inválido (rojo) en
    /// vez de valer 12 — en una máquina que siembra es mejor preguntar que
    /// adivinar.
    /// </summary>
    private double? LeerNudDec2(TextBox? t, (double min, double max) lim)
    {
        if (t == null) return null;
        string s = (t.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, Inv, out double v)
            || double.IsNaN(v) || double.IsInfinity(v))
        {
            Invalido(t, true);
            return null;
        }
        if (v < lim.min) v = lim.min;
        if (v > lim.max) v = lim.max;
        v = CfgCtx.RedondeoJs(v * 100.0) / 100.0;
        Invalido(t, false);
        SetTexto(t, v.ToString("0.##", Inv));
        return v;
    }

    private static void Invalido(TextBox t, bool mal)
    {
        t.BorderBrush = mal ? CfgUi.Err : CfgUi.Borde;
        t.Background = mal ? BgNudMal : BgNud;
    }

    /// <summary>Escritura por código: no cuenta como cambio del operario.</summary>
    private void SetTexto(TextBox t, string s)
    {
        _cargando = true;
        try { t.Text = s; }
        finally { _cargando = false; }
    }

    // =======================================================================
    //  Pintura
    // =======================================================================

    /// <summary>El `enter()` del JS: los cuatro valores tal cual los manda el
    /// motor (sin clampear — ver la cabecera) y las unidades del display.</summary>
    private void PintarValores()
    {
        var z = C.Snap?.Uturn;
        _smoothing = z?.Smoothing ?? 14;
        _ext = z?.ExtensionLength ?? 20;

        if (_txtRadio != null)
            SetTexto(_txtRadio, z == null ? "" : Dec2(M2Disp(z.Radius)));
        if (_txtDistancia != null)
            SetTexto(_txtDistancia, z == null ? "" : Dec2(M2Disp(z.DistanceFromBoundary)));

        if (_txtRadio != null) Invalido(_txtRadio, false);
        if (_txtDistancia != null) Invalido(_txtDistancia, false);

        string u = Unidad();
        if (_uniRadio != null) _uniRadio.Text = u;
        if (_uniDistancia != null) _uniDistancia.Text = u;

        PintarSteppers();
    }

    /// <summary>`Math.round(v * 100) / 100` con el redondeo de JavaScript, e
    /// impreso como lo imprimiría JS (8.1, no "8.10").</summary>
    private static string Dec2(double v)
        => (CfgCtx.RedondeoJs(v * 100.0) / 100.0).ToString("0.##", Inv);

    /// <summary>`utPintarExt()` del JS: la extensión CON unidad (en imperial,
    /// metros × 3.28 redondeado) y el suavizado SIN unidad.</summary>
    private void PintarSteppers()
    {
        if (_lblSmooth != null) _lblSmooth.Text = _smoothing.ToString(Inv);
        if (_lblExt != null)
            _lblExt.Text = C.IsMetric
                ? _ext.ToString(Inv) + " m"
                : CfgCtx.RedondeoJs(_ext * 3.28).ToString("0", Inv) + " ft";

        // Los topes se ven: el botón que ya no avanza queda apagado. No cambia
        // el comportamiento (el clamp corre igual), solo deja de mentirle al
        // dedo del operario.
        bool editable = Editable() && !_guardando;
        Apagar(_btnSmoothDn, editable && _smoothing > SmoothMin);
        Apagar(_btnSmoothUp, editable && _smoothing < SmoothMax);
        Apagar(_btnExtDn, editable && _ext > ExtMin);
        Apagar(_btnExtUp, editable && _ext < ExtMax);
    }

    private static void Apagar(Button? b, bool vivo)
    {
        if (b == null) return;
        b.IsEnabled = vivo;
        b.Opacity = vivo ? 1.0 : 0.45;
    }

    private void PintarHabilitado()
    {
        bool editable = Editable() && !_guardando;
        foreach (var t in new[] { _txtRadio, _txtDistancia })
        {
            if (t == null) continue;
            t.IsEnabled = editable;
            t.Opacity = editable ? 1.0 : 0.55;
        }
        PintarSteppers();
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor (ni en qué unidad) y
    /// el POST iría al mismo Hub que no contesta: editar a ciegas es peor que no
    /// poder editar.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    private bool AlgunCampoConFoco()
        => (_txtRadio?.IsFocused ?? false)
        || (_txtDistancia?.IsFocused ?? false);

    // =======================================================================
    //  Dibujos
    // =======================================================================

    /// <summary>Los PNG se decodifican UNA vez y se comparten entre rebuilds.
    /// Solo se toca desde el hilo de UI.</summary>
    private static readonly Dictionary<string, Bitmap?> _iconos =
        new Dictionary<string, Bitmap?>(StringComparer.Ordinal);

    private static Bitmap? Icono(string nombre)
    {
        if (_iconos.TryGetValue(nombre, out var cacheado)) return cacheado;
        Bitmap? bmp;
        try { bmp = new Bitmap(AssetLoader.Open(new Uri("avares://PilotX.UI/Assets/config/" + nombre))); }
        catch { bmp = null; }   // falta el asset ⇒ carta sin dibujo, nunca una excepción
        _iconos[nombre] = bmp;
        return bmp;
    }
}
