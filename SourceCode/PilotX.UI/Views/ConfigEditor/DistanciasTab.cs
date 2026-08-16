// ============================================================================
// DistanciasTab.cs — pestaña "Implemento › Distancias" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=thitch` (la sección data-tab="thitch" +
// tabs.thitch de config.js).
//
// QUÉ QUEDÓ NATIVO: el diagrama de enganche según el modo, las TRES filas NUD
// (largo de barra / enganche de arrastre / enganche del tanque) con su
// visibilidad por modo, sus rangos y su validación, y el guardado
// (POST /api/aog/config/enganche_dist) con la relectura del snapshot.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (Offset, Pivote, Timing, Secciones…).
//
// Qué config toca (la trampa de las DOS configuraciones de implemento):
//   · esta pestaña habla con /api/aog/config — la de PilotX, la que usa el
//     GUIADO y la geometría del implemento (hitch_length /
//     trailing_hitch_length / tank_trailing_hitch_length), respaldada por
//     tool.json;
//   · NO toca /api/implemento (surcos y semillas/ha de la pantalla de siembra).
//     Son dos configuraciones distintas y NO están sincronizadas: acá no hay
//     nada que espejar, pero conviene saberlo antes de "arreglar" una
//     discrepancia entre pantallas.
//
// Unidades: el wire viene SIEMPRE en metros; la UI edita ENTEROS en cm o in
// según is_metric (M2Disp/Disp2M de CfgCtx, réplica de m2disp/disp2m del JS).
//
// Trampas cubiertas:
//   · El MODO no es el estilo a secas: con vehicle_type == 1 (cosechadora) el
//     modo es "harvester" y PISA lo que diga enganche.estilo. De ahí salen el
//     diagrama y qué filas se ven.
//   · Los valores se muestran SIEMPRE en magnitud: el motor guarda el signo
//     (barra trasera y arrastre/tanque ≤ 0) y lo decide él en
//     GuardarEngancheDist. Duplicar esa lógica acá sería inventar contrato.
//   · Body PARCIAL: solo viajan los campos VISIBLES del modo. Los ocultos se
//     cargan en pantalla pero NO se mandan — el backend trata lo ausente como
//     "no tocar", y mandarlos "por las dudas" re-normalizaría el signo de
//     campos que el operario no tocó. Es una diferencia consciente con
//     Dimensiones, que sí manda los tres.
//   · El mínimo de arrastre/tanque NO es cero: 10 cm | 4 in. Un 0 tipeado se
//     clampea solo (silencioso, igual que el original — sin diálogos).
//   · Rangos: réplica de limDrawbar/limTrailingHitch de config.js — si alguien
//     cambia los del JS hay que cambiar ESTOS también, o la cabina y el celular
//     van a guardar cosas distintas.
//   · Math.round de JavaScript (mitades hacia +infinito) ≠ Math.Round de .NET
//     (banqueros): va CfgCtx.RedondeoJs.
//   · Coma decimal: el operario escribe "2,5". Se reemplaza por punto y se
//     parsea con InvariantCulture, igual que el `replace(',', '.')` del JS.
//   · Validación en cascada con corte: en TBT, si el arrastre valida y el
//     tanque no, el arrastre ya quedó clampeado en pantalla y NO se guarda
//     nada. Es el comportamiento heredado del JS y se replica tal cual: pulir
//     eso le cambiaría al operario lo que ve.
//
// Quirk del HTML que NO se porta (a propósito): config.js pone `th.dirty =
// false` ANTES del POST, así que un guardado fallido deja el segundo intento
// sin mandar nada y el botón canta "Guardado ✔" sin haber guardado. Acá el
// dirty se limpia SOLO si el POST salió bien.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO Y CON REINICIO DEL MOTOR
// (2026-08-16, banco): los TRES campos sobreviven a matar y volver a levantar
// PilotX.GuidanceEngine. Todos viajan por tool.json (ToolGeometryStore.Guardar
// espeja HitchLength / TrailingHitchLength / TankTrailingHitchLength y Cargar()
// los reaplica en el arranque, antes de construir CTool/CVehicle); hitch_length
// además queda en el perfil <Documentos>\AgOpenGPS\Vehicles\<perfil>.XML por el
// Settings.Default.Save(). Ojo: setTool_toolTrailingHitchLength NO está en el
// XML del perfil (es setting de Tool, no de Vehicle) — si algún día se borra
// tool.json, el arrastre vuelve al default aunque el perfil siga entero.
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

