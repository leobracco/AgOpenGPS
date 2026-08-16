// ============================================================================
// RolidoTab.cs — pestaña "GPS / IMU › Rolido" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=roll` (la sección data-tab="roll" +
// tabs.roll / rollAccion() / rollLivePoll() de config.js).
//
// QUÉ QUEDÓ NATIVO: las dos cartas — "Cero de rolido" (los 5 botones de acción
// inmediata, el toggle "Invertir rolido" y la barra de Filtro) y "Tractor visto
// de atrás (en vivo)" con el poll de 500 ms y el tap-para-poner-en-cero.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (U-Turn, Tram) más los módulos embebidos.
//
// Qué configura esta pantalla, en criollo: el CERO del rolido. La IMU mide la
// inclinación lateral y PilotX la usa para corregir la posición de la antena
// (una antena a 3 m de altura con el tractor 5° inclinado miente ~26 cm de
// posición). Si el cero está mal, TODA la pasada queda corrida para el mismo
// lado: no es cosmético, se paga en el lote.
//
// Qué configuración toca (la trampa de las TRES superficies de config):
//   · SOLO /api/aog/config (la de PilotX / el guiado), sección "rolido", más el
//     endpoint hermano /api/aog/config/rolido/accion. NO toca /api/implemento
//     (surcos y semillas/ha de la pantalla de siembra) ni /api/tool
//     (ToolConfigDto camelCase del QuantiXEditor).
//   · Los campos viven en Settings (setIMU_rollZero, setIMU_rollFilter,
//     setIMU_invertRoll) y NINGUNO baja a tool.json: quien los persiste es
//     Settings.Save() → <Documentos>\AgOpenGPS\Vehicles\<perfil>.XML.
//   · El vivo NO sale de la config: sale de GET /api/aog/graph-correction.
//
// ---------------------------------------------------------------------------
// TRES MECÁNICAS DE GUARDADO CONVIVIENDO — SON DEL ORIGINAL, NO UN DESPROLIJO.
// Quien venga después: NO LAS UNIFIQUES.
//   1. ACCIONES INMEDIATAS (poner en cero / quitar offset / ±0,1° / reiniciar
//      IMU, y el tap en el tractorcito): POST /rolido/accion. Aplican y
//      PERSISTEN al toque en el motor y NO marcan la pestaña como sucia — el
//      botón Guardar del shell ni se entera. Es calibración: quien la toca
//      quiere el efecto ya, mirando el dibujo.
//   2. INVERTIR ROLIDO: guarda AL TOQUE (no espera a salir de la pestaña). La
//      calibración es visual — el operario invierte y mira el tractorcito; si
//      quedara "pendiente hasta Guardar" el dibujo no reflejaría nada y la
//      verificación se rompe (reporte 2026-08-10).
//   3. FILTRO (la barra): ÚNICO campo con guardado diferido clásico — marca
//      sucio y se manda al salir de la pestaña o con el botón Guardar.
// Y el POST del toggle manda TAMBIÉN el filtro con el valor actual de la barra
// (réplica exacta del HTML): si el operario movió la barra y después invierte,
// el filtro queda guardado en ESE mismo POST. "Prolijearlo" mandando solo
// invert_roll cambiaría el momento en que el filtro pega en el motor.
//
// EL TRACTOR EN VIVO muestra el rolido QUE USA PILOTX (roll_degrees de
// graph-correction: cero e inversión YA aplicados por el motor), no el crudo
// del ECU. Antes mostraba el crudo y "poner en cero" parecía no hacer nada,
// porque el cero es un offset del lado de PilotX (reporte 2026-08-10). Corolario
// que NO hay que "arreglar": después de tocar el cero, el dibujo se endereza en
// el próximo tick (≤500 ms). La fuente de verdad es el motor, no la pantalla.
//
// Trampas cubiertas:
//   · CENTINELA 88888: `imu_roll` NO es un ángulo cuando vale 88888 (= sin dato
//     de IMU). Todo formateo pasa antes por imu_present / roll_present. Un
//     formateo directo pintaría "+88888.0°" y volcaría el tractor a +30°.
//   · CATCH MUDO DEL POLL: si el motor no contesta se CONSERVA el último cuadro.
//     Blanquear a "—" en cada timeout haría parpadear el número con el motor
//     ocupado, y pintar "sin IMU" por un bache de red sería mentira: el estado
//     rojo es SOLO roll_present:false.
//   · EL "***" DEL LABEL: tras una acción fallida por sin-imu el label del cero
//     pasa a "***"; con ok:true se pinta el valor PERO queda "***" si
//     !imu_present && accion != "quitar" (o sea: "quitar" muestra 0.00 aunque no
//     haya IMU, y "reiniciar IMU" deja "***" a propósito). La condición es fácil
//     de simplificar mal — es réplica exacta del JS.
//   · POR ESO Live() NO REPINTA EL CERO desde el snapshot: el refresco de 3 s
//     del shell borraría ese "***" y el operario creería que hay dato de IMU.
//   · CLAMP VISUAL ±30°: el DIBUJO se clampea, el NÚMERO se muestra crudo. Sin
//     clamp, una IMU en falla (180°) daría un tractor patas arriba; sin el
//     número crudo, el operario no vería que la IMU está en falla.
//   · PARAR EL POLL SIEMPRE al salir: un timer huérfano a 2 Hz contra el motor
//     es el tipo de fuga que ya congeló la UI antes. TaskCanceledException
//     hereda de OperationCanceledException — se filtra con `when
//     (ct.IsCancellationRequested)` y los pushes van por Dispatcher.UIThread.
//
// Quirks del HTML que NO se portan (a propósito):
//   · El FLASH dirty→ok del toggle Invertir: allá el `.chkimg` cae en la
//     delegación de eventos de #main, así que el botón flotante se enciende y se
//     apaga en el mismo click. Es un artefacto del HTML, no un requisito.
//   · Los `title=` de los 5 botones: hover no existe en cabina táctil. El rol de
//     los tooltips lo cumple la nota visible ("Cero actual · flechas ±0.1° · el
//     último botón reinicia la IMU."), que se mantiene.
//   · config.js pone `rl.dirty = false` ANTES del POST del toggle: si ese POST
//     falla, el filtro que estaba pendiente se pierde en silencio. Acá el dirty
//     se limpia SOLO si el POST salió bien.
//   · Si el POST del toggle falla, acá se REVIERTE el toggle (misma doctrina que
//     RumboTab): el motor no invirtió nada y dejarlo en verde sería mentirle al
//     operario sobre para qué lado corrige la máquina.
//
// NO SE FUSIONA con TractorRolidoOverlay (el mini tractor del mapa): comparten
// endpoint y la idea del dibujo, pero son dos roles distintos — aquel es solo
// lectura, chiquito, paleta oscura y IsHitTestVisible=false; este es el de
// CALIBRACIÓN: grande, interactivo (tap = poner en cero) y en paleta clara de
// card. Con el panel abierto conviven los dos polls; es barato y no se
// "optimiza" acoplando las clases.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO CON REINICIO REAL DEL MOTOR
// (banco, 2026-08-16). Método válido: POST → mirar el XML en disco → matar el
// motor → arranque limpio → GET. "Guardar y releer" NO prueba nada: el GET arma
// el snapshot leyendo Settings EN MEMORIA.
// Ronda 1 (wire crudo, juego ASIMÉTRICO — nada de "todo true", que taparía un
// campo que no se escribe):
//   POST /rolido {"roll_filter":37,"invert_roll":false} · 3× /rolido/accion
//   {"accion":"subir"} (cero 0,4 → 0,7) · POST /rolido {"roll_filter":51,
//   "invert_roll":true}
//   En G:\Documentos\AgOpenGPS\Vehicles\PilotX.XML quedaron setIMU_rollFilter
//   0.51, setIMU_invertRoll True y setIMU_rollZero 0.7. Matado el motor y
//   levantado limpio, el GET devolvió roll_filter 51, invert_roll true,
//   roll_zero 0.7 — los mismos.
// Ronda 2 (desde ESTA pantalla, con PilotX.Desktop contra el motor real):
//   "quitar offset" ⇒ cero 0 · toggle Invertir ⇒ invert false · barra a 80 +
//   botón Guardar. XML: setIMU_rollZero 0, setIMU_invertRoll False,
//   setIMU_rollFilter 0.8. Matado el motor y levantado limpio, el GET devolvió
//   roll_zero 0, invert_roll false, roll_filter 80.
//   PERSISTEN (3 de 3): roll_filter · invert_roll · roll_zero (el de las
//   acciones inmediatas, que el backend escribe con Settings.Save() propio).
//   NO PERSISTE NADA MÁS: imu_present / imu_roll / roll_degrees son RUNTIME
//   (estado de la IMU), no configuración — no tienen dónde guardarse ni deben.
// Dato que conviene saber: POST /rolido persiste SOLO (el dispatcher Guardar
// hace su Settings.Save()); no hace falta ninguna acción previa que "empuje" el
// archivo. Acá SÍ persiste Settings.Save(): el perfil existe
// (RegistrySettings.vehicleFileName = "PilotX", creado por Program.cs al
// arrancar). Si algún día el motor arrancara sin perfil, esta pestaña entera
// pasaría a cantar un "Guardado ✔" que dura hasta el próximo arranque.
//
// PRUEBA DE PANTALLA (banco, 2026-08-16, PilotX.Desktop contra el motor real,
// con el motor SIN dato de IMU tras un reset_imu):
//   · el tractor queda derecho y el valor en "sin IMU" rojo (roll_present:false);
//   · "poner en cero" ⇒ label "***" y footer "Sin datos de IMU" en rojo;
//   · "reiniciar IMU" ⇒ "Aplicado ✔" y el label SIGUE en "***" (imu_present
//     false y la acción no es "quitar") — la condición que hay que NO simplificar;
//   · "quitar offset" ⇒ "0.00" AUNQUE no haya IMU, con "Aplicado ✔";
//   · ninguna de esas tres encendió el botón Guardar (siguió en "Sin cambios");
//   · toggle Invertir ⇒ se apaga el borde verde y el footer canta "Rolido normal
//     ✔" al toque, sin pasar por "Guardar" (y el XML ya quedó en False);
//   · mover la barra ⇒ el botón pasa a "Guardar"; al tocarlo, el XML queda en
//     setIMU_rollFilter = barra × 0,01.
// Detalle de layout conocido: en la card de 326 px los 5 botones entran 4 + 1
// (el `.btnZero` mide 56 y no dan los 5 en una línea). Es el mismo envolver del
// flex-wrap del HTML; achicar los botones rompería el tamaño táctil.
// Falta la validación en cabina CON IMU REAL (inclinar la máquina y ver el
// dibujo seguirla, y el "poner en cero" enderezándolo): en el tablero va
// `Prueba:`.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class RolidoTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Milisegundos entre muestras del vivo — los mismos 500 ms del
    /// setInterval del HTML. Más rápido no aporta (el motor redondea a 1
    /// decimal) y más lento hace que el dibujo "salte".</summary>
    private const int PollMs = 500;

    /// <summary>Tope del ángulo DEL DIBUJO. El número se muestra crudo.</summary>
    private const double TopeDibujo = 30.0;

    // ---- modelo local (el `rl` de config.js) -------------------------------
    private bool _invert;
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: todo queda muerto (anti doble-tap).</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo por código (carga): ese cambio NO es del
    /// operario y no tiene que ensuciar la pestaña.</summary>
    private bool _cargando;

    private TextBlock? _txtZero, _txtVivo, _lblPct;
    private Slider? _slFiltro;
    private Border? _tileInvert;
    private readonly List<Button> _botones = new List<Button>();
    private readonly RotateTransform _rot = new RotateTransform(0);

    /// <summary>Poll del vivo. Se para SIEMPRE al salir de la pestaña.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>Estado con el que se armó el árbol, para que el refresco de
    /// fondo sepa si tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public RolidoTab(CfgCtx c) : base(c) { }

    /// <summary>La barra de Filtro es lo único que se guarda diferido, y para eso
    /// hace falta el botón Guardar del shell.</summary>
    public override bool TieneGuardar => true;

    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: lee el snapshot, repinta, descarta cambios sin
    /// guardar y arranca el poll del vivo.</summary>
    public override Task AlEntrarAsync()
    {
        LeerDelSnapshot();
        _dirty = false;
        PintarValores();
        Pintar();
        ArrancarPoll();
        return Task.CompletedTask;
    }

    /// <summary>`leave()`: PARA EL POLL SIEMPRE (primera línea, aunque no haya
    /// nada que guardar) y recién después guarda el filtro si quedó pendiente.
    /// false CANCELA la navegación.</summary>
    public override async Task<bool> AlSalirAsync()
    {
        PararPoll();
        if (!_dirty) return true;
        if (C.Client == null) { C.Estado?.Invoke("Sin conexión con PilotX", "err"); ArrancarPoll(); return false; }
        // POST en vuelo (el inmediato del toggle Invertir): cancelar la
        // navegación EN SILENCIO deja al operario tocando otra pestaña sin que
        // pase nada y sin una línea que lo explique. Se avisa.
        if (_guardando) { C.Estado?.Invoke("Esperá, se está guardando…", ""); ArrancarPoll(); return false; }

        _guardando = true;
        Pintar();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            // Body EXACTO del `leave()` del HTML: los DOS campos siempre, aunque
            // solo se haya movido la barra.
            var r = await C.Client.GuardarAsync("rolido", new
            {
                roll_filter = ValorFiltro(),
                invert_roll = _invert,
            }).ConfigureAwait(true);

            if (r == null)
            {
                C.Estado?.Invoke("Sin conexión con PilotX", "err");
                ArrancarPoll();          // el operario se queda acá: el vivo sigue vivo
                return false;
            }
            if (!r.Ok)
            {
                C.Estado?.Invoke("Error: " + (string.IsNullOrWhiteSpace(r.Error) ? "desconocido" : r.Error), "err");
                ArrancarPoll();
                return false;
            }

            _dirty = false;
            C.Estado?.Invoke("Guardado ✔", "ok");

            // Relectura: el motor clampea la barra a 0..98 de su lado y ADEMÁS
            // asienta en el POST el rollZero vivo que dejaron las acciones.
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
            Pintar();
        }
    }

    /// <summary>Mapeo snapshot → modelo (el `enter()` del JS). Sin la sección no
    /// se inventan defaults: se deja lo que había.</summary>
    private void LeerDelSnapshot()
    {
        var z = C.Snap?.Rolido;
        if (z == null) return;
        _invert = z.InvertRoll;
    }

    /// <summary>
    /// Refresco de fondo del shell (3 s). Esta pestaña NO resincroniza sus
    /// valores desde el snapshot a propósito: el label del cero puede estar en
    /// "***" (acción fallida por sin-imu, o reset_imu) y repintarlo con el
    /// roll_zero del snapshot borraría ese aviso, que es justamente el que le
    /// dice al operario que no hay dato de IMU. El vivo ya se refresca solo, a
    /// 500 ms, por su propio poll.
    /// Solo se rearma el árbol si cambió el estado de conexión (aparece o
    /// desaparece el servicio) y no hay nada pendiente.
    /// </summary>
    public override void Live()
    {
        if (_estadoPintado == EstadoActual()) return;
        if (_dirty || _guardando) return;
        LeerDelSnapshot();
        Rebuild();
    }

    // =======================================================================
    //  Poll del tractor en vivo (rollLiveStart / rollLiveStop del HTML)
    // =======================================================================

    /// <summary>Guard anti doble-start: cancela el anterior antes de arrancar.</summary>
    private void ArrancarPoll()
    {
        PararPoll();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = PollAsync(cts.Token);
    }

    private void PararPoll()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        // Primer tick INMEDIATO (igual que rollLiveStart): el operario no se
        // come medio segundo de "—" al entrar.
        while (!ct.IsCancellationRequested)
        {
            CorrectionSample? s = null;
            if (C.Client != null)
            {
                try { s = await C.Client.GetCorrectionAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { s = null; }
            }

            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => PintarVivo(s));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { }

            try { await Task.Delay(PollMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Un cuadro del vivo. `null` = el motor no contestó ⇒ SE CONSERVA EL ÚLTIMO
    /// CUADRO (catch mudo del HTML): no se blanquea ni se pinta "sin IMU", que
    /// significaría otra cosa.
    /// </summary>
    private void PintarVivo(CorrectionSample? s)
    {
        if (_txtVivo == null) return;
        if (s == null) return;

        if (!s.RollPresent)
        {
            _rot.Angle = 0;
            _txtVivo.Text = PilotX.Cockpit.Bars.Traductor.T("sin IMU");
            _txtVivo.Foreground = CfgUi.Err;
            return;
        }

        double roll = s.RollDegrees;
        // El DIBUJO se clampea (una IMU en falla no vuelca el tractorcito); el
        // NÚMERO va crudo, así el operario ve que algo anda mal.
        _rot.Angle = Math.Max(-TopeDibujo, Math.Min(TopeDibujo, roll));
        _txtVivo.Text = (roll > 0 ? "+" : "") + roll.ToString("0.0", Inv) + "°";
        _txtVivo.Foreground = CfgUi.Texto;
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _botones.Clear();
        _estadoPintado = EstadoActual();

        // MaxWidth + Left OBLIGATORIOS, las dos cosas: el TabHost cuelga de un
        // ScrollViewer con scroll HORIZONTAL, así que sin tope el panel mide
        // "infinito" (las notas se estiran a una línea larguísima y las cartas
        // se van de la pantalla) y con Stretch un hijo con MaxWidth se CENTRA en
        // el sobrante en vez de arrancar a la izquierda.
        MaxWidth = 686;
        HorizontalAlignment = HorizontalAlignment.Left;

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
        else if (C.Snap?.Rolido == null)
        {
            Children.Add(CfgUi.ChipError("PilotX no informó la configuración de rolido", "AGP-NET-201"));
        }

        // Las dos cartas al lado (el `.dosCol` del HTML).
        var dosCol = CfgUi.Grilla();
        dosCol.HorizontalAlignment = HorizontalAlignment.Left;
        dosCol.Children.Add(CartaCero());
        dosCol.Children.Add(CartaTractor());
        Children.Add(dosCol);

        PintarValores();
        Pintar();
    }

    // ---- carta 1: cero de rolido -------------------------------------------

    private Border CartaCero()
    {
        var col = new StackPanel { Spacing = 10 };
        col.Children.Add(CfgUi.Titulo("Cero de rolido"));

        _txtZero = new TextBlock
        {
            Text = "0.00",
            Foreground = CfgUi.Texto, FontSize = 34, FontWeight = FontWeight.ExtraBold,
            FontFamily = CfgUi.Mono,
        };
        col.Children.Add(_txtZero);

        // Los 5 botones de acción INMEDIATA (ver la cabecera: no ensucian nada).
        var fila = CfgUi.Grilla();
        fila.HorizontalAlignment = HorizontalAlignment.Left;
        fila.Children.Add(BotonAccion("ConDa_RollSetZero.png", "zero"));
        fila.Children.Add(BotonAccion("ConDa_RemoveOffset.png", "quitar"));
        fila.Children.Add(BotonAccion("UpArrow64.png", "subir"));
        fila.Children.Add(BotonAccion("DnArrow64.png", "bajar"));
        fila.Children.Add(BotonAccion("ConDa_ResetIMU.png", "reset_imu"));
        col.Children.Add(fila);

        col.Children.Add(CfgUi.Nota("Cero actual · flechas ±0.1° · el último botón reinicia la IMU."));

        _tileInvert = FilaInvertir();
        col.Children.Add(_tileInvert);

        // Fila del Filtro (la `.nudfila` con el input[type=range] del HTML).
        _slFiltro = new Slider
        {
            Minimum = 0, Maximum = 98,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 10,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = CfgUi.Verde,
        };
        _slFiltro.ValueChanged += (_, __) =>
        {
            if (_cargando) return;
            _dirty = true;
            C.MarcarSucio?.Invoke();
            PintarPorcentaje();
        };

        _lblPct = new TextBlock
        {
            Text = "0%",
            Foreground = CfgUi.TextoDim, FontSize = 12, FontWeight = FontWeight.SemiBold,
            FontFamily = CfgUi.Mono,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
            TextAlignment = TextAlignment.Right,
        };

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var lbl = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T("Filtro"),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(_slFiltro, 1);
        Grid.SetColumn(_lblPct, 2);
        g.Children.Add(lbl);
        g.Children.Add(_slFiltro);
        g.Children.Add(_lblPct);
        col.Children.Add(g);

        return Carta(col);
    }

    /// <summary>El `.btnZero` del HTML: 56×56 con el dibujo de 40×40. Dispara una
    /// acción INMEDIATA (POST /rolido/accion), que no marca sucio.</summary>
    private Button BotonAccion(string icono, string accion)
    {
        var b = new Button
        {
            Width = 56, Height = 56,
            Margin = new Thickness(0, 0, 10, 0),
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
                Width = 40, Height = 40,
                // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml (que la ventana
                // incluye para las barras del cockpit) trae un
                // `Style Selector="Image"` con máximos de 34 px que le gana al
                // Width local. Sin esto el pictograma sale de estampilla.
                MaxWidth = 40, MaxHeight = 40,
                Stretch = Stretch.Uniform,
            },
        };
        string acc = accion;
        b.Click += (_, __) => Accion(acc);
        _botones.Add(b);
        return b;
    }

    /// <summary>El `.chkimg.sw` del HTML. Guarda AL TOQUE (mecánica 2 de la
    /// cabecera).</summary>
    private Border FilaInvertir()
    {
        var img = new Image
        {
            Source = Icono("ConDa_InvertRoll.png"),
            Width = 44, Height = 44,
            MaxWidth = 44, MaxHeight = 44,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var texto = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T("Invertir rolido"),
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
        borde.Tapped += (_, __) => TocarInvertir();
        return borde;
    }

    // ---- carta 2: tractor visto de atrás (en vivo) --------------------------

    private Border CartaTractor()
    {
        var col = new StackPanel { Spacing = 10 };
        col.Children.Add(CfgUi.Titulo("Tractor visto de atrás (en vivo)"));

        var fila = CfgUi.Fila(16);
        fila.Children.Add(Dibujo());

        var datos = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        datos.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T("Rolido en vivo").ToUpperInvariant(),
            Foreground = CfgUi.TextoMuted, FontSize = 11, FontWeight = FontWeight.Bold,
        });
        _txtVivo = new TextBlock
        {
            Text = "—",
            Foreground = CfgUi.Texto, FontSize = 32, FontWeight = FontWeight.ExtraBold,
            FontFamily = CfgUi.Mono,
        };
        datos.Children.Add(_txtVivo);
        datos.Children.Add(CfgUi.Nota("− izquierda · derecha +"));
        fila.Children.Add(datos);
        col.Children.Add(fila);

        // La nota de convención, con las negritas del HTML.
        var nota = new TextBlock
        {
            Foreground = CfgUi.TextoMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        nota.Inlines = new InlineCollection
        {
            new Run(PilotX.Cockpit.Bars.Traductor.T("Inclinado a la ")),
            new Run(PilotX.Cockpit.Bars.Traductor.T("derecha")) { FontWeight = FontWeight.Bold },
            new Run(PilotX.Cockpit.Bars.Traductor.T(" = número ")),
            new Run(PilotX.Cockpit.Bars.Traductor.T("positivo")) { FontWeight = FontWeight.Bold },
            new Run(PilotX.Cockpit.Bars.Traductor.T(". Si el dibujo se inclina al revés de la máquina, activá ")),
            new Run(PilotX.Cockpit.Bars.Traductor.T("Invertir rolido")) { FontWeight = FontWeight.Bold },
            new Run(PilotX.Cockpit.Bars.Traductor.T(
                " (el dibujo lo refleja al toque). Con el tractor en piso nivelado, poné el cero.")),
        };
        col.Children.Add(nota);

        return Carta(col);
    }

    /// <summary>
    /// El SVG del HTML, rehecho con formas de Avalonia: mismas coordenadas
    /// (lienzo 230×120, pivote 115,88), misma paleta. Tocarlo pone el cero — es
    /// el gesto natural del operario: "está derecho, marcalo así".
    /// </summary>
    private Control Dibujo()
    {
        var lienzo = new Canvas
        {
            Width = 230, Height = 120,
            // Transparent y NO null: con Background null el Canvas no recibe el
            // toque y el tap-para-poner-en-cero solo andaría sobre los dibujos.
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        // Línea de piso punteada (referencia del horizonte). En Avalonia el dash
        // se mide en múltiplos del grosor: 5 px con grosor 1,5 ≈ 3,3.
        var piso = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Point(8, 98),
            EndPoint = new Point(222, 98),
            Stroke = CfgUi.Borde,
            StrokeThickness = 1.5,
            StrokeDashArray = new AvaloniaList<double> { 3.3, 3.3 },
        };
        lienzo.Children.Add(piso);

        var tractor = new Canvas { Width = 230, Height = 120 };
        void Rect(double x, double y, double w, double h, IBrush b, double rad)
        {
            var r = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = w, Height = h, Fill = b, RadiusX = rad, RadiusY = rad,
            };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            tractor.Children.Add(r);
        }
        Rect(64, 60, 24, 28, CfgUi.Texto, 4);    // rueda izquierda
        Rect(142, 60, 24, 28, CfgUi.Texto, 4);   // rueda derecha
        Rect(60, 50, 110, 15, CfgUi.Verde, 4);   // eje / cuerpo
        Rect(86, 16, 58, 38, CfgUi.Verde, 6);    // cabina
        Rect(94, 22, 42, 18, CfgUi.BgFila, 3);   // vidrio

        tractor.RenderTransform = _rot;
        // El `rotate(ang 115 88)` del SVG: pivote abajo-centro.
        tractor.RenderTransformOrigin = new RelativePoint(115.0 / 230.0, 88.0 / 120.0, RelativeUnit.Relative);
        lienzo.Children.Add(tractor);

        lienzo.Tapped += (_, __) => Accion("zero");
        return lienzo;
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

    // =======================================================================
    //  Acciones
    // =======================================================================

    /// <summary>
    /// `rollAccion(accion)` — POST inmediato. El backend YA aplicó y persistió
    /// cuando contesta ok:true; acá no se calcula nada, solo se muestra lo que
    /// vuelve. NINGUNA acción marca sucio (el botón Guardar del shell sigue como
    /// estaba).
    /// </summary>
    private void Accion(string accion)
    {
        if (_guardando || !Editable()) return;
        _ = AccionAsync(accion);
    }

    private async Task AccionAsync(string accion)
    {
        _guardando = true;
        Pintar();
        try
        {
            var r = C.Client == null
                ? null
                : await C.Client.PostRolidoAccionAsync(accion).ConfigureAwait(true);

            if (r == null)
            {
                C.Estado?.Invoke("Sin conexión con PilotX", "err");
                return;
            }
            if (!r.Ok)
            {
                if (r.Error == "sin-imu")
                {
                    PintarZero(0, false);            // el "***"
                    C.Estado?.Invoke("Sin datos de IMU", "err");
                }
                else
                {
                    C.Estado?.Invoke("Error: " + (string.IsNullOrWhiteSpace(r.Error) ? "desconocido" : r.Error), "err");
                }
                return;
            }

            // Condición del original, NO simplificar: tras "quitar" el label
            // muestra 0.00 AUNQUE no haya IMU; tras "reset_imu" (que deja
            // imu_present:false y no es "quitar") queda "***" a propósito.
            PintarZero(r.RollZero, r.ImuPresent || accion == "quitar");
            C.Estado?.Invoke("Aplicado ✔", "ok");
        }
        finally
        {
            _guardando = false;
            Pintar();
        }
    }

    /// <summary>«Invertir rolido»: guarda AL TOQUE (mecánica 2). Manda TAMBIÉN el
    /// filtro con el valor actual de la barra — réplica exacta del HTML.</summary>
    private void TocarInvertir()
    {
        if (_guardando || !Editable()) return;
        _ = InvertirAsync();
    }

    private async Task InvertirAsync()
    {
        bool antes = _invert;
        _invert = !_invert;
        _guardando = true;
        Pintar();                      // el operario ve el cambio YA
        try
        {
            C.Estado?.Invoke("Guardando…", "");
            var r = C.Client == null
                ? null
                : await C.Client.GuardarAsync("rolido", new
                {
                    roll_filter = ValorFiltro(),
                    invert_roll = _invert,
                }).ConfigureAwait(true);

            if (r == null || !r.Ok)
            {
                // Revertir: el motor NO invirtió nada y dejarlo en verde sería
                // mentirle al operario sobre para qué lado corrige la máquina.
                _invert = antes;
                string msg = r == null
                    ? "Sin conexión con PilotX"
                    : "Error: " + (string.IsNullOrWhiteSpace(r.Error) ? "desconocido" : r.Error);
                C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo cambiar la inversión del rolido"));
                C.Estado?.Invoke(msg, "err");
                return;
            }

            // El POST llevó el filtro: lo que estaba pendiente quedó guardado.
            _dirty = false;
            C.Estado?.Invoke(_invert ? "Rolido invertido ✔" : "Rolido normal ✔", "ok");

            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }
        }
        finally
        {
            _guardando = false;
            Pintar();
        }
    }

    // =======================================================================
    //  Pintura
    // =======================================================================

    /// <summary>`enter()`: los valores del snapshot a los controles. El label del
    /// cero se pinta SIEMPRE con imuPresente=true, igual que el JS: el "***" solo
    /// aparece después de una acción.</summary>
    private void PintarValores()
    {
        var z = C.Snap?.Rolido;

        if (_txtZero != null) PintarZero(z?.RollZero ?? 0, true);

        if (_slFiltro != null)
        {
            bool antes = _cargando;
            _cargando = true;
            try
            {
                double v = z?.RollFilter ?? 0;
                _slFiltro.Value = v < 0 ? 0 : v > 98 ? 98 : v;
            }
            finally { _cargando = antes; }
        }
        PintarPorcentaje();
    }

    /// <summary>`rlPintarZero(v, imuPresente)`: 2 decimales SIEMPRE, o "***"
    /// cuando el motor avisó que no hay dato de IMU.</summary>
    private void PintarZero(double v, bool imuPresente)
    {
        if (_txtZero == null) return;
        _txtZero.Text = imuPresente
            ? (CfgCtx.RedondeoJs(v * 100.0) / 100.0).ToString("0.00", Inv)
            : "***";
    }

    private void PintarPorcentaje()
    {
        if (_lblPct != null) _lblPct.Text = ValorFiltro().ToString("0", Inv) + "%";
    }

    private void Pintar()
    {
        bool editable = Editable() && !_guardando;

        if (_tileInvert != null)
        {
            _tileInvert.BorderBrush = _invert ? CfgUi.Verde : CfgUi.Borde;
            _tileInvert.Background = _invert ? CfgUi.BgFilaSel : CfgUi.BgFila;
            _tileInvert.Opacity = editable ? 1.0 : 0.55;
            _tileInvert.IsHitTestVisible = editable;
        }
        foreach (var b in _botones) b.IsEnabled = editable;
        if (_slFiltro != null) _slFiltro.IsEnabled = editable;
    }

    /// <summary>La barra es un entero 0..98 (el motor guarda barra × 0,01).</summary>
    private int ValorFiltro()
    {
        if (_slFiltro == null) return 0;
        int v = (int)Math.Round(_slFiltro.Value, MidpointRounding.AwayFromZero);
        return v < 0 ? 0 : v > 98 ? 98 : v;
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor y el POST iría al
    /// mismo Hub que no contesta. Sin la sección `rolido` tampoco se toca: el
    /// modelo local arrancaría en los defaults de C# (invert=false, filtro 0) y
    /// el primer toque guardaría ESO encima de la config real.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido && C.Snap?.Rolido != null;

    /// <summary>0 sin datos · 1 servicio caído · 2 respondió pero sin la sección
    /// `rolido` · 3 todo bien.</summary>
    private int EstadoActual()
        => C.SinDatos ? 0 : C.ServicioCaido ? 1 : C.Snap?.Rolido == null ? 2 : 3;

    // =======================================================================
    //  Íconos
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
        catch { bmp = null; }   // falta el asset ⇒ botón sin dibujo, nunca una excepción
        _iconos[nombre] = bmp;
        return bmp;
    }
}
