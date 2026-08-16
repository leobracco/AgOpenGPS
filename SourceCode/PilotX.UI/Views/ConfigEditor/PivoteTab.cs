// ============================================================================
// PivoteTab.cs — pestaña "Implemento › Pivote" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=toolpivot` (la sección data-tab="toolpivot"
// + tabs.toolpivot de config.js).
//
// QUÉ QUEDÓ NATIVO: la carta "Distancia rueda / pivote del implemento" con sus
// dos radios-imagen en TRI-ESTADO, su botón de puesta a cero, el NUD con
// validación y rango, y el guardado (POST /api/aog/config/pivote) con la
// relectura del snapshot.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (Timing, Secciones, Switches, Rumbo, Rolido…).
//
// Qué config toca (la trampa de las DOS configuraciones de implemento):
//   · esta pestaña habla con /api/aog/config — la de PilotX, la que usa el
//     GUIADO y la geometría del implemento (trailingToolToPivotLength),
//     respaldada por tool.json;
//   · NO toca /api/implemento (surcos y semillas/ha de la pantalla de siembra).
//     Son dos configuraciones distintas y NO están sincronizadas: mover el
//     pivote acá no cambia nada de lo que muestra la pantalla de siembra.
//
// Qué es el número: la distancia entre la RUEDA del implemento de arrastre y su
// PIVOTE (el punto que sigue al enganche). Con el signo el motor sabe de qué
// lado de la rueda está el pivote, y con eso arma la cinemática del arrastre:
//   +  = pivote DETRÁS de la rueda   ("Detrás")
//   −  = pivote ADELANTE de la rueda ("Adelante")
//    0 = tri-estado, ningún radio marcado
//
// ---------------------------------------------------------------------------
// DOS QUIRKS DE ORIGEN QUE SE REPLICAN TAL CUAL (no son errores de tipeo):
//
// 1) EL CRUCE DE IMÁGENES. "Adelante" muestra el dibujo …OffsetPos y "Detrás"
//    el …OffsetNeg — o sea, al revés del signo que guardan. Viene del FormConfig
//    original de WinForms, el HTML lo arrastra con un comentario de aviso y el
//    operario ya reconoce cada dibujo por su lugar en la pantalla.
//    "Corregirlo" acá haría que la cabina y el celular muestren dibujos
//    distintos para lo mismo, que es peor que el cruce.
//
// 2) SIN RADIO ⇒ SE GUARDA NEGATIVO. Las pestañas hermanas (Offset, Antena)
//    auto-marcan un radio antes de guardar cuando el valor es ≠ 0; ESTA NO
//    (config.js lo dice con todas las letras: "acá NO se auto-marca un radio —
//    sin selección el valor va negativo"). Consecuencia visible y buscada:
//    poner 50, tocar el botón de cero (que borra la selección), escribir 50 y
//    guardar ⇒ se persiste −0,50 m y al repintar aparece "Adelante" marcado.
//    Copiarle el auto-marcado a las hermanas sería un bug de fidelidad: el
//    mismo gesto guardaría +0,50 y el implemento quedaría armado al revés.
// ---------------------------------------------------------------------------
//
// Otras trampas cubiertas (compartidas con las hermanas):
//   · TRI-ESTADO: con el valor en 0 NO hay NINGÚN radio marcado.
//   · Rango [0,2000] cm / [0,787] in (limPivot del JS, que sale del original
//     tras FixMinMaxSpinners). El BACKEND NO VALIDA NADA: GuardarPivote toma el
//     número tal cual, sin abs ni clamp. Esta validación de UI es la ÚNICA
//     barrera — un bug que mande cm en vez de metros persiste 100× el valor.
//   · Math.round de JavaScript (mitades hacia +infinito) ≠ Math.Round de .NET
//     (banqueros): va CfgCtx.RedondeoJs.
//   · Coma decimal: el operario escribe "2,5" ⇒ replace + InvariantCulture.
//   · Guardar sin cambios no manda nada (guard _dirty): el display redondea a
//     cm enteros, así que un ida y vuelta gratis le comería los decimales finos
//     al motor (0,355 m → 36 cm → 0,36 m). El HTML tiene la misma protección.
//   · tool_offset / tool_overlap viajan en la MISMA sección del snapshot pero
//     son de la pestaña Offset: acá ni se leen ni se mandan (el backend trata
//     lo ausente como "no tocar").
//
// Quirk del HTML que NO se porta (a propósito): config.js pone `tp.dirty =
// false` ANTES del POST, así que un guardado fallido deja el segundo intento
// sin mandar nada y el botón canta "Guardado ✔" sin haber guardado. Acá el
// dirty se limpia SOLO si el POST salió bien.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO Y CON REINICIO DEL MOTOR
// (2026-08-16, banco). El campo SOBREVIVE a matar y volver a levantar
// PilotX.GuidanceEngine: POST {trailing_tool_to_pivot_length:-0.50} →
// `GuidanceEngineData\tool.json` quedó con `"trailing_tool_to_pivot_length":
// -0.5` → taskkill del proceso → arranque limpio → GET /api/aog/config
// devolvió −0.5 (y el panel repintó "Adelante", como manda el quirk 2).
// (El "guardar, releer y comparar" sin reinicio NO prueba nada: BuildSnapshot
// lee Settings.Default EN MEMORIA y devuelve lo que se acaba de mandar.)
//
// QUIÉN persiste de verdad: SOLO tool.json (ToolGeometryStore.Guardar lo
// espeja y Cargar() lo reaplica en el arranque, antes de construir CTool). El
// `Settings.Default.Save()` que dispara cada guardado NO dejó rastro en disco:
// no se creó ni se tocó ningún <Documentos>\AgOpenGPS\Vehicles\<perfil>.XML ni
// ningún user.config, aunque perfil_activo diga "PilotX" (la trampa conocida
// del repo: Settings.Save() es no-op sin perfil real). La red de seguridad es
// UNA sola: si alguien saca ToolGeometryStore o le cambia la carpeta, esta
// pestaña pasa a cantar un "Guardado ✔" que dura hasta el próximo arranque.
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

