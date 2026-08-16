// ============================================================================
// DimensionesTab.cs — pestaña "Vehículo › Dimensiones" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=vdimensions` (la sección data-tab="vdimensions"
// + tabs.vdimensions de config.js).
//
// QUÉ QUEDÓ NATIVO: el diagrama según el tipo de vehículo, los tres NUD (entre
// ejes, trocha y distancia del eje rígido al enganche) con su validación y sus
// rangos, y el guardado (POST /api/aog/config/dimensiones) con la relectura del
// snapshot.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron.
//
// Unidades: el wire viene SIEMPRE en metros; la UI edita ENTEROS en cm o in
// según is_metric (M2Disp/Disp2M de CfgCtx, réplica de m2disp/disp2m del JS).
//
// Trampas cubiertas:
//   · El enganche se muestra SIEMPRE en magnitud: el motor guarda el signo
//     (− = atrás) y lo decide él solo según el estilo de implemento. Duplicar
//     esa lógica acá sería inventar contrato y una fuente segura de bugs.
//   · La fila del enganche se ve SOLO con implemento TBT o de arrastre, igual
//     que el original; cuando se oculta aparece la nota que dice dónde se
//     configura. Aunque esté oculta, su valor se valida y se manda igual (es lo
//     que hace el JS: el campo conserva lo que cargó `enter()`).
//   · Rangos: réplica de limWheelbase/limTrack/limHitch de config.js — si
//     alguien cambia los del JS hay que cambiar ESTOS también, o la cabina y el
//     celular van a guardar cosas distintas.
//   · Math.round de JavaScript (mitades hacia +infinito) ≠ Math.Round de .NET
//     (banqueros): va CfgCtx.RedondeoJs.
//   · Coma decimal: el operario escribe "2,5". Se reemplaza por punto y se
//     parsea con InvariantCulture, igual que el `replace(',', '.')` del JS.
//
// Quirk del HTML que NO se porta (a propósito): config.js pone `dim.dirty =
// false` ANTES del POST, así que un guardado fallido deja el segundo intento
// sin mandar nada y el botón canta "Guardado ✔" sin haber guardado. Acá el
// dirty se limpia SOLO si el POST salió bien.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — LEER ESTO ANTES DE PROMETERLE NADA AL OPERARIO (2026-08-16):
// de los tres campos de esta pestaña, al motor headless le sobrevive al
// reinicio UNO SOLO:
//   · hitch_length  → SÍ persiste (ToolGeometryStore lo espeja en tool.json);
//   · wheelbase     → NO persiste;
//   · track_width   → NO persiste.
// El motor los escribe en `Properties.Settings`, pero su `Save()` es un no-op
// mientras no haya perfil de vehículo elegido (vehicle_file_name = "", el caso
// normal headless) y `tool.json` solo espeja la geometría del IMPLEMENTO. En
// caliente los dos andan (el guiado ya trabaja con los valores nuevos); al
// reiniciar el motor vuelven a los del código. Es un agujero del back-end
// (mismo que sufre la página HTML: NO lo introduce este porteo) y se arregla
// agregando esos dos campos a ToolGeometryStore. Hasta entonces esta pestaña
// no puede cantar "queda guardado para siempre" — y por eso no lo dice.
// ============================================================================

using System;
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

