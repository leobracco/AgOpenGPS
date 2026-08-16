// ============================================================================
// TimingTab.cs — pestaña "Implemento › Timing" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=tsettings` (la sección data-tab="tsettings"
// + tabs.tsettings de config.js).
//
// QUÉ QUEDÓ NATIVO: la carta "Tiempos de anticipación de secciones" con sus
// tres columnas (dibujo + NUD + rótulo), la regla XOR en vivo entre "Apagado" y
// "Retardo", el tope `apagado ≤ 0,8 × encendido`, la validación con rangos y el
// guardado (POST /api/aog/config/timing) con la relectura del snapshot.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (Secciones, Switches, Máquina, Rumbo, Rolido, U-Turn, Tram).
//
// Qué config toca (la trampa de las DOS configuraciones de implemento):
//   · esta pestaña habla con /api/aog/config — la de PilotX, la que usa el
//     GUIADO y el corte de secciones. Los tres valores viven en
//     Settings.setVehicle_toolLookAheadOn / _toolLookAheadOff / _toolOffDelay y
//     además bajan a tool.json por ToolGeometryStore;
//   · NO toca /api/implemento (surcos y semillas/ha de la pantalla de siembra):
//     cambiar el timing acá no mueve nada de lo que muestra esa pantalla, y
//     tampoco al revés (ImplementoDto no lleva copia del look-ahead, así que
//     este es el ÚNICO lugar que los escribe — a diferencia de Pivote/Offset/
//     Distancias, que el implemento central pisa en cada guardado suyo).
//
// Qué son los tres números, en criollo:
//   · ENCENDIDO (s): con cuánta anticipación prende la sección ANTES de llegar
//     a lo no sembrado. Corto = franja sin sembrar en cada entrada.
//   · APAGADO (s): con cuánta anticipación corta ANTES de pisar lo ya sembrado.
//     Corto = doble siembra en la cabecera.
//   · RETARDO DE APAGADO (s): cuánto SIGUE aplicando después de pisar sembrado
//     (lo contrario del anterior). Por eso son excluyentes.
// Aplican EN CALIENTE (sin reiniciar el motor) y mandan el corte real que
// después ejecutan SectionX/QuantiX: un valor mal cargado se paga en el lote.
//
// ---------------------------------------------------------------------------
// SEGUNDOS PUROS — ACÁ NO HAY CONVERSIÓN DE UNIDADES. Es la única pestaña de
// config que NO mira is_metric: el wire manda segundos y la UI muestra segundos.
// No meterle m2disp/disp2m "por consistencia" con las hermanas: multiplicaría
// por 100 el look-ahead y el corte se iría a la loma del orto.
// ---------------------------------------------------------------------------
//
// Trampas cubiertas:
//   · XOR EN VIVO, direccional: tipear un valor > 0 en Apagado pone Retardo en
//     0 (y al revés) EN EL MOMENTO, no al guardar. En el HTML el listener es de
//     `input` (solo gestos del operario); en Avalonia TextChanged también salta
//     con los seteos por código, así que TODA escritura programática va por
//     SetTexto con el flag _cargando: sin eso, el 0 que ponemos en un campo
//     dispararía el handler del otro y se borrarían mutuamente al cargar.
//   · La REGLA DURA vive en el backend: si igual llegan los dos > 0, el POST
//     devuelve ok:false "off-y-delay-excluyentes". Se muestra en criollo y la
//     navegación NO avanza: el operario corrige y reintenta.
//   · Tope `off ≤ 0,8 × on`: se aplica acá REESCRIBIENDO el campo (así se ve
//     con qué se guardó) y también lo aplica el motor — pero el motor NO
//     redondea (0,88) y la UI sí (0,9). Por eso el guardado relee el snapshot:
//     lo que queda en pantalla es lo que el motor tiene de verdad, y no lo que
//     la cabina mostraría distinto del celular.
//   · Rangos on [0,2 · 22] · off [0 · 20] · delay [0 · 10] (los mismos del JS y
//     del backend). Se clampea en silencio y se redondea a 1 decimal.
//   · Coma decimal: el operario escribe "1,5" ⇒ replace + InvariantCulture en
//     los dos sentidos. Un double.Parse con cultura es-AR convierte 1.5 en 15.
//   · El motor devuelve el clamp SIN redondear y en coma flotante cruda: con
//     on = 1,1 el GET trae look_ahead_off = 0.8800000000000001 (verificado en
//     banco). El HTML lo escupe entero en el campo; acá se muestra "0.88"
//     (formato 0.####). Divergencia consciente: 17 dígitos en un campo de 130 px
//     no son información, son ruido — y el valor que manda el motor no cambia.
//   · Guardar sin cambios no manda nada (guard _dirty): el redondeo a 1 decimal
//     le comería los decimales finos que el motor puede tener del clamp 0,8×
//     (un 0,88 volvería como 0,9).
//
// Quirk del HTML que NO se porta (a propósito): config.js pone `ts.dirty =
// false` ANTES del POST, así que un guardado fallido deja el segundo intento
// sin mandar nada. Acá el dirty se limpia SOLO si el POST salió bien.
//
// Los GIF animados del HTML (SectionOnLookAhead.gif y compañía) se portaron
// como PNG del PRIMER cuadro: Avalonia no anima GIF y no se agrega una librería
// por tres dibujitos didácticos. El cuadro elegido ya muestra la situación
// (tractor, línea y sección de color); la animación no aportaba nada operativo.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO CON REINICIO DEL MOTOR
// (2026-08-16): POST de valores distinguibles (on 3,4 / off 0 / delay 2,7) →
// aparecieron en <Documentos>\AgOpenGPS\Vehicles\PilotX.XML
// (setVehicle_toolLookAheadOn/_toolLookAheadOff/_toolOffDelay) y en tool.json
// (look_ahead_on / look_ahead_off / turn_off_delay) → se MATÓ el motor
// (taskkill) → arranque limpio → GET /api/aog/config devolvió los mismos tres
// valores. Los tres campos persisten. Doble red: aunque Settings.Save() sea
// no-op sin perfil, ToolGeometryStore.Guardar() los baja igual a tool.json.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class TimingTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    // ---- rangos (réplica EXACTA de tabs.tsettings de config.js, que a su vez
    // son los del backend GuardarTiming). MANTENER SINCRONIZADO: cabina, celular
    // y motor validan contra el mismo número.
    private static readonly (double min, double max) LimOn    = (0.2, 22.0);
    private static readonly (double min, double max) LimOff   = (0.0, 20.0);
    private static readonly (double min, double max) LimDelay = (0.0, 10.0);

    // ---- modelo local (el `ts` de config.js) -------------------------------
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: los campos quedan muertos (anti doble-tap
    /// y anti "sigo tipeando mientras se guarda").</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo los TextBox por código (carga, clamp o el
    /// cero del XOR): ese TextChanged NO es un cambio del operario y NO tiene
    /// que disparar el XOR del otro campo.</summary>
    private bool _cargando;

    private TextBox? _txtOn, _txtOff, _txtDelay;

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public TimingTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repinta los tres valores TAL CUAL vienen del wire y
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

        double? on    = LeerNudDec(_txtOn,    LimOn);
        double? off   = LeerNudDec(_txtOff,   LimOff);
        double? delay = LeerNudDec(_txtDelay, LimDelay);
        if (on == null || off == null || delay == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        // Tope del original: el apagado no puede pasar de 0,8 × encendido. Se
        // reescribe el campo para que el operario VEA el valor corregido (el
        // motor vuelve a clampear, sin redondear — ver cabecera).
        if (off.Value > on.Value * 0.8)
        {
            off = CfgCtx.RedondeoJs(on.Value * 0.8 * 10.0) / 10.0;
            if (_txtOff != null) SetTexto(_txtOff, Num(off.Value));
        }

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            var r = await C.Client.GuardarAsync("timing", new
            {
                look_ahead_on  = on.Value,
                look_ahead_off = off.Value,
                turn_off_delay = delay.Value,
            }).ConfigureAwait(true);

            if (r == null)
            {
                C.Estado?.Invoke("Sin conexión con PilotX", "err");
                return false;
            }
            if (!r.Ok)
            {
                // La regla dura del motor, en criollo. NO se "arregla" pisando
                // un campo por atrás: quedaría desincronizado lo que se ve de lo
                // que se posteó.
                string msg = (r.Error ?? "") == "off-y-delay-excluyentes"
                    ? "Apagado y Retardo no pueden usarse a la vez"
                    : "Error: " + (string.IsNullOrWhiteSpace(r.Error) ? "desconocido" : r.Error);
                C.Estado?.Invoke(msg, "err");
                return false;
            }

            _dirty = false;
            C.Estado?.Invoke("Guardado ✔", "ok");

            // Relectura OBLIGATORIA: el motor aplica su propio tope 0,8× SIN
            // redondear. Sin esto la cabina mostraría 0,9 y el celular 0,88.
            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }
            PintarValores();
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
        _estadoPintado = EstadoActual();

        // MaxWidth OBLIGATORIO: el TabHost cuelga de un ScrollViewer con scroll
        // horizontal, así que sin un ancho tope el StackPanel mide "infinito" y
        // NADA envuelve (las tres columnas saldrían en fila con barra abajo).
        var carta = new StackPanel { Spacing = 10, MaxWidth = 620 };
        carta.Children.Add(CfgUi.Titulo("Tiempos de anticipación de secciones"));

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

        _txtOn    = Nud("Encendido (s)");
        _txtOff   = Nud("Apagado (s)");
        _txtDelay = Nud("Retardo de apagado (s)");
        Cablear();   // recién acá: cada handler del XOR necesita al otro campo

        // Las tres columnas del `.timingCols` (flex-wrap): en WrapPanel se
        // apilan solas cuando la pantalla es angosta (10").
        var cols = CfgUi.Grilla();
        cols.Children.Add(Columna("SectionOnLookAhead.png",    _txtOn,    "Encendido (s)"));
        cols.Children.Add(Columna("SectionLookAheadOff.png",   _txtOff,   "Apagado (s)"));
        cols.Children.Add(Columna("SectionLookAheadDelay.png", _txtDelay, "Retardo de apagado (s)"));
        carta.Children.Add(cols);

        carta.Children.Add(CfgUi.Nota(
            "«Apagado» y «Retardo» son excluyentes: al usar uno el otro vuelve a 0. "
            + "El apagado no puede superar 0,8 × encendido."));

        Children.Add(CfgUi.Carta(carta));

        PintarValores();
        PintarHabilitado();
    }

    /// <summary>Una `.timingCol` del HTML: dibujo arriba, NUD al medio, rótulo
    /// de unidad abajo.</summary>
    private Control Columna(string icono, TextBox nud, string rotulo)
    {
        var pila = new StackPanel
        {
            Spacing = 8, Width = 190,
            Margin = new Thickness(0, 0, 12, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // El PNG viene con fondo blanco: recuadro blanco con borde suave para
        // que no quede un rectángulo suelto sobre la carta.
        pila.Children.Add(new Border
        {
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image
            {
                Source = Icono(icono),
                Width = 150, Height = 200,
                // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml trae un
                // `Style Selector="Image"` con máximos de 34 px que aplica a
                // TODA imagen de la ventana y le gana al Width.
                MaxWidth = 150, MaxHeight = 200,
                Stretch = Stretch.Uniform,
            },
        });

        nud.HorizontalAlignment = HorizontalAlignment.Center;
        pila.Children.Add(nud);

        pila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(rotulo),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });

        return pila;
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
        return t;
    }

    /// <summary>Engancha el XOR + el dirty. Se hace DESPUÉS de crear los tres
    /// campos porque cada handler necesita al otro campo ya existente.</summary>
    private void Cablear()
    {
        if (_txtOn != null)
            _txtOn.TextChanged += (_, __) => { if (!_cargando) Ensuciar(); };

        if (_txtOff != null)
            _txtOff.TextChanged += (_, __) =>
            {
                if (_cargando) return;
                Ensuciar();
                // XOR del original: usar Apagado pone Retardo en 0.
                if (Positivo(_txtOff) && _txtDelay != null) SetTexto(_txtDelay, "0");
            };

        if (_txtDelay != null)
            _txtDelay.TextChanged += (_, __) =>
            {
                if (_cargando) return;
                Ensuciar();
                // XOR espejo: usar Retardo pone Apagado en 0.
                if (Positivo(_txtDelay) && _txtOff != null) SetTexto(_txtOff, "0");
            };
    }

    private void Ensuciar()
    {
        _dirty = true;
        C.MarcarSucio?.Invoke();
    }

    /// <summary>`parseFloat(v.replace(',', '.')) > 0` del JS: lo que no parsea
    /// no dispara el XOR (borrar el campo no toca al otro).</summary>
    private static bool Positivo(TextBox? t)
    {
        if (t == null) return false;
        string s = (t.Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, Inv, out double v)
               && !double.IsNaN(v) && v > 0;
    }

    // =======================================================================
    //  Validación (réplica de leerNudDec)
    // =======================================================================

    /// <summary>
    /// `leerNudDec(input, min, max)` del JS: acepta coma decimal, marca en rojo
    /// lo que no es número, y para lo válido clampea al rango y redondea a UN
    /// decimal REESCRIBIENDO el campo, así el operario ve con qué se guardó.
    /// A diferencia de leerNud (enteros) NO hace Math.abs — pero los mínimos son
    /// ≥ 0, así que el clamp ya se come los negativos.
    /// Divergencia consciente con parseFloat: "1,5s" acá es inválido (rojo) en
    /// vez de valer 1,5 — en una máquina que siembra es mejor preguntar que
    /// adivinar.
    /// </summary>
    private double? LeerNudDec(TextBox? t, (double min, double max) lim)
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
        // Math.round(v*10)/10 del JS (mitades hacia +infinito), NO el Math.Round
        // de .NET (banqueros): con 0,25 el JS da 0,3 y .NET daría 0,2.
        v = CfgCtx.RedondeoJs(v * 10.0) / 10.0;
        Invalido(t, false);
        SetTexto(t, Num(v));
        return v;
    }

    private static void Invalido(TextBox t, bool mal)
    {
        t.BorderBrush = mal ? CfgUi.Err : CfgUi.Borde;
        t.Background = mal ? BgNudMal : BgNud;
    }

    /// <summary>Escritura por código: no cuenta como cambio del operario y NO
    /// dispara el XOR del otro campo (ver cabecera).</summary>
    private void SetTexto(TextBox t, string s)
    {
        bool antes = _cargando;
        _cargando = true;
        try { t.Text = s; }
        finally { _cargando = antes; }
    }

    /// <summary>Número como lo imprimiría JavaScript, SIEMPRE invariante
    /// (1 → "1", 1.5 → "1.5"). El punto es el separador del wire.</summary>
    private static string Num(double v) => v.ToString("0.####", Inv);

    // =======================================================================
    //  Pintura
    // =======================================================================

    /// <summary>`enter()`: los tres valores tal cual del snapshot. Va todo por
    /// SetTexto: si no, el 0 de un campo dispararía el XOR del otro y se
    /// borrarían mutuamente apenas se abre la pestaña.</summary>
    private void PintarValores()
    {
        var tm = C.Snap?.Timing;
        if (_txtOn    != null) { SetTexto(_txtOn,    Val(tm?.LookAheadOn));  Invalido(_txtOn, false); }
        if (_txtOff   != null) { SetTexto(_txtOff,   Val(tm?.LookAheadOff)); Invalido(_txtOff, false); }
        if (_txtDelay != null) { SetTexto(_txtDelay, Val(tm?.TurnOffDelay)); Invalido(_txtDelay, false); }
    }

    private static string Val(double? v)
        => v == null || double.IsNaN(v.Value) ? "" : Num(v.Value);

    private void PintarHabilitado()
    {
        bool editable = Editable() && !_guardando;
        foreach (var t in new[] { _txtOn, _txtOff, _txtDelay })
        {
            if (t == null) continue;
            t.IsEnabled = editable;
            t.Opacity = editable ? 1.0 : 0.55;
        }
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor y el POST iría al
    /// mismo Hub que no contesta: editar a ciegas es peor que no poder editar.
    /// </summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    private bool AlgunCampoConFoco()
        => (_txtOn?.IsFocused ?? false)
        || (_txtOff?.IsFocused ?? false)
        || (_txtDelay?.IsFocused ?? false);

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
