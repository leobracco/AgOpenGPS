// ============================================================================
// TramTab.cs — pestaña "Otros › Tram" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=tram` (la sección data-tab="tram" +
// tabs.tram de config.js, líneas 1496-1533).
//
// QUÉ QUEDÓ NATIVO: la carta única "Trochas (tramlines)" con su dibujo, el NUD
// del ancho de trocha (cm | in ENTEROS) con su unidad dinámica, los dos
// toggles de imagen ("Mostrar control en pantalla" e "Invertir exterior /
// interior") y el guardado (POST /api/aog/config/tram) con relectura del
// snapshot.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — las pestañas que todavía no se portaron y las
// pantallas de CONSTRUCCIÓN de trochas sobre el lote (pages/tramline.html y
// pages/tramlines.html), que son otra cosa: acá solo se guarda la geometría y
// dos preferencias, no se dibuja ninguna huella.
//
// Qué config toca: /api/aog/config sección `tram` — la MISMA superficie que
// usan casi todas las pestañas de este panel. NO toca /api/implemento (surcos y
// semillas/ha de la pantalla de siembra) ni /api/tool (ToolConfigDto camelCase
// del editor de QuantiX). Son tres configuraciones distintas y no están
// sincronizadas.
//
// ---------------------------------------------------------------------------
// ACÁ SE CONFIGURA EL ANCHO DE TROCHA, NO SI EL BOTÓN EXISTE. El toggle que
// muestra u oculta el botón Trochas del menú de campo (`feature_tram`) es de la
// pestaña "Botones", otra sección del wire. No se duplica acá.
//
// Por qué importa el número: el motor decide sobre qué pasada caen las huellas
// con `isOuter = ((int)(tramWidth / toolWidth + 0.5)) % 2 == 0` (CTram.cs). Un
// ancho mal cargado corre las huellas UNA pasada entera y la pulverizadora
// termina pisando cultivo sembrado. Por eso el panel valida con los mismos
// rangos que el HTML y REESCRIBE en el campo el valor con el que se guardó: el
// operario tiene que ver el número final, no el que tipeó.
// ---------------------------------------------------------------------------
//
// Unidades — las del SHELL (CfgCtx.M2Disp/Disp2M), NO las de U-Turn:
//   · el wire viene SIEMPRE en METROS; acá el display es cm | in ENTEROS
//     (×100 / ×39.3701, y de vuelta ×0.01 / ×0.0254 — los factores EXACTOS de
//     m2disp/disp2m del JS);
//   · en métrico el round-trip es exacto; en imperial el redondeo a pulgadas
//     enteras puede mover el valor persistido hasta ~1,2 cm por ciclo de
//     editar-guardar. El HTML ya vive con eso; el guard _dirty (sin cambios no
//     se postea) evita que el drift se acumule solo.
//
// Trampas cubiertas:
//   · Rango de la UI: [1, 10000] cm / [1, 3937] in. El clamp CORRIGE en
//     silencio (no rechaza) y `Math.Abs` se come el signo, igual que leerNud;
//     solo lo que no es número queda en rojo y BLOQUEA el guardado.
//   · El server NO clampea: GuardarTram solo hace Math.Abs, sin mínimo ni
//     máximo. Si el celular u otro cliente dejó tram_width en 0 o en 500 m, el
//     panel PINTA eso tal cual y no lo "corrige" solo: corregirlo al abrir
//     marcaría sucio sin gesto del operario y postearía un recálculo gratis.
//     El clamp corre recién al guardar.
//   · Coma decimal: el operario rioplatense tipea "150,5". Se reemplaza por
//     punto y se parsea con InvariantCulture (`double.Parse` con cultura es-AR
//     convierte 1.5 en 15 EN SILENCIO).
//   · Guardar sin cambios no manda nada (guard _dirty): cada POST dispara
//     IsTramOuterOrInner() en el motor. Es inofensivo, pero no se gatilla gratis
//     al cerrar el panel.
//   · _cargando: en Avalonia TextChanged dispara también al escribir por código
//     (la carga inicial, el clamp que reescribe y el repintado posterior al
//     guardado). Sin el flag, el panel queda sucio apenas se abre y el cierre
//     re-postea.
//
// Quirk del HTML que NO se porta (a propósito): config.js pone `tr.dirty =
// false` ANTES del POST, así que un guardado fallido deja el segundo intento
// sin mandar nada. Acá el dirty se limpia SOLO si el POST salió bien.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO Y CON REINICIO REAL DEL MOTOR
// (banco, 2026-08-16). Método válido: POST → mirar el archivo en disco → matar
// el motor → arranque limpio → GET. ("Guardar y releer" NO prueba nada:
// BuildSnapshot arma el snapshot leyendo Settings EN MEMORIA.)
//   POST /api/aog/config/tram {tram_width 7.77, display_tram_control false,
//   outer_inverted true} → ok
//   G:\Documentos\AgOpenGPS\Vehicles\PilotX.XML quedó con setTram_tramWidth
//   7.77, setTool_isDisplayTramControl False, setTool_isTramOuterInverted True
//   → Stop-Process del motor → arranque limpio → GET /api/aog/config devolvió
//   esos MISMOS tres valores.
//   PERSISTEN LOS 3 DE 3: tram_width · display_tram_control · outer_inverted.
// QUIÉN persiste: el `Settings.Default.Save()` del dispatcher, que en este
// banco SÍ escribe porque hay perfil de vehículo (RegistrySettings
// .vehicleFileName = "PilotX", con su Vehicles\PilotX.XML). tool.json NO tiene
// ninguno de estos 3 campos, así que acá la red de seguridad es UNA sola y es
// el perfil: si el motor arrancara sin perfil elegido, esta pestaña pasaría a
// cantar un "Guardado ✔" que dura hasta el próximo arranque.
//
// CLAMPS DEL SERVER — verificados en el mismo banco, contra el wire:
//   POST {tram_width -3.5} → el GET devolvió 3.5 (Math.Abs y nada más);
//   POST {tram_width 0}    → el GET devolvió 0    (NO hay mínimo);
//   POST {tram_width 999}  → el GET devolvió 999  (NO hay máximo).
// O sea: el rango [1 cm, 100 m] es cortesía EXCLUSIVA de esta UI. Por eso el
// panel pinta lo que llega sin corregirlo y el clamp corre recién al guardar.
//
// LO QUE NO SE PUDO PROBAR: que "Mostrar control en pantalla"
// (isDisplayTramControl) haga algo visible en el cockpit Avalonia — ese setting
// gobernaba un control de la pantalla WinForms. El panel lo PERSISTE igual
// (es el contrato, y el celular y el WinForms alternativo lo siguen usando),
// pero si en cabina "no hace nada" es por eso, no por el porteo. Tampoco se
// probó la pestaña dibujada en cabina (pantalla táctil, teclado nativo) ni que
// las huellas generadas caigan donde corresponde con el ancho nuevo: eso va
// como `Prueba:` hasta que alguien lo mire en el lote.
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

