// ============================================================================
// AntenaTab.cs — pestaña "Vehículo › Antena" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=vantenna` (la sección data-tab="vantenna" +
// tabs.vantenna de config.js).
//
// QUÉ QUEDÓ NATIVO: el diagrama según el tipo de vehículo, los tres NUD (altura,
// distancia al pivote y offset) con su validación y sus rangos, los tres
// radios-imagen del lado del offset, y el guardado
// (POST /api/aog/config/antena) con la relectura del snapshot.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron.
//
// Unidades: el wire viene SIEMPRE en metros; la UI edita ENTEROS en cm o in
// según is_metric (M2Disp/Disp2M de CfgCtx, réplica de m2disp/disp2m del JS).
//
// ---------------------------------------------------------------------------
// EL SIGNO DEL OFFSET ESTÁ INVERTIDO respecto del implemento. Leer dos veces:
//   antenna_offset:  + = IZQUIERDA   − = DERECHA   (esta pestaña)
//   tool_offset:     + = DERECHA     − = IZQUIERDA (pestaña Offset del implemento)
// Copiar el signo del lado equivocado no "corre un poco" la línea: la corre al
// otro lado, o sea el DOBLE del offset cargado. Con 25 cm de antena eso es medio
// metro de error sostenido en todo el lote. Por eso el lado se pinta desde el
// signo que manda el motor y se vuelve a armar desde el radio elegido, nunca se
// arrastra un signo "de memoria" en una variable.
// ---------------------------------------------------------------------------
//
// Trampas cubiertas:
//   · La altura y el offset se editan en MAGNITUD (el radio pone el signo del
//     offset; la altura no tiene signo y el motor le aplica Math.Abs igual).
//     El PIVOTE sí se edita CON SIGNO: negativo = antena detrás del pivote, y
//     ese signo es información del montaje, no un defecto de tipeo.
//   · Rangos [0,1000] / [−999,999] / [0,500]: quirk heredado del FormConfig
//     original — NO se convierten a pulgadas. En imperial son los MISMOS
//     números. Se replica tal cual: "corregirlo" acá dejaría la cabina y el
//     celular validando distinto contra el mismo motor.
//   · Reglas de lado del Leave original: offset 0 ⇒ Centro (aunque haya otro
//     radio elegido); offset ≠ 0 con Centro elegido ⇒ asume DERECHA y repinta,
//     así el operario ve con qué se guardó.
//   · Tocar "Centro" pone el campo de offset en 0 (réplica del click del HTML).
//   · Math.round de JavaScript (mitades hacia +infinito) ≠ Math.Round de .NET
//     (banqueros): va CfgCtx.RedondeoJs. Con el pivote en −10,5 cm importa: el
//     JS muestra −10 y AwayFromZero mostraría −11.
//   · Coma decimal: el operario escribe "2,5". Se reemplaza por punto y se
//     parsea con InvariantCulture, igual que el `replace(',', '.')` del JS.
//   · Guardar sin cambios no manda nada (guard _dirty): el display redondea a
//     cm enteros, así que un ida y vuelta gratis le comería los decimales finos
//     al motor (0,455 m → 46 cm → 0,46 m). El HTML tiene la misma protección.
//
// Quirk del HTML que NO se porta (a propósito): config.js pone `ant.dirty =
// false` ANTES del POST, así que un guardado fallido deja el segundo intento
// sin mandar nada y el botón canta "Guardado ✔" sin haber guardado. Acá el
// dirty se limpia SOLO si el POST salió bien.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO, no asumida (2026-08-16):
// los tres valores van a `Settings.Default.setVehicle_antennaHeight/
// antennaPivot/antennaOffset` y bajan al perfil de vehículo
// <Documentos>\AgOpenGPS\Vehicles\<perfil>.XML con el `Settings.Default.Save()`
// que dispara cada guardado de config. Se comprobó con el motor headless de
// banco: POST de valores distinguibles → el XML en disco quedó con
// setVehicle_antennaHeight/antennaPivot/antennaOffset en lo posteado, y tras
// MATAR y volver a arrancar el motor el GET /api/aog/config los devolvió
// iguales (CVehicle.cs los relee del Settings en el arranque).
//
// La trampa conocida del repo ("Settings.Save() es no-op sin perfil") NO aplica
// hoy porque `Program.cs` del motor se crea el perfil "PilotX" al arrancar si
// RegistrySettings.vehicleFileName está vacío. La antena NO tiene la red de
// seguridad que tiene la geometría del implemento (tool.json): si alguien saca
// ese bootstrap del perfil, la antena vuelve a fábrica en cada arranque y esta
// pestaña pasaría a cantar un "Guardado ✔" que dura hasta el reinicio.
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