public sealed class DimensionesTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    /// <summary>Diagrama por tipo de vehículo (el DIM_IMG del JS).</summary>
    private static string Diagrama(int tipo) => tipo switch
    {
        1 => "RadiusWheelBaseHarvester.png",
        2 => "RadiusWheelBaseArticulated.png",
        _ => "RadiusWheelBase.png",
    };

    // ---- rangos (réplica EXACTA de limWheelbase/limTrack/limHitch) ----------
    // Son los del original WinForms tras FixMinMaxSpinners, en cm | in.
    // MANTENER SINCRONIZADO con config.js: los dos guardan al mismo motor.
    private (double min, double max) LimWheelbase() => C.IsMetric ? (50, 1999) : (20, 787);
    private (double min, double max) LimTrack()     => C.IsMetric ? (20, 2000) : (8, 787);
    private (double min, double max) LimHitch()     => C.IsMetric ? (0, 4000)  : (0, 1575);

    // ---- modelo local (el `dim` de config.js) ------------------------------
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: los campos quedan muertos (anti doble-tap
    /// y anti "sigo tipeando mientras se guarda").</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo los TextBox por código (carga o clamp): el
    /// TextChanged de esos cambios NO es un cambio del operario.</summary>
    private bool _cargando;

    private TextBox? _txtWheelbase, _txtTrack, _txtHitch;
    private Control? _filaHitch;
    private TextBlock? _notaHitchOculto;

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public DimensionesTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>El shell lo consulta antes de cantar "Guardado ✔": sin cambios
    /// no se manda nada al motor.</summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repinta todo desde el snapshot (diagrama, valores,
    /// visibilidad del enganche) y descarta cambios sin guardar, igual que el
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

        // Los TRES se validan siempre, incluso el enganche oculto: conserva el
        // valor que cargó AlEntrarAsync, así que valida OK y se reenvía igual
        // (mismo comportamiento que el JS).
        double? wb = LeerNud(_txtWheelbase, LimWheelbase());
        double? tr = LeerNud(_txtTrack,     LimTrack());
        double? hi = LeerNud(_txtHitch,     LimHitch());
        if (wb == null || tr == null || hi == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            // METROS y magnitud sin signo: el motor le pone el − al enganche si
            // el implemento no es frontal.
            var r = await C.Client.GuardarAsync("dimensiones", new
            {
                wheelbase    = C.Disp2M(wb.Value),
                track_width  = C.Disp2M(tr.Value),
                hitch_length = C.Disp2M(hi.Value),
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

            // Relectura: el guardado recalcula geometría en el motor (reparte el
            // ancho de secciones, actualiza el tram) y puede devolver el
            // enganche con otro signo. Sin esto el footer y el resto de las
            // pestañas quedarían con datos viejos.
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
    /// rearma si cambió el estado de conexión (los campos pasan de apagados a
    /// editables) y nadie está tipeando.</summary>
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
        // NADA envuelve — la nota del enganche salía cortada en una sola línea
        // ("…se configura en la pestaña Distancias del") con barra horizontal.
        var carta = new StackPanel { Spacing = 10, MaxWidth = 560 };
        carta.Children.Add(CfgUi.Titulo("Dimensiones del vehículo"));

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

        _txtWheelbase = Nud("Entre ejes", Valor(C.Snap?.Dimensiones?.Wheelbase));
        _txtTrack     = Nud("Trocha",     Valor(C.Snap?.Dimensiones?.TrackWidth));
        _txtHitch     = Nud("Distancia del eje rígido al enganche", Valor(C.Snap?.Dimensiones?.HitchLength));

        filas.Children.Add(FilaNud("Entre ejes", _txtWheelbase));
        filas.Children.Add(FilaNud("Trocha", _txtTrack));
        _filaHitch = FilaNud("Distancia del eje rígido al enganche", _txtHitch);
        filas.Children.Add(_filaHitch);
        carta.Children.Add(filas);

        _notaHitchOculto = CfgUi.Nota(
            "La distancia del eje rígido al enganche no aplica con implemento fijo o frontal — "
            + "se configura en la pestaña Distancias del implemento.");
        carta.Children.Add(_notaHitchOculto);

        Children.Add(CfgUi.Carta(carta));

        // Enganche visible SOLO con implemento TBT o de arrastre (réplica del
        // original); si no, en su lugar queda la nota que dice dónde se toca.
        string estilo = (C.Snap?.Enganche?.Estilo ?? "").Trim();
        bool conHitch = estilo.Equals("tbt", StringComparison.OrdinalIgnoreCase)
                     || estilo.Equals("trailing", StringComparison.OrdinalIgnoreCase);
        _filaHitch.IsVisible = conHitch;
        _notaHitchOculto.IsVisible = !conHitch;

        PintarHabilitado();
    }

    /// <summary>Valor del snapshot listo para editar: magnitud, en cm|in
    /// enteros (`Math.round(m2disp(Math.abs(v)))` del JS).</summary>
    private string Valor(double? m)
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

    // =======================================================================
    //  Validación (réplica de leerNud)
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
        v = Math.Abs(v);                        // el signo lo decide el motor
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

    private void PintarHabilitado()
    {
        bool editable = Editable() && !_guardando;
        foreach (var t in new[] { _txtWheelbase, _txtTrack, _txtHitch })
        {
            if (t == null) continue;
            t.IsEnabled = editable;
            t.Opacity = editable ? 1.0 : 0.55;
        }
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor (ni en qué unidad, ni
    /// con qué rangos) y el POST iría al mismo Hub que no contesta: editar a
    /// ciegas es peor que no poder editar.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    private bool AlgunCampoConFoco()
        => (_txtWheelbase?.IsFocused ?? false)
        || (_txtTrack?.IsFocused ?? false)
        || (_txtHitch?.IsFocused ?? false);

    // =======================================================================
    //  Diagramas
    // =======================================================================

    /// <summary>Los 3 PNG se decodifican UNA vez y se comparten entre rebuilds
    /// (Rebuild corre en cada entrada a la pestaña y en cada cambio de estado de
    /// conexión). Solo se toca desde el hilo de UI.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Bitmap?> _iconos =
        new System.Collections.Generic.Dictionary<string, Bitmap?>(StringComparer.Ordinal);

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