public sealed class TramTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    // ---- rango del NUD (réplica EXACTA de `lim` de tabs.tram.leave) --------
    private (double min, double max) LimAncho => C.IsMetric ? (1.0, 10000.0) : (1.0, 3937.0);

    // ---- modelo local (el `tr` de config.js) ------------------------------
    // El ancho vive en el TextBox, igual que en el HTML.
    private bool _display, _override;
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: todo queda muerto (anti doble-tap).</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo el TextBox por código (carga inicial, clamp
    /// o repintado post-guardado): ese TextChanged NO es del operario.</summary>
    private bool _cargando;

    private TextBox? _txtAncho;
    private TextBlock? _uniAncho;
    private Border? _filaDisplay, _filaOverride;

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public TramTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>Sin cambios NO se postea: cada POST recalcula
    /// IsTramOuterOrInner() en el motor (ver la cabecera).</summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repinta los tres valores desde el snapshot y descarta
    /// cambios sin guardar, igual que el HTML.</summary>
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

        double? ancho = LeerNud(_txtAncho, LimAncho);
        if (ancho == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            var r = await C.Client.GuardarAsync("tram", new
            {
                tram_width           = C.Disp2M(ancho.Value),
                display_tram_control = _display,
                outer_inverted       = _override,
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

            // Relectura: el motor guarda el valor absoluto y recalcula isOuter.
            // Sin esto el panel mostraría lo que se mandó, no lo que quedó.
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

    /// <summary>Refresco de fondo (3 s). Esta pestaña tiene un campo:
    /// reconstruir abajo del dedo tira el foco y cierra el teclado nativo, así
    /// que solo se rearma si cambió el estado de conexión y nadie está
    /// editando.</summary>
    public override void Live()
    {
        if (_estadoPintado == EstadoActual()) return;
        if (_dirty || _guardando) return;
        if (_txtAncho?.IsFocused ?? false) return;
        Rebuild();
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _estadoPintado = EstadoActual();

        // MaxWidth + Left OBLIGATORIOS, las dos cosas: el TabHost cuelga de un
        // ScrollViewer con scroll HORIZONTAL, así que sin tope el panel mide
        // "infinito" y con Stretch un hijo con MaxWidth se CENTRA en el
        // sobrante en vez de arrancar a la izquierda.
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
        else if (SinSeccion)
        {
            Children.Add(CfgUi.Carta(new TextBlock
            {
                Text = PilotX.Cockpit.Bars.Traductor.T(
                    "El motor contestó sin los datos de trochas — no se puede editar sin saber qué tiene cargado."),
                Foreground = CfgUi.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            }));
        }

        Children.Add(CartaTrochas());

        PintarValores();
        PintarHabilitado();
    }

    private Border CartaTrochas()
    {
        var col = new StackPanel { Spacing = 10 };
        col.Children.Add(CfgUi.Titulo("Trochas (tramlines)"));

        // Fila superior: el dibujo del espaciado + la columna de controles. En
        // angosto la columna baja entera (WrapPanel), igual que el flex-wrap
        // del HTML.
        var fila = new WrapPanel { Orientation = Orientation.Horizontal };

        fila.Children.Add(new Image
        {
            Source = Icono("ConT_TramSpacing.png"),
            Width = 120, Height = 120,
            // MaxWidth/MaxHeight EXPLÍCITOS: el Style de BarStyles.axaml limita
            // TODA imagen de la ventana y le gana al Width.
            MaxWidth = 120, MaxHeight = 120,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 8),
        });

        var derecha = new StackPanel { Spacing = 10, Width = 360 };

        _txtAncho = Nud("Ancho de trocha");
        _uniAncho = new TextBlock
        {
            Text = "cm",
            Foreground = CfgUi.TextoMuted, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 24,
        };
        derecha.Children.Add(FilaNud("Ancho de trocha", _txtAncho, _uniAncho));

        _filaDisplay = FilaToggle("ConT_TramOverrideDisplay.png", "Mostrar control en pantalla",
            () => { _display = !_display; });
        derecha.Children.Add(_filaDisplay);

        _filaOverride = FilaToggle("ConT_TramOverride.png", "Invertir exterior / interior",
            () => { _override = !_override; });
        derecha.Children.Add(_filaOverride);

        fila.Children.Add(derecha);
        col.Children.Add(fila);

        var carta = CfgUi.Carta(col);
        carta.MaxWidth = 560;
        carta.HorizontalAlignment = HorizontalAlignment.Left;
        return carta;
    }

    /// <summary>La `.nudfila` del HTML: rótulo, NUD y unidad en una línea.</summary>
    private static Control FilaNud(string etiqueta, TextBox nud, TextBlock unidad)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var lbl = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(etiqueta),
            Foreground = CfgUi.TextoMuted, FontSize = 13, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 150,
            Margin = new Thickness(0, 0, 8, 0),
        };
        nud.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(nud, 1);
        Grid.SetColumn(unidad, 2);
        g.Children.Add(lbl);
        g.Children.Add(nud);
        g.Children.Add(unidad);
        return g;
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

    /// <summary>El `.chkimg.sw` del HTML: fila táctil con el ícono a la
    /// izquierda y el rótulo al lado. `accion` toca SOLO el modelo local y marca
    /// sucio — el guardado va por el botón Guardar del shell o al salir de la
    /// pestaña, igual que en el original.</summary>
    private Border FilaToggle(string icono, string caption, Action accion)
    {
        // Alto FIJO y ancho libre (con tope), como el `style="height:44px"` del
        // HTML: los dos PNG no tienen la misma proporción (160×160 y 274×224) y
        // metidos en una caja cuadrada el apaisado bajaba a 36 px de alto —
        // dos íconos de distinto tamaño uno arriba del otro en la misma carta.
        // MaxWidth/MaxHeight EXPLÍCITOS igual: el Style de BarStyles.axaml
        // limita TODA imagen de la ventana a 34 px y le gana al Height local.
        var img = new Image
        {
            Source = Icono(icono),
            Height = 44,
            MaxWidth = 56, MaxHeight = 44,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var texto = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(caption),
            Foreground = CfgUi.Texto, FontSize = 13, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        var pila = CfgUi.Fila(10);
        pila.Children.Add(img);
        pila.Children.Add(texto);

        var borde = new Border
        {
            MinHeight = 56,
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 4, 10, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };
        // Tapped, NO PointerPressed: el táctil de la cabina manda Tapped y el
        // PointerPressed se dispara también con el arrastre del scroll.
        borde.Tapped += (_, __) =>
        {
            if (_guardando || !Editable()) return;
            accion();
            _dirty = true;
            PintarToggles();
            C.MarcarSucio?.Invoke();
        };
        return borde;
    }

    // =======================================================================
    //  Validación (réplica de leerNud, variante ENTERA sin signo)
    // =======================================================================

    /// <summary>
    /// `leerNud(input, min, max)` del JS (sin `conSigno`): acepta coma decimal,
    /// marca en rojo lo que no es número, y para lo válido hace `Math.abs`,
    /// CLAMPEA a [min,max] y REDONDEA A ENTERO reescribiendo el campo, así el
    /// operario ve con qué se guardó.
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
        v = Math.Abs(v);                        // el negativo se vuelve positivo en silencio
        if (v < lim.min) v = lim.min;
        if (v > lim.max) v = lim.max;
        v = CfgCtx.RedondeoJs(v);               // entero, con el redondeo de JavaScript
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

    /// <summary>El `enter()` del JS: los tres valores tal cual los manda el
    /// motor (sin clampear — ver la cabecera) y la unidad del display.</summary>
    private void PintarValores()
    {
        var z = C.Snap?.Tram;
        _display = z?.DisplayTramControl ?? false;
        _override = z?.OuterInverted ?? false;

        if (_txtAncho != null)
        {
            // Math.round(m2disp(Math.abs(tram_width))) — entero y sin signo.
            // Sin snapshot el campo queda VACÍO: mostrar un 0 inventado sería
            // peor (el operario lo guardaría creyendo que es lo que hay).
            SetTexto(_txtAncho, z?.TramWidth == null
                ? ""
                : CfgCtx.RedondeoJs(C.M2Disp(Math.Abs(z.TramWidth.Value))).ToString("0", Inv));
            Invalido(_txtAncho, false);
        }

        if (_uniAncho != null) _uniAncho.Text = C.Unidad();

        PintarToggles();
    }

    private void PintarToggles()
    {
        PintarSel(_filaDisplay, _display);
        PintarSel(_filaOverride, _override);
    }

    private static void PintarSel(Border? b, bool sel)
    {
        if (b == null) return;
        b.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
        b.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
    }

    private void PintarHabilitado()
    {
        bool editable = Editable() && !_guardando;
        if (_txtAncho != null)
        {
            _txtAncho.IsEnabled = editable;
            _txtAncho.Opacity = editable ? 1.0 : 0.55;
        }
        foreach (var b in new[] { _filaDisplay, _filaOverride })
        {
            if (b == null) continue;
            b.Opacity = editable ? 1.0 : 0.55;
            b.IsHitTestVisible = editable;
        }
    }

    /// <summary>
    /// El snapshot llegó bien pero SIN la sección `tram`. Los dos toggles son
    /// bool: sin sección quedarían pintados en "no" y el primer gesto del
    /// operario postearía display_tram_control=false y outer_inverted=false
    /// PISANDO lo que el motor tiene de verdad (el POST manda siempre los 3
    /// campos). Con la sección ausente no se edita.
    /// </summary>
    private bool SinSeccion => !C.SinDatos && !C.ServicioCaido && C.Snap?.Tram == null;

    /// <summary>Sin snapshot no se sabe qué tiene el motor (ni en qué unidad) y
    /// el POST iría al mismo Hub que no contesta: editar a ciegas es peor que no
    /// poder editar.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido && !SinSeccion;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : SinSeccion ? 3 : 2;

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