public sealed class PivoteTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    // ---- rango (réplica EXACTA de limPivot() de config.js) ------------------
    private (double min, double max) LimPivot => C.IsMetric ? (0, 2000) : (0, 787);

    /// <summary>Una opción de radio-imagen: la clave del modelo local, su dibujo
    /// y el rótulo de abajo (el `data-pivote` del HTML).</summary>
    private sealed class Opcion
    {
        public string Clave = "";
        public string Icono = "";
        public string Titulo = "";
    }

    // Mismo orden que el #tpLados del HTML. OJO con los dibujos: el cruce
    // Pos↔Neg es INTENCIONAL y heredado del original (ver quirk 1 de arriba).
    // "Adelante" guarda NEGATIVO y muestra …OffsetPos; "Detrás" guarda POSITIVO
    // y muestra …OffsetNeg. No "acomodar" estos dos nombres.
    private static readonly Opcion[] LADOS =
    {
        new Opcion { Clave = "ahead",  Icono = "ToolHitchPivotOffsetPos.png", Titulo = "Adelante" },
        new Opcion { Clave = "behind", Icono = "ToolHitchPivotOffsetNeg.png", Titulo = "Detrás"   },
    };

    // ---- modelo local (el `tp` de config.js) -------------------------------
    // null = ningún radio marcado. Es un estado VÁLIDO (valor en cero, o el
    // operario tocó el botón de cero), no un "todavía no cargué".
    private string? _pivote;
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: los campos quedan muertos (anti doble-tap
    /// y anti "sigo tipeando mientras se guarda").</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo el TextBox por código (carga, clamp o el
    /// botón de cero): ese TextChanged NO es un cambio del operario.</summary>
    private bool _cargando;

    private TextBox? _txtPivot;
    private readonly Dictionary<string, Border> _radios = new Dictionary<string, Border>(StringComparer.Ordinal);
    private readonly List<Button> _botonesCero = new List<Button>();

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public PivoteTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>El shell lo consulta antes de cantar "Guardado ✔": sin cambios
    /// no se manda nada al motor (y así no se redondea de gusto, ver cabecera).
    /// </summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repinta desde el snapshot (el valor en magnitud y el
    /// radio derivado del SIGNO) y descarta cambios sin guardar, igual que el
    /// HTML.</summary>
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

        double? p = LeerNud(_txtPivot, LimPivot);
        if (p == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        // Quirk 2 de la cabecera: acá NO se auto-marca ningún radio. Sin
        // selección el valor se va NEGATIVO (= pivote adelante), que es lo que
        // hace el original. El repintado posterior (AlEntrarAsync desde el
        // snapshot recién leído) le muestra al operario con qué quedó.
        double pivoteM = _pivote == "behind" ? C.Disp2M(p.Value) : -C.Disp2M(p.Value);

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            var r = await C.Client.GuardarAsync("pivote", new
            {
                trailing_tool_to_pivot_length = pivoteM,
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

            // Relectura: el motor rearma la geometría del implemento EN CALIENTE
            // con el nuevo pivote. Sin esto el resto del panel — y el repintado
            // que muestra el signo con el que realmente quedó — trabajarían con
            // datos viejos.
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

    /// <summary>Refresco de fondo (3 s). Esta pestaña tiene un campo editable:
    /// reconstruir abajo del dedo tira el foco y cierra el teclado nativo, así
    /// que solo se rearma si cambió el estado de conexión y nadie está
    /// tipeando.</summary>
    public override void Live()
    {
        if (_estadoPintado == EstadoActual()) return;
        if (_dirty || _guardando) return;
        if (_txtPivot?.IsFocused ?? false) return;
        Rebuild();
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _radios.Clear();
        _botonesCero.Clear();   // si no, PintarHabilitado seguiría tocando los del árbol viejo
        _estadoPintado = EstadoActual();

        // MaxWidth OBLIGATORIO: el TabHost cuelga de un ScrollViewer con scroll
        // horizontal, así que sin un ancho tope el StackPanel mide "infinito" y
        // nada envuelve.
        _txtPivot = Nud("Distancia rueda / pivote", Magnitud(C.Snap?.Offset?.TrailingToolToPivotLength));

        var carta = new StackPanel { Spacing = 10, MaxWidth = 560 };
        carta.Children.Add(CfgUi.Titulo("Distancia rueda / pivote del implemento"));

        // El aviso de conexión va DENTRO de la carta (igual que las hermanas):
        // en una carta aparte quedaba un segundo título arriba del real y
        // parecía que la pestaña se dibujó dos veces.
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

        carta.Children.Add(CfgUi.Nota("Distancia entre la rueda del implemento de arrastre y su pivote, y de qué lado de la rueda queda el pivote."));

        var g = CfgUi.Grilla();
        foreach (var l in LADOS) g.Children.Add(Radio(l));
        carta.Children.Add(g);

        carta.Children.Add(FilaNud(_txtPivot));
        Children.Add(CfgUi.Carta(carta));

        // Radio derivado del SIGNO que manda el motor (tri-estado: 0 ⇒ ninguno
        // marcado). Nunca de una variable arrastrada de la vez anterior.
        _pivote = PorSigno(C.Snap?.Offset?.TrailingToolToPivotLength);

        PintarRadios();
        PintarHabilitado();
    }

    /// <summary>`> 0` → detrás · `&lt; 0` → adelante · `== 0` (o sin dato) →
    /// null (ningún radio marcado — el tri-estado del original).</summary>
    private static string? PorSigno(double? m)
    {
        if (m == null || double.IsNaN(m.Value)) return null;
        if (m.Value > 0) return "behind";
        if (m.Value < 0) return "ahead";
        return null;
    }

    /// <summary>Valor del snapshot listo para editar SIN signo, en cm|in enteros
    /// (`Math.round(m2disp(Math.abs(p)))` del JS).</summary>
    private string Magnitud(double? m)
    {
        if (m == null || double.IsNaN(m.Value)) return "";
        return CfgCtx.RedondeoJs(C.M2Disp(Math.Abs(m.Value))).ToString("0", Inv);
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

    /// <summary>La `.nudfila` del CSS: botón de cero | NUD | unidad (acá no hay
    /// etiqueta a la izquierda: el título de la carta ya dice qué es, igual que
    /// en el HTML).</summary>
    private Control FilaNud(TextBox nud)
    {
        var fila = CfgUi.Fila(10);
        fila.Children.Add(BotonCero());
        fila.Children.Add(nud);
        fila.Children.Add(new TextBlock
        {
            Text = C.Unidad(),
            Foreground = CfgUi.TextoMuted, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return fila;
    }

    /// <summary>El `.btnZero` del HTML (56×56 con el dibujo SteerZero).</summary>
    private Button BotonCero()
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
                Source = Icono("SteerZero.png"),
                Width = 40, Height = 40,
                // MaxWidth/MaxHeight EXPLÍCITOS: el Style de BarStyles.axaml
                // limita TODA imagen de la ventana a 34 px y le gana al Width.
                MaxWidth = 40, MaxHeight = 40,
                Stretch = Stretch.Uniform,
            },
        };
        b.Click += (_, __) => Cero();
        _botonesCero.Add(b);
        return b;
    }

    /// <summary>Botón de cero: valor 0 y SIN radio elegido. NO guarda (réplica
    /// del HTML): solo deja preparado el valor y marca sucio. Ojo con la
    /// combinación "cero + escribo un número y guardo": sin radio el valor se
    /// persiste NEGATIVO (quirk 2 de la cabecera).</summary>
    private void Cero()
    {
        if (_guardando || !Editable()) return;
        if (_txtPivot != null) SetTexto(_txtPivot, "0");
        _pivote = null;
        _dirty = true;
        PintarRadios();
        C.MarcarSucio?.Invoke();
    }

    /// <summary>La `.radioimg.dircol` del HTML: dibujo + rótulo, con el borde
    /// verde y el fondo claro cuando está elegido.</summary>
    private Border Radio(Opcion o)
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
                Source = Icono(o.Icono),
                Width = 76, Height = 100,
                // MaxWidth/MaxHeight EXPLÍCITOS: ver la nota del botón de cero.
                MaxWidth = 76, MaxHeight = 100,
                Stretch = Stretch.Uniform,
            },
        });

        pila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(o.Titulo),
            Foreground = CfgUi.TextoMuted, FontSize = 11, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var card = new Border
        {
            Width = 120, Height = 152,
            Margin = new Thickness(0, 0, 10, 10),
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };

        string clave = o.Clave;
        card.Tapped += (_, __) => Elegir(clave);

        _radios[o.Clave] = card;
        return card;
    }

    /// <summary>Tap en un radio: solo cambia el modelo local y marca sucio. NO
    /// guarda — el guardado va por el botón Guardar del shell o al salir de la
    /// pestaña, igual que en el HTML.</summary>
    private void Elegir(string pivote)
    {
        if (_guardando || !Editable()) return;
        _pivote = pivote;
        _dirty = true;
        PintarRadios();
        C.MarcarSucio?.Invoke();
    }

    // =======================================================================
    //  Validación (réplica de leerNud sin conSigno ⇒ magnitud)
    // =======================================================================

    /// <summary>
    /// `leerNud(input, min, max)` del JS: acepta coma decimal, marca en rojo lo
    /// que no es número, y para lo válido aplica magnitud + clamp + redondeo a
    /// entero REESCRIBIENDO el campo, así el operario ve con qué se guardó.
    /// Divergencia consciente con parseFloat: "12abc" acá es inválido (rojo) en
    /// vez de valer 12 — en una máquina que siembra es mejor preguntar que
    /// adivinar.
    /// </summary>
    private double? LeerNud(TextBox? t, (double min, double max) lim)
    {
        if (t == null) return null;
        string s = (t.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, Inv, out double v)
            || double.IsNaN(v) || double.IsInfinity(v))
        {
            Invalido(t, true);
            return null;
        }
        v = Math.Abs(v);                       // el signo lo pone el radio
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

    private void PintarRadios()
    {
        bool editable = Editable() && !_guardando;
        foreach (var kv in _radios)
        {
            bool sel = _pivote != null && kv.Key == _pivote;
            kv.Value.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
            kv.Value.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
            kv.Value.Opacity = editable ? 1.0 : 0.55;
            kv.Value.IsHitTestVisible = editable;
        }
    }

    private void PintarHabilitado()
    {
        bool editable = Editable() && !_guardando;
        if (_txtPivot != null)
        {
            _txtPivot.IsEnabled = editable;
            _txtPivot.Opacity = editable ? 1.0 : 0.55;
        }
        foreach (var b in _botonesCero)
        {
            b.IsEnabled = editable;
            b.Opacity = editable ? 1.0 : 0.55;
        }
        PintarRadios();
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor (ni en qué unidad) y
    /// el POST iría al mismo Hub que no contesta: editar a ciegas es peor que no
    /// poder editar.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

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