public sealed class AntenaTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    /// <summary>Diagrama por tipo de vehículo (el ANT_IMG del JS).</summary>
    private static string Diagrama(int tipo) => tipo switch
    {
        1 => "AntennaHarvester.png",
        2 => "AntennaArticulated.png",
        _ => "AntennaTractor.png",
    };

    // ---- rangos (réplica EXACTA de LIM_ANT_* de config.js) ------------------
    // OJO: en imperial son los MISMOS números (quirk del original, ver cabecera).
    // MANTENER SINCRONIZADO con config.js: los dos guardan al mismo motor.
    private static readonly (double min, double max) LimAltura = (0, 1000);
    private static readonly (double min, double max) LimPivote = (-999, 999);
    private static readonly (double min, double max) LimOffset = (0, 500);

    /// <summary>Un lado del offset: la clave que viaja en el modelo local, su
    /// dibujo y el rótulo de abajo (el `data-lado` del HTML).</summary>
    private sealed class Lado
    {
        public string Clave = "";
        public string Icono = "";
        public string Titulo = "";
    }

    // Mismo orden que el #antLados del HTML.
    private static readonly Lado[] LADOS =
    {
        new Lado { Clave = "izq",    Icono = "AntennaLeftOffset.png",  Titulo = "Izquierda" },
        new Lado { Clave = "centro", Icono = "AntennaNoOffset.png",    Titulo = "Centro"    },
        new Lado { Clave = "der",    Icono = "AntennaRightOffset.png", Titulo = "Derecha"   },
    };

    // ---- modelo local (el `ant` de config.js) ------------------------------
    private string _lado = "centro";
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: los campos quedan muertos (anti doble-tap
    /// y anti "sigo tipeando mientras se guarda").</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo los TextBox por código (carga, clamp o el
    /// cero que fuerza "Centro"): ese TextChanged NO es un cambio del operario.
    /// </summary>
    private bool _cargando;

    private TextBox? _txtAltura, _txtPivote, _txtOffset;
    private readonly Dictionary<string, Border> _radios = new Dictionary<string, Border>(StringComparer.Ordinal);

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public AntenaTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>El shell lo consulta antes de cantar "Guardado ✔": sin cambios
    /// no se manda nada al motor (y así no se redondea de gusto, ver cabecera).
    /// </summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repinta todo desde el snapshot (diagrama, los tres
    /// valores y el lado derivado del SIGNO del offset) y descarta cambios sin
    /// guardar, igual que el HTML.</summary>
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

        double? h = LeerNud(_txtAltura, LimAltura, conSigno: false);
        double? p = LeerNud(_txtPivote, LimPivote, conSigno: true);   // el pivote SÍ lleva signo
        double? o = LeerNud(_txtOffset, LimOffset, conSigno: false);
        if (h == null || p == null || o == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        // Reglas de lado del Leave original: sin offset no hay lado, y un offset
        // sin lado elegido se asume a la DERECHA. Se repinta antes de guardar
        // para que el operario vea con qué se está guardando.
        if (o.Value == 0) _lado = "centro";
        else if (_lado == "centro") _lado = "der";
        PintarLados();

        // + izquierda / − derecha (ver la advertencia de la cabecera).
        double offsetM = _lado == "izq" ? C.Disp2M(o.Value)
                       : _lado == "der" ? -C.Disp2M(o.Value)
                       : 0.0;

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            var r = await C.Client.GuardarAsync("antena", new
            {
                antenna_height = C.Disp2M(h.Value),
                antenna_pivot  = C.Disp2M(p.Value),   // conserva el signo
                antenna_offset = offsetM,
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

            // Relectura: el motor aplica la antena EN CALIENTE y recalcula
            // geometría. Sin esto el footer y el resto de las pestañas quedarían
            // con datos viejos.
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
    /// rearma si cambió el estado de conexión y nadie está tipeando.</summary>
    public override void Live()
    {
        if (_estadoPintado == EstadoActual()) return;
        if (_dirty || _guardando) return;
        if (AlgunCampoConFoco()) return;
        Rebuild();
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _radios.Clear();
        _estadoPintado = EstadoActual();

        // MaxWidth OBLIGATORIO: el TabHost cuelga de un ScrollViewer con scroll
        // horizontal, así que sin un ancho tope el StackPanel mide "infinito" y
        // NADA envuelve (la nota de la antena dual saldría en una sola línea con
        // barra horizontal).
        var carta = new StackPanel { Spacing = 10, MaxWidth = 560 };
        carta.Children.Add(CfgUi.Titulo("Posición de antena"));

        if (C.SinDatos)
        {
            carta.Children.Add(new TextBlock
            {
                Text = PilotX.Cockpit.Bars.Traductor.T("PilotX no responde — todavía no llegaron los datos."),
                Foreground = CfgUi.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (C.ServicioCaido)
        {
            carta.Children.Add(CfgUi.ChipError("Servicio de configuración no disponible", "AGP-NET-201"));
        }

        // Diagrama del tipo de vehículo activo (se resuelve en cada rebuild: si
        // el operario cambió el tipo en la pestaña "Tipo", acá se refleja).
        carta.Children.Add(new Image
        {
            Source = Icono(Diagrama(C.Snap?.Vehiculo?.VehicleType ?? 0)),
            // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml trae un
            // `Style Selector="Image"` con máximos de 34 px que aplica a TODA
            // imagen de la ventana; sin esto el diagrama sale de estampilla.
            MaxWidth = 420, MaxHeight = 220,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var filas = new StackPanel { Spacing = 8 };

        _txtAltura = Nud("Altura de antena",    Magnitud(C.Snap?.Antena?.AntennaHeight));
        _txtPivote = Nud("Distancia al pivote", ConSigno(C.Snap?.Antena?.AntennaPivot));
        _txtOffset = Nud("Offset",              Magnitud(C.Snap?.Antena?.AntennaOffset));

        filas.Children.Add(FilaNud("Altura de antena", _txtAltura));
        filas.Children.Add(FilaNud("Distancia al pivote", _txtPivote));
        carta.Children.Add(filas);

        Children.Add(CfgUi.Carta(carta));

        // ---- segunda carta: el offset y su lado (el `dosCol` del HTML) ------
        var cartaOffset = new StackPanel { Spacing = 10, MaxWidth = 560 };
        cartaOffset.Children.Add(CfgUi.Titulo("Offset de antena"));
        cartaOffset.Children.Add(FilaNud("Offset", _txtOffset));

        var grilla = CfgUi.Grilla();
        foreach (var l in LADOS) grilla.Children.Add(Radio(l));
        cartaOffset.Children.Add(grilla);

        cartaOffset.Children.Add(CfgUi.Nota("** Antena dual: la posición va a la derecha."));

        Children.Add(CfgUi.Carta(cartaOffset));

        // Lado derivado del SIGNO que manda el motor: + izquierda, − derecha,
        // 0 centro. Nunca de una variable arrastrada de la vez anterior.
        _lado = LadoDeSigno(C.Snap?.Antena?.AntennaOffset);

        PintarLados();
        PintarHabilitado();
    }

    /// <summary>`> 0` → izq · `< 0` → der · `== 0` (o sin dato) → centro.</summary>
    private static string LadoDeSigno(double? offsetM)
    {
        if (offsetM == null || double.IsNaN(offsetM.Value)) return "centro";
        if (offsetM.Value > 0) return "izq";
        if (offsetM.Value < 0) return "der";
        return "centro";
    }

    /// <summary>Valor del snapshot listo para editar SIN signo, en cm|in enteros
    /// (`Math.round(m2disp(Math.abs(v)))` del JS).</summary>
    private string Magnitud(double? m)
    {
        if (m == null || double.IsNaN(m.Value)) return "";
        return CfgCtx.RedondeoJs(C.M2Disp(Math.Abs(m.Value))).ToString("0", Inv);
    }

    /// <summary>Igual pero CONSERVANDO el signo (el pivote: negativo = la antena
    /// va detrás del pivote).</summary>
    private string ConSigno(double? m)
    {
        if (m == null || double.IsNaN(m.Value)) return "";
        return CfgCtx.RedondeoJs(C.M2Disp(m.Value)).ToString("0", Inv);
    }

    /// <summary>El `.nud` del CSS: 130 px, 22 px bold centrado, fondo aliceblue,
    /// alto táctil de 52. Pide el teclado nativo al enfocarse.</summary>
    private TextBox Nud(string titulo, string valor)
    {
        var t = new TextBox
        {
            Text = valor,
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

    /// <summary>La `.nudfila` del CSS: etiqueta | NUD | unidad.</summary>
    private Control FilaNud(string etiqueta, TextBox nud)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("230,Auto,Auto") };

        var lbl = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(etiqueta),
            Foreground = CfgUi.Texto, FontSize = 14, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var uni = new TextBlock
        {
            Text = C.Unidad(),
            Foreground = CfgUi.TextoMuted, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(nud, 1);
        Grid.SetColumn(uni, 2);
        g.Children.Add(lbl);
        g.Children.Add(nud);
        g.Children.Add(uni);
        return g;
    }

    /// <summary>El `.radioimg.ant` del HTML: dibujo + rótulo, con el borde verde
    /// y el fondo claro cuando está elegido.</summary>
    private Border Radio(Lado l)
    {
        var pila = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // El PNG viene con fondo blanco: recuadro blanco para que el radio
        // elegido, que va con fondo verde claro, no muestre un rectángulo suelto.
        pila.Children.Add(new Border
        {
            Background = CfgUi.BgFila,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = new Image
            {
                Source = Icono(l.Icono),
                Width = 64, Height = 70,
                // MaxWidth/MaxHeight EXPLÍCITOS: el Style de BarStyles.axaml
                // limita TODA imagen de la ventana a 34 px y le gana al Width.
                MaxWidth = 64, MaxHeight = 70,
                Stretch = Stretch.Uniform,
            },
        });

        pila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(l.Titulo),
            Foreground = CfgUi.TextoMuted, FontSize = 11, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var card = new Border
        {
            Width = 108, Height = 122,
            Margin = new Thickness(0, 0, 10, 10),
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };

        string clave = l.Clave;
        card.Tapped += (_, __) => Elegir(clave);

        _radios[l.Clave] = card;
        return card;
    }

    /// <summary>Tap en un lado: solo cambia el modelo local y marca sucio. NO
    /// guarda — el guardado va por el botón Guardar del shell o al salir de la
    /// pestaña, igual que en el HTML.</summary>
    private void Elegir(string lado)
    {
        if (_guardando) return;
        if (!Editable()) return;

        _lado = lado;
        // Réplica del click del HTML: elegir "Centro" pone el offset en cero.
        // Va por SetTexto (no cuenta como cambio del operario) y el dirty lo
        // marca este mismo tap.
        if (lado == "centro" && _txtOffset != null) SetTexto(_txtOffset, "0");
        _dirty = true;
        PintarLados();
        C.MarcarSucio?.Invoke();
    }

    // =======================================================================
    //  Validación (réplica de leerNud)
    // =======================================================================

    /// <summary>
    /// `leerNud(input, min, max, conSigno)` del JS: acepta coma decimal, marca en
    /// rojo lo que no es número, y para lo válido aplica (magnitud si no lleva
    /// signo) + clamp + redondeo a entero REESCRIBIENDO el campo, así el
    /// operario ve con qué se guardó.
    /// Divergencia consciente con parseFloat: "12abc" acá es inválido (rojo) en
    /// vez de valer 12 — en una máquina que siembra es mejor preguntar que
    /// adivinar.
    /// </summary>
    private double? LeerNud(TextBox? t, (double min, double max) lim, bool conSigno)
    {
        if (t == null) return null;
        string s = (t.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, Inv, out double v)
            || double.IsNaN(v) || double.IsInfinity(v))
        {
            Invalido(t, true);
            return null;
        }
        if (!conSigno) v = Math.Abs(v);
        if (v < lim.min) v = lim.min;
        if (v > lim.max) v = lim.max;
        v = CfgCtx.RedondeoJs(v);
        Invalido(t, false);
        SetTexto(t, v.ToString("0", Inv));
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

    private void PintarLados()
    {
        bool editable = Editable() && !_guardando;
        foreach (var kv in _radios)
        {
            bool sel = kv.Key == _lado;
            kv.Value.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
            kv.Value.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
            kv.Value.Opacity = editable ? 1.0 : 0.55;
            kv.Value.IsHitTestVisible = editable;
        }
    }

    private void PintarHabilitado()
    {
        bool editable = Editable() && !_guardando;
        foreach (var t in new[] { _txtAltura, _txtPivote, _txtOffset })
        {
            if (t == null) continue;
            t.IsEnabled = editable;
            t.Opacity = editable ? 1.0 : 0.55;
        }
        PintarLados();
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor (ni en qué unidad) y
    /// el POST iría al mismo Hub que no contesta: editar a ciegas es peor que no
    /// poder editar.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    private bool AlgunCampoConFoco()
        => (_txtAltura?.IsFocused ?? false)
        || (_txtPivote?.IsFocused ?? false)
        || (_txtOffset?.IsFocused ?? false);

    // =======================================================================
    //  Dibujos
    // =======================================================================

    /// <summary>Los PNG se decodifican UNA vez y se comparten entre rebuilds
    /// (Rebuild corre en cada entrada a la pestaña y en cada cambio de estado de
    /// conexión). Solo se toca desde el hilo de UI.</summary>
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