public sealed class DistanciasTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    /// <summary>Tipo de vehículo "cosechadora" en el wire (`vehicle_type`).</summary>
    private const int TipoCosechadora = 1;

    /// <summary>Diagrama por modo (el TH_IMG del JS).</summary>
    private static string Diagrama(string modo) => modo switch
    {
        "front"     => "ToolHitchPageFront.png",
        "rear"      => "ToolHitchPageRear.png",
        "tbt"       => "ToolHitchPageTBT.png",
        "harvester" => "ToolHitchPageFrontHarvester.png",
        _           => "ToolHitchPageTrailing.png",
    };

    // ---- rangos (réplica EXACTA de limDrawbar/limTrailingHitch) -------------
    // Son los del original WinForms tras FixMinMaxSpinners, en cm | in.
    // MANTENER SINCRONIZADO con config.js: los dos guardan al mismo motor.
    // El mínimo del arrastre/tanque es 10 cm | 4 in — NO cero.
    private (double min, double max) LimDrawbar()       => C.IsMetric ? (0, 3000)  : (0, 1181);
    private (double min, double max) LimTrailingHitch() => C.IsMetric ? (10, 3000) : (4, 1181);

    // ---- modelo local (el `th` de config.js) -------------------------------
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: los campos quedan muertos (anti doble-tap
    /// y anti "sigo tipeando mientras se guarda").</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo los TextBox por código (carga o clamp): el
    /// TextChanged de esos cambios NO es un cambio del operario.</summary>
    private bool _cargando;

    private TextBox? _txtDrawbar, _txtTrailing, _txtTank;
    private Control? _filaDrawbar, _filaTrailing, _filaTank;

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    /// <summary>Modo con el que se armó el árbol. Si el operario cambia el
    /// estilo de enganche o el tipo de vehículo desde otra pestaña (o desde el
    /// celular), acá cambian el diagrama Y qué filas se ven: hay que rearmar.
    /// </summary>
    private string _modoPintado = "";

    public DistanciasTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>El shell lo consulta antes de cantar "Guardado ✔": sin cambios
    /// no se manda nada al motor.</summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Modo (el thModo() de config.js)
    // =======================================================================

    /// <summary>
    /// `harvester` si el vehículo es cosechadora (pisa al estilo: el motor le
    /// fuerza implemento frontal), si no el estilo tal cual viene del wire.
    /// Un estilo desconocido (motor viejo / snapshot incompleto) cae a
    /// "trailing": en el JS dejaría la pantalla con CERO filas y un diagrama
    /// roto, y una pantalla vacía no le dice nada al operario.
    /// </summary>
    private string ModoActual()
    {
        if ((C.Snap?.Vehiculo?.VehicleType ?? 0) == TipoCosechadora) return "harvester";
        string e = (C.Snap?.Enganche?.Estilo ?? "").Trim().ToLowerInvariant();
        return e is "front" or "rear" or "tbt" or "trailing" ? e : "trailing";
    }

    private static bool ConDrawbar(string m)  => m is "front" or "rear" or "harvester";
    private static bool ConTrailing(string m) => m is "tbt" or "trailing";
    private static bool ConTank(string m)     => m == "tbt";

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repinta todo desde el snapshot (diagrama, valores,
    /// filas visibles) y descarta cambios sin guardar, igual que el HTML.
    /// </summary>
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

        string modo = ModoActual();

        // Body PARCIAL: SOLO los campos visibles del modo, en metros y en
        // magnitud. Lo que no viaja, el motor no lo toca.
        var body = new Dictionary<string, object>(StringComparer.Ordinal);

        // Orden y corte idénticos al JS: el primer inválido corta la validación
        // (los que ya pasaron quedan clampeados en pantalla y no se guarda nada).
        if (ConDrawbar(modo))
        {
            double? d = LeerNud(_txtDrawbar, LimDrawbar());
            if (d == null) { C.Estado?.Invoke("Revisá los valores marcados en rojo", "err"); return false; }
            body["hitch_length"] = C.Disp2M(d.Value);
        }
        if (ConTrailing(modo))
        {
            double? t = LeerNud(_txtTrailing, LimTrailingHitch());
            if (t == null) { C.Estado?.Invoke("Revisá los valores marcados en rojo", "err"); return false; }
            body["trailing_hitch_length"] = C.Disp2M(t.Value);
        }
        if (ConTank(modo))
        {
            double? k = LeerNud(_txtTank, LimTrailingHitch());
            if (k == null) { C.Estado?.Invoke("Revisá los valores marcados en rojo", "err"); return false; }
            body["tank_trailing_hitch_length"] = C.Disp2M(k.Value);
        }

        // Ningún campo del modo (no debería pasar: siempre hay 1 o 2 filas).
        // Sin campos el POST no tendría nada que guardar y el shell cantaría
        // "Guardado ✔" por un POST vacío.
        if (body.Count == 0) { _dirty = false; return true; }

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            var r = await C.Client.GuardarAsync("enganche_dist", body).ConfigureAwait(true);

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

            // Relectura: el motor le pone el SIGNO a lo guardado (barra trasera
            // y arrastre/tanque negativos) y recalcula geometría. Sin esto el
            // footer y las otras pestañas (Dimensiones comparte
            // setVehicle_hitchLength) quedarían con datos viejos.
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
    /// rearma si cambió el estado de conexión o el MODO (diagrama y filas
    /// visibles son otros) y nadie está tipeando.</summary>
    public override void Live()
    {
        if (_estadoPintado == EstadoActual() && _modoPintado == ModoActual()) return;
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
        string modo = ModoActual();
        _modoPintado = modo;

        // MaxWidth OBLIGATORIO: el TabHost cuelga de un ScrollViewer con scroll
        // horizontal, así que sin un ancho tope el StackPanel mide "infinito" y
        // nada envuelve.
        var carta = new StackPanel { Spacing = 10, MaxWidth = 560 };
        carta.Children.Add(CfgUi.Titulo("Distancias de enganche"));

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

        carta.Children.Add(new Image
        {
            Source = Icono(Diagrama(modo)),
            // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml (que la ventana
            // incluye para las barras del cockpit) trae un `Style
            // Selector="Image"` con máximos de 34 px que aplica a TODA imagen de
            // la ventana; sin esto el diagrama sale de estampilla.
            MaxWidth = 420, MaxHeight = 220,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var filas = new StackPanel { Spacing = 8 };

        _txtDrawbar  = Nud("Largo de barra",       Valor(C.Snap?.Enganche?.HitchLength));
        _txtTrailing = Nud("Enganche de arrastre", Valor(C.Snap?.Enganche?.TrailingHitchLength));
        _txtTank     = Nud("Enganche del tanque",  Valor(C.Snap?.Enganche?.TankTrailingHitchLength));

        _filaDrawbar  = FilaNud("Largo de barra", _txtDrawbar);
        _filaTrailing = FilaNud("Enganche de arrastre", _txtTrailing);
        _filaTank     = FilaNud("Enganche del tanque", _txtTank);

        filas.Children.Add(_filaDrawbar);
        filas.Children.Add(_filaTrailing);
        filas.Children.Add(_filaTank);
        carta.Children.Add(filas);

        // Filas visibles según el modo (tabla del original): front/rear/
        // harvester → solo la barra · trailing → solo el arrastre · tbt →
        // arrastre + tanque. Nunca cero, nunca las tres.
        _filaDrawbar.IsVisible  = ConDrawbar(modo);
        _filaTrailing.IsVisible = ConTrailing(modo);
        _filaTank.IsVisible     = ConTank(modo);

        Children.Add(CfgUi.Carta(carta));

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
    /// entero REESCRIBIENDO el campo, así el operario ve con qué se guardó (por
    /// ejemplo un 0 en el arrastre aparece como 10).
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
        foreach (var t in new[] { _txtDrawbar, _txtTrailing, _txtTank })
        {
            if (t == null) continue;
            t.IsEnabled = editable;
            t.Opacity = editable ? 1.0 : 0.55;
        }
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor (ni en qué unidad, ni
    /// con qué modo, ni con qué rangos) y el POST iría al mismo Hub que no
    /// contesta: editar a ciegas es peor que no poder editar.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    private bool AlgunCampoConFoco()
        => (_txtDrawbar?.IsFocused ?? false)
        || (_txtTrailing?.IsFocused ?? false)
        || (_txtTank?.IsFocused ?? false);

    // =======================================================================
    //  Diagramas
    // =======================================================================

    /// <summary>Los 5 PNG se decodifican UNA vez y se comparten entre rebuilds
    /// (Rebuild corre en cada entrada a la pestaña, en cada cambio de modo y en
    /// cada cambio de estado de conexión). Solo se toca desde el hilo de UI.
    /// </summary>
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
