// ============================================================================
// OffsetTab.cs — pestaña "Implemento › Offset" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=tooloffset` (la sección data-tab="tooloffset"
// + tabs.tooloffset de config.js).
//
// QUÉ QUEDÓ NATIVO: las dos cartas ("Offset del implemento" y "Overlap / Gap")
// con su botón de puesta a cero, su NUD con validación y rangos, sus dos
// radios-imagen en TRI-ESTADO, y el guardado
// (POST /api/aog/config/offset_implemento) con la relectura del snapshot.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (Pivote, Timing, Secciones…).
//
// Qué config toca (la trampa de las DOS configuraciones de implemento):
//   · esta pestaña habla con /api/aog/config — la de PilotX, la que usa el
//     GUIADO, el pintado de secciones y el anti-solape (tool_offset /
//     tool_overlap), respaldada por tool.json;
//   · NO toca /api/implemento (surcos y semillas/ha de la pantalla de siembra).
//     Son dos configuraciones distintas y NO están sincronizadas: mover el
//     offset acá no cambia nada de lo que muestra la pantalla de siembra, y al
//     revés tampoco.
//
// Unidades: el wire viene SIEMPRE en metros; la UI edita ENTEROS en cm o in
// según is_metric (M2Disp/Disp2M de CfgCtx, réplica de m2disp/disp2m del JS).
//
// ---------------------------------------------------------------------------
// EL SIGNO DEL OFFSET ESTÁ INVERTIDO respecto del de la antena. Leer dos veces:
//   tool_offset:     + = DERECHA     − = IZQUIERDA (esta pestaña)
//   antenna_offset:  + = IZQUIERDA   − = DERECHA   (pestaña Vehículo › Antena)
// Copiar el signo de la otra pantalla no "corre un poco" el implemento: lo
// corre al otro lado, o sea el DOBLE del offset cargado. Con 25 cm eso es medio
// metro de error sostenido en todo el lote. Por eso el lado se pinta desde el
// signo que manda el motor y se vuelve a armar desde el radio elegido, nunca se
// arrastra un signo "de memoria" en una variable.
// Ídem el overlap: + = OVERLAP (solapa de más) / − = GAP (deja franja sin
// sembrar). Un signo dado vuelta acá se ve recién en la pasada siguiente.
// ---------------------------------------------------------------------------
//
// Trampas cubiertas:
//   · TRI-ESTADO de los radios: con el valor en 0 NO hay NINGÚN radio marcado
//     (réplica exacta del HTML). Simplificarlo a "siempre uno elegido" haría
//     que la cabina y el celular muestren cosas distintas del mismo motor.
//   · Defaults del Leave original, aplicados y REPINTADOS antes de guardar:
//     offset ≠ 0 sin lado ⇒ DERECHA · offset == 0 ⇒ sin lado ·
//     overlap ≠ 0 sin modo ⇒ OVERLAP · overlap == 0 ⇒ sin modo. Se repinta
//     para que el operario VEA con qué se guardó.
//   · Los botones de cero NO guardan: ponen 0 + sacan la selección + marcan
//     sucio. El guardado sigue siendo del botón Guardar o del cambio de
//     pestaña, igual que en el HTML.
//   · Rangos: [0,2500] cm / [0,387] in y [0,1000] cm / [0,155] in. Los máximos
//     imperiales salen del doble-divide del FormConfig original (2500/2.54² y
//     1000/2.54²) — están MAL de origen y se replican TAL CUAL: "arreglarlos"
//     acá dejaría la cabina y el celular validando distinto contra el mismo
//     motor. Si algún día se corrigen, se corrigen en los DOS lados.
//   · Math.round de JavaScript (mitades hacia +infinito) ≠ Math.Round de .NET
//     (banqueros): va CfgCtx.RedondeoJs.
//   · Coma decimal: el operario escribe "2,5". Se reemplaza por punto y se
//     parsea con InvariantCulture, igual que el `replace(',', '.')` del JS.
//   · Guardar sin cambios no manda nada (guard _dirty): el display redondea a
//     cm enteros, así que un ida y vuelta gratis le comería los decimales finos
//     al motor (0,255 m → 26 cm → 0,26 m). El HTML tiene la misma protección.
//   · trailing_tool_to_pivot_length viaja en la MISMA sección del snapshot pero
//     es de la pestaña Pivote: acá ni se lee ni se manda (el backend trata lo
//     ausente como "no tocar").
//
// Quirk del HTML que NO se porta (a propósito): config.js pone `to.dirty =
// false` ANTES del POST, así que un guardado fallido deja el segundo intento
// sin mandar nada y el botón canta "Guardado ✔" sin haber guardado. Acá el
// dirty se limpia SOLO si el POST salió bien.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO Y CON REINICIO DEL MOTOR
// (2026-08-16, banco). Los DOS campos sobreviven a matar y volver a levantar
// PilotX.GuidanceEngine: POST {tool_offset:-0.37, tool_overlap:-0.13} →
// `GuidanceEngineData\tool.json` (al lado del exe del motor) quedó con esos
// mismos números → taskkill del proceso → arranque limpio →
// GET /api/aog/config devolvió {"tool_offset":-0.37,"tool_overlap":-0.13}.
// (El "guardar, releer y comparar" sin reinicio NO prueba nada: BuildSnapshot
// lee Settings.Default EN MEMORIA y devuelve lo que se acaba de mandar.)
//
// QUIÉN persiste de verdad: SOLO tool.json (ToolGeometryStore.Guardar espeja
// tool_offset / tool_overlap y Cargar() los reaplica en el arranque, antes de
// construir CTool/CVehicle). El `Settings.Default.Save()` que dispara cada
// guardado de config NO dejó rastro en disco en este banco: no se creó ni se
// tocó ningún <Documentos>\AgOpenGPS\Vehicles\<perfil>.XML ni ningún
// user.config, aunque perfil_activo diga "PilotX" (la trampa conocida del
// repo: Settings.Save() es no-op sin perfil real). O sea: si alguien saca
// ToolGeometryStore o le cambia la carpeta, esta pestaña pasa a cantar un
// "Guardado ✔" que dura hasta el próximo arranque. La red de seguridad es
// UNA sola, no dos.
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

public sealed class OffsetTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    // ---- rangos (réplica EXACTA de limToolOffset/limToolOverlap de config.js)
    // Los máximos imperiales vienen del doble-divide del original. NO tocar sin
    // tocar también el JS (ver cabecera).
    private (double min, double max) LimOffset  => C.IsMetric ? (0, 2500) : (0, 387);
    private (double min, double max) LimOverlap => C.IsMetric ? (0, 1000) : (0, 155);

    /// <summary>Una opción de radio-imagen: la clave del modelo local, su dibujo
    /// y el rótulo de abajo (el `data-lado` / `data-modo` del HTML).</summary>
    private sealed class Opcion
    {
        public string Clave = "";
        public string Icono = "";
        public string Titulo = "";
    }

    // Mismo orden que el #toLados del HTML.
    private static readonly Opcion[] LADOS =
    {
        new Opcion { Clave = "izq", Icono = "ToolOffsetNegativeLeft.png",  Titulo = "Izquierda" },
        new Opcion { Clave = "der", Icono = "ToolOffsetPositiveRight.png", Titulo = "Derecha"   },
    };

    // Mismo orden que el #toModos del HTML.
    private static readonly Opcion[] MODOS =
    {
        new Opcion { Clave = "overlap", Icono = "ToolOverlap.png", Titulo = "Overlap" },
        new Opcion { Clave = "gap",     Icono = "ToolGap.png",     Titulo = "Gap"     },
    };

    // ---- modelo local (el `to` de config.js) -------------------------------
    // null = ningún radio marcado. Es un estado VÁLIDO (valor en cero), no un
    // "todavía no cargué".
    private string? _lado;
    private string? _modo;
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: los campos quedan muertos (anti doble-tap
    /// y anti "sigo tipeando mientras se guarda").</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo los TextBox por código (carga, clamp o el
    /// botón de cero): ese TextChanged NO es un cambio del operario.</summary>
    private bool _cargando;

    private TextBox? _txtOffset, _txtOverlap;
    private readonly Dictionary<string, Border> _radiosLado = new Dictionary<string, Border>(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _radiosModo = new Dictionary<string, Border>(StringComparer.Ordinal);

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public OffsetTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>El shell lo consulta antes de cantar "Guardado ✔": sin cambios
    /// no se manda nada al motor (y así no se redondea de gusto, ver cabecera).
    /// </summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repinta todo desde el snapshot (los dos valores en
    /// magnitud y los radios derivados del SIGNO) y descarta cambios sin
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

        double? o = LeerNud(_txtOffset,  LimOffset);
        double? v = LeerNud(_txtOverlap, LimOverlap);
        if (o == null || v == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        // Defaults del Leave original. Se repinta ANTES de guardar para que el
        // operario vea con qué se está guardando (un offset cargado sin elegir
        // lado se va a la derecha, y eso tiene que verse).
        if (o.Value != 0 && _lado == null) _lado = "der";
        if (o.Value == 0) _lado = null;
        if (v.Value != 0 && _modo == null) _modo = "overlap";
        if (v.Value == 0) _modo = null;
        PintarRadios();

        // + derecha / − izquierda · + overlap / − gap (ver la cabecera).
        double offsetM  = _lado == "izq" ? -C.Disp2M(o.Value) : C.Disp2M(o.Value);
        double overlapM = _modo == "gap" ? -C.Disp2M(v.Value) : C.Disp2M(v.Value);

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            var r = await C.Client.GuardarAsync("offset_implemento", new
            {
                tool_offset  = offsetM,
                tool_overlap = overlapM,
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

            // Relectura: el motor aplica offset y overlap EN CALIENTE sobre la
            // geometría del implemento. Sin esto el resto del panel quedaría con
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
        _radiosLado.Clear();
        _radiosModo.Clear();
        _botonesCero.Clear();      // si no, PintarHabilitado seguiría tocando los del árbol viejo
        _estadoPintado = EstadoActual();

        // MaxWidth OBLIGATORIO: el TabHost cuelga de un ScrollViewer con scroll
        // horizontal, así que sin un ancho tope el StackPanel mide "infinito" y
        // nada envuelve.
        if (C.SinDatos || C.ServicioCaido)
        {
            var aviso = new StackPanel { Spacing = 10, MaxWidth = 560 };
            aviso.Children.Add(CfgUi.Titulo("Offset del implemento"));
            if (C.SinDatos)
            {
                aviso.Children.Add(new TextBlock
                {
                    Text = PilotX.Cockpit.Bars.Traductor.T("PilotX no responde — todavía no llegaron los datos."),
                    Foreground = CfgUi.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                aviso.Children.Add(CfgUi.ChipError("Servicio de configuración no disponible", "AGP-NET-201"));
            }
            Children.Add(CfgUi.Carta(aviso));
        }

        // ---- carta 1: offset lateral ---------------------------------------
        _txtOffset = Nud("Offset del implemento", Magnitud(C.Snap?.Offset?.ToolOffset));

        var c1 = new StackPanel { Spacing = 10, MaxWidth = 560 };
        c1.Children.Add(CfgUi.Titulo("Offset del implemento"));
        c1.Children.Add(CfgUi.Nota("Corrimiento lateral del implemento respecto del eje del tractor."));
        c1.Children.Add(FilaNud(_txtOffset, () => Cero(_txtOffset, esOffset: true)));

        var g1 = CfgUi.Grilla();
        foreach (var l in LADOS) g1.Children.Add(Radio(l, _radiosLado, ElegirLado));
        c1.Children.Add(g1);
        Children.Add(CfgUi.Carta(c1));

        // ---- carta 2: overlap / gap ----------------------------------------
        _txtOverlap = Nud("Overlap / Gap", Magnitud(C.Snap?.Offset?.ToolOverlap));

        var c2 = new StackPanel { Spacing = 10, MaxWidth = 560 };
        c2.Children.Add(CfgUi.Titulo("Overlap / Gap"));
        c2.Children.Add(CfgUi.Nota("Overlap: las pasadas se solapan. Gap: queda una franja sin tocar entre pasadas."));
        c2.Children.Add(FilaNud(_txtOverlap, () => Cero(_txtOverlap, esOffset: false)));

        var g2 = CfgUi.Grilla();
        foreach (var m in MODOS) g2.Children.Add(Radio(m, _radiosModo, ElegirModo));
        c2.Children.Add(g2);
        Children.Add(CfgUi.Carta(c2));

        // Radios derivados del SIGNO que manda el motor (tri-estado: 0 ⇒ ninguno
        // marcado). Nunca de una variable arrastrada de la vez anterior.
        _lado = PorSigno(C.Snap?.Offset?.ToolOffset,  "der", "izq");
        _modo = PorSigno(C.Snap?.Offset?.ToolOverlap, "overlap", "gap");

        PintarRadios();
        PintarHabilitado();
    }

    /// <summary>`> 0` → positivo · `&lt; 0` → negativo · `== 0` (o sin dato) →
    /// null (ningún radio marcado — el tri-estado del original).</summary>
    private static string? PorSigno(double? m, string positivo, string negativo)
    {
        if (m == null || double.IsNaN(m.Value)) return null;
        if (m.Value > 0) return positivo;
        if (m.Value < 0) return negativo;
        return null;
    }

    /// <summary>Valor del snapshot listo para editar SIN signo, en cm|in enteros
    /// (`Math.round(m2disp(Math.abs(v)))` del JS).</summary>
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

    /// <summary>La `.nudfila` del CSS de esta pestaña: botón de cero | NUD |
    /// unidad (acá no hay etiqueta a la izquierda: el título de la carta ya dice
    /// qué es, igual que en el HTML).</summary>
    private Control FilaNud(TextBox nud, Action alCero)
    {
        var fila = CfgUi.Fila(10);
        fila.Children.Add(BotonCero(alCero));
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
    private Button BotonCero(Action alCero)
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
        b.Click += (_, __) => alCero();
        _botonesCero.Add(b);
        return b;
    }

    private readonly List<Button> _botonesCero = new List<Button>();

    /// <summary>Botón de cero: valor 0 y SIN radio elegido. NO guarda (réplica
    /// del HTML): solo deja preparado el valor y marca sucio.</summary>
    private void Cero(TextBox? t, bool esOffset)
    {
        if (_guardando || !Editable()) return;
        if (t != null) SetTexto(t, "0");
        if (esOffset) _lado = null; else _modo = null;
        _dirty = true;
        PintarRadios();
        C.MarcarSucio?.Invoke();
    }

    /// <summary>La `.radioimg.dircol` del HTML: dibujo + rótulo, con el borde
    /// verde y el fondo claro cuando está elegido.</summary>
    private Border Radio(Opcion o, Dictionary<string, Border> destino, Action<string> alElegir)
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
        card.Tapped += (_, __) => alElegir(clave);

        destino[o.Clave] = card;
        return card;
    }

    /// <summary>Tap en un lado: solo cambia el modelo local y marca sucio. NO
    /// guarda — el guardado va por el botón Guardar del shell o al salir de la
    /// pestaña, igual que en el HTML.</summary>
    private void ElegirLado(string lado)
    {
        if (_guardando || !Editable()) return;
        _lado = lado;
        _dirty = true;
        PintarRadios();
        C.MarcarSucio?.Invoke();
    }

    private void ElegirModo(string modo)
    {
        if (_guardando || !Editable()) return;
        _modo = modo;
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
        Pintar(_radiosLado, _lado, editable);
        Pintar(_radiosModo, _modo, editable);
    }

    private static void Pintar(Dictionary<string, Border> radios, string? elegido, bool editable)
    {
        foreach (var kv in radios)
        {
            bool sel = elegido != null && kv.Key == elegido;
            kv.Value.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
            kv.Value.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
            kv.Value.Opacity = editable ? 1.0 : 0.55;
            kv.Value.IsHitTestVisible = editable;
        }
    }

    private void PintarHabilitado()
    {
        bool editable = Editable() && !_guardando;
        foreach (var t in new[] { _txtOffset, _txtOverlap })
        {
            if (t == null) continue;
            t.IsEnabled = editable;
            t.Opacity = editable ? 1.0 : 0.55;
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

    private bool AlgunCampoConFoco()
        => (_txtOffset?.IsFocused ?? false)
        || (_txtOverlap?.IsFocused ?? false);

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
