// ============================================================================
// MaquinaTab.cs — pestaña "Secciones › Máquina" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=amachine` (la sección data-tab="amachine" +
// tabs.amachine / maPintar() de config.js).
//
// QUÉ QUEDÓ NATIVO: las dos cartas (Levante hidráulico / Módulo) con el toggle
// de levante, los 3 tiempos, el toggle de invertir relés, los 4 bytes de
// usuario, el indicador de "pendiente", el botón "Enviar + Guardar" y la
// validación completa (POST /api/aog/config/maquina → PGN 238).
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas todavía sin portar (Pines
// relay, Rumbo, Rolido, U-Turn, Tram, Display, Botones).
//
// ---------------------------------------------------------------------------
// ESTA PESTAÑA NO GUARDA AL SALIR. NO LA "ARREGLES".
// ---------------------------------------------------------------------------
// Junto con Pines relay es la única de config.html cuya semántica es «Enviar +
// Guardar» explícito: salir sin enviar DESCARTA los cambios en silencio, igual
// que el tabAMachine nativo original y que `tabs.amachine.leave()` del JS (que
// solo esconde el pendiente y resuelve true).
// Por qué importa: este POST manda un PGN 238 al módulo de máquina. Si el panel
// copiara el "guardar al cerrar" de las pestañas hermanas, un operario que entró
// a MIRAR y rozó "Invertir relés" le daría vuelta los relés al módulo sin haber
// tocado nunca "Enviar + Guardar" — y se entera cuando la máquina aplica al
// revés en el lote. Cerrar = descartar, SIEMPRE.
// Por eso además `TieneGuardar` queda en false: el shell esconde su botón
// Guardar y el único guardado de la pantalla es el botón propio de acá. Si se
// pusiera en true, el shell llamaría a AlSalirAsync() (que descarta) y cantaría
// "Guardado ✔" sin haber mandado nada: una mentira sobre un PGN.
// La nota fija de la carta inferior es el aviso al operario; no hay diálogo de
// confirmación (no lo tiene ni el HTML ni el original, y un modal sobre la
// cabina está prohibido).
// ---------------------------------------------------------------------------
//
// Qué configuración toca (la trampa de las TRES superficies de config):
//   · SOLO /api/aog/config (la de PilotX / el guiado). NO toca /api/implemento
//     (surcos y semillas/ha de la pantalla de siembra) ni /api/tool.
//
// Qué son estos números, en criollo:
//   · Habilitar levante: bit 1 de `setArdMac_setting0`. Prende el control del
//     levante hidráulico de tres puntos por el módulo de máquina.
//   · Tiempo de subida / bajada (s): cuánto mantiene la salida el módulo para
//     subir / bajar el implemento. Bytes crudos 1..255 que viajan en el PGN.
//   · Anticipación del levante (s): con cuánta anticipación el motor pide el
//     levante. NO viaja en el PGN — es config local del motor
//     (_engine.Vehicle.hydLiftLookAheadTime).
//   · Invertir relés: bit 0 de `setArdMac_setting0`. Da vuelta la lógica de las
//     salidas del módulo. Mal puesto = secciones aplicando al revés.
//   · Usuario 1..4: bytes crudos 0..255 que interpreta el firmware del módulo
//     del usuario. El original no les pone unidad y acá tampoco: inventarles
//     una sería adivinar qué hace el firmware de otro.
//
// SEGUNDOS Y BYTES PUROS — ACÁ NO HAY CONVERSIÓN DE UNIDADES. `is_metric` NO
// participa (a diferencia de casi todas las hermanas). No meterle m2disp/disp2m
// "por consistencia": multiplicaría por 100 los tiempos del levante.
//
// Trampas cubiertas:
//   · Los 3 NUD del levante se DESHABILITAN con el levante apagado (réplica de
//     maPintar), PERO IGUAL SE VALIDAN Y SE ENVÍAN. El original manda los 7
//     números siempre; filtrarlos "prolijo" cambiaría el PGN 238 resultante.
//     Ojo: un campo gris puede quedar además en rojo (inválido) y bloquear el
//     envío entero — por eso el estilo inválido se ve también deshabilitado.
//   · Coma decimal: el operario escribe "2,5" ⇒ Replace(',', '.') +
//     TryParse(InvariantCulture). Con cultura es-AR, "1.5" se leería 15.
//   · Look-ahead mínimo 1, no 0: `leerNudDec(…, 1, 20)` clampea el 0 a 1. No
//     "arreglarlo" permitiendo 0 — el backend también clampea 1.0..20.0 y el
//     original nunca dejó desactivarlo por acá.
//   · El toggle del levante SWAPEA la imagen (SwitchOn/SwitchOff.png); el de
//     invertir relés NO (imagen fija, solo cambia el resaltado). Son distintos
//     en el HTML a propósito: no unificarlos.
//   · "Enviado al módulo ✔" significa que el PGN salió del motor, no que el
//     módulo lo tomó (SendPgnToLoop no confirma recepción). El texto del
//     original ya dice eso y no promete más.
//   · Se manda ESTADO, no deltas: los 9 campos siempre. Si alguien cambió la
//     config desde el celular mientras la pestaña estaba abierta, enviar pisa
//     esos cambios. Mismo límite que el HTML; se mitiga releyendo el snapshot
//     al entrar y después de cada envío OK.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — LOS 9 CAMPOS PERSISTEN. Verificado con REINICIO REAL del motor
// (banco, 2026-08-16), no con un round-trip: el GET arma el snapshot leyendo
// Settings.Default EN MEMORIA, así que "guardar y releer" no prueba nada.
// Método: POST → inspección del XML en disco → taskkill del motor → arranque
// limpio → GET, dos veces y con combinaciones ASIMÉTRICAS para descartar un
// "todo true" o un "quedó lo de antes":
//   1) {hyd_on:true, invert:true, raise 7, lower 11, look-ahead 3.7,
//      user 21/34/55/89}
//      → G:\Documentos\AgOpenGPS\Vehicles\PilotX.XML: setArdMac_setting0=3,
//        hydRaiseTime=7, hydLowerTime=11, hydraulicLiftLookAhead=3.7,
//        user1..4 = 21/34/55/89
//      → reinicio → GET devuelve exactamente eso.
//   2) {hyd_on:FALSE, invert:TRUE, raise 1, lower 255, look-ahead 1,
//      user 0/255/7/13} → setting0=1 en el XML → reinicio → GET idéntico
//      (los dos bits del bitfield se prueban por separado).
//   3) Envío hecho DESDE EL PANEL NATIVO (toggle de levante + botón) →
//      reinicio → hyd_on sigue en true.
// Depende de que haya perfil de vehículo: Settings.Save() escribe el XML SOLO
// si RegistrySettings.vehicleFileName no está vacío. Acá lo hay (el motor
// headless se crea el perfil "PilotX" al arrancar, Program.cs) y el archivo
// existe de verdad — a diferencia de lo que anotan PivoteTab/TimingTab, donde
// quien salva es tool.json. Ninguno de estos 9 campos vive en tool.json, así
// que acá NO aplica el cuelgue del implemento central: /api/implemento no toca
// ningún campo de máquina.
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
using Avalonia.Threading;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class MaquinaTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    /// <summary>Fondo del NUD deshabilitado (el `:disabled` del CSS).</summary>
    private static readonly IBrush BgNudOff = new SolidColorBrush(Color.Parse("#EEF1EE"));

    // ---- rangos (réplica EXACTA de btnMachineSend en config.js, que son los
    // mismos que clampea GuardarMaquina en el backend). MANTENER SINCRONIZADO:
    // cabina, celular y motor validan contra el mismo número.
    private static readonly (int min, int max) LimTiempo = (1, 255);
    private static readonly (double min, double max) LimLookAhead = (1.0, 20.0);
    private static readonly (int min, int max) LimUser = (0, 255);

    // ---- modelo local (el `ma` de config.js) -------------------------------
    private bool _hydOn, _invert;

    /// <summary>El `maPendiente` del HTML: hay cambios que el módulo todavía no
    /// tiene. Se prende con CUALQUIER cambio y se apaga al entrar, al salir
    /// (descarte) y tras un envío exitoso.</summary>
    private bool _pendiente;

    /// <summary>Hay un POST en vuelo: todo queda muerto (anti doble-tap).</summary>
    private bool _enviando;

    /// <summary>Estamos escribiendo los TextBox por código (carga o clamp): ese
    /// TextChanged no es un cambio del operario y no marca pendiente.</summary>
    private bool _cargando;

    private TextBox? _txtRaise, _txtLower, _txtLookAhead;
    private TextBox? _txtUser1, _txtUser2, _txtUser3, _txtUser4;

    private Border? _filaHyd, _filaInvert;
    private Image? _imgHyd, _imgPendiente;
    private Button? _btnEnviar;
    private TextBlock? _txtBtnEnviar;

    /// <summary>Vuelve el botón de "Enviado ✔" a su texto normal. Uno solo,
    /// reusado: dos envíos seguidos no dejan timers colgando.</summary>
    private DispatcherTimer? _timerEnviado;

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// sin sección / ok), para saber si el refresco de fondo tiene que
    /// reconstruir.</summary>
    private int _estadoPintado = -1;

    public MaquinaTab(CfgCtx c) : base(c) { }

    /// <summary>false A PROPÓSITO: el shell esconde su botón Guardar. El único
    /// guardado de esta pantalla es "Enviar + Guardar" (ver cabecera).</summary>
    public override bool TieneGuardar => false;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: repuebla TODO desde el snapshot (lo que se haya
    /// tocado sin enviar desaparece) y apaga el pendiente.</summary>
    public override Task AlEntrarAsync()
    {
        LeerDelSnapshot();
        _pendiente = false;
        Rebuild();
        return Task.CompletedTask;
    }

    /// <summary>`leave()`: NO GUARDA. Solo apaga el pendiente y deja navegar.
    /// Es la semántica de esta pestaña — ver el cartel de la cabecera antes de
    /// tocar esto.</summary>
    public override Task<bool> AlSalirAsync()
    {
        _pendiente = false;
        PintarPendiente();
        _ = C.Client?.TecladoAsync(false);
        return Task.FromResult(true);
    }

    /// <summary>Mapeo snapshot → modelo (el `enter()` del JS). Sin la sección no
    /// se inventan defaults: quedaría todo en cero y el primer envío pisaría la
    /// config real del módulo.</summary>
    private void LeerDelSnapshot()
    {
        var z = C.Snap?.Maquina;
        if (z == null) return;
        _hydOn = z.HydOn;
        _invert = z.InvertRelays;
    }

    /// <summary>Refresco de fondo (3 s). Esta pestaña tiene campos: reconstruir
    /// abajo del dedo tira el foco y cierra el teclado nativo. Y con cambios
    /// pendientes NO se toca nada — pisarlos sería exactamente el descarte
    /// silencioso que el operario todavía no pidió.</summary>
    public override void Live()
    {
        if (_estadoPintado == EstadoActual()) return;
        if (_pendiente || _enviando) return;
        if (AlgunCampoConFoco()) return;
        LeerDelSnapshot();
        Rebuild();
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _estadoPintado = EstadoActual();
        _filaHyd = _filaInvert = null;
        _imgHyd = _imgPendiente = null;
        _btnEnviar = null;
        _txtBtnEnviar = null;

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
        else if (C.Snap?.Maquina == null)
        {
            // El motor contestó ok pero sin la sección: se muestra lo último
            // conocido y NO se deja tocar (ver Editable()). Mandar un PGN 238
            // armado con los defaults de C# sería configurar el módulo a ciegas.
            Children.Add(CfgUi.ChipError("PilotX no informó la configuración del módulo de máquina", "AGP-NET-201"));
        }

        // Las dos cartas al lado (el `.dosCol` del HTML). WrapPanel: si la
        // pantalla de 10" no da el ancho, la segunda baja entera.
        var dosCol = CfgUi.Grilla();
        dosCol.Children.Add(CartaLevante());
        dosCol.Children.Add(CartaModulo());
        Children.Add(dosCol);

        Children.Add(CartaEnvio());

        PintarValores();
        // El pendiente se RECALCULA (no se hereda): recién acá los TextBox
        // tienen los valores del snapshot y la comparación tiene sentido.
        RecalcularPendiente();
        Pintar();
    }

    private Border CartaLevante()
    {
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(CfgUi.Titulo("Levante hidráulico"));

        // Único toggle de la pantalla con imagen DINÁMICA (On/Off).
        _filaHyd = FilaToggle("SwitchOff.png", "Habilitar levante", out _imgHyd,
                              () => { _hydOn = !_hydOn; });
        col.Children.Add(_filaHyd);

        _txtRaise     = Nud("Tiempo de subida");
        _txtLower     = Nud("Tiempo de bajada");
        _txtLookAhead = Nud("Anticipación del levante");

        col.Children.Add(FilaNud("ConMa_LiftRaiseTime.png", "Tiempo de subida", _txtRaise, "s"));
        col.Children.Add(FilaNud("ConMa_LiftLowerTime.png", "Tiempo de bajada", _txtLower, "s"));
        col.Children.Add(FilaNud(null, "Anticipación del levante", _txtLookAhead, "s"));

        return Carta(col);
    }

    private Border CartaModulo()
    {
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(CfgUi.Titulo("Módulo"));

        // Imagen FIJA: acá solo cambia el resaltado (réplica del HTML).
        _filaInvert = FilaToggle("ConSt_InvertRelay.png", "Invertir relés", out _,
                                 () => { _invert = !_invert; });
        col.Children.Add(_filaInvert);

        _txtUser1 = Nud("Usuario 1");
        _txtUser2 = Nud("Usuario 2");
        _txtUser3 = Nud("Usuario 3");
        _txtUser4 = Nud("Usuario 4");

        // Sin unidad: son bytes crudos del firmware del módulo.
        col.Children.Add(FilaNud(null, "Usuario 1", _txtUser1, null));
        col.Children.Add(FilaNud(null, "Usuario 2", _txtUser2, null));
        col.Children.Add(FilaNud(null, "Usuario 3", _txtUser3, null));
        col.Children.Add(FilaNud(null, "Usuario 4", _txtUser4, null));

        return Carta(col);
    }

    /// <summary>La carta inferior del HTML: el botón de envío, el indicador de
    /// pendiente y la leyenda fija del descarte.</summary>
    private Border CartaEnvio()
    {
        _txtBtnEnviar = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T("Enviar + Guardar"),
            Foreground = CfgUi.Texto, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var contenido = CfgUi.Fila(10);
        contenido.Children.Add(new Image
        {
            Source = Icono("ToolAcceptChange.png"),
            Width = 36, Height = 36,
            // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml (que la ventana
            // incluye para las barras del cockpit) trae un `Style
            // Selector="Image"` con máximos de 34 px que le gana al Width local.
            MaxWidth = 36, MaxHeight = 36,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        });
        contenido.Children.Add(_txtBtnEnviar);

        _btnEnviar = new Button
        {
            Content = contenido,
            MinHeight = 56, MinWidth = 124,
            Padding = new Thickness(14, 6, 16, 6),
            CornerRadius = new CornerRadius(10),
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _btnEnviar.Click += (_, __) => _ = EnviarAsync();

        _imgPendiente = new Image
        {
            Source = Icono("ConSt_Mandatory.png"),
            Width = 44, Height = 44, MaxWidth = 44, MaxHeight = 44,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        var fila = CfgUi.Fila(12);
        fila.Children.Add(_btnEnviar);
        fila.Children.Add(_imgPendiente);

        var pila = new StackPanel { Spacing = 8, MaxWidth = 660 };
        pila.Children.Add(fila);
        // Texto SIEMPRE visible, no condicionado al estado: es el único aviso
        // que tiene el operario de que cerrar sin enviar pierde lo tocado.
        pila.Children.Add(CfgUi.Nota("Salir sin enviar descarta los cambios (igual que el original)."));

        return CfgUi.Carta(pila);
    }

    /// <summary>Ancho fijo para que las dos cartas queden lado a lado y, cuando
    /// no entran, la segunda baje entera en vez de encogerse.</summary>
    private static Border Carta(Control contenido)
    {
        var b = CfgUi.Carta(contenido);
        b.Width = 340;
        b.Margin = new Thickness(0, 0, 12, 12);
        return b;
    }

    /// <summary>El `.chkimg.sw` del HTML: fila táctil con el ícono 52×52 a la
    /// izquierda y el rótulo al lado. `accion` solo toca el modelo local y marca
    /// pendiente — el envío va SIEMPRE por el botón "Enviar + Guardar".</summary>
    private Border FilaToggle(string icono, string caption, out Image? img, Action accion)
    {
        img = new Image
        {
            Source = Icono(icono),
            Width = 52, Height = 52, MaxWidth = 52, MaxHeight = 52,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var pila = CfgUi.Fila(10);
        pila.Children.Add(img);
        pila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(caption),
            Foreground = CfgUi.Texto, FontSize = 13, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        var borde = new Border
        {
            MinHeight = 56,
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 4, 10, 4),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };
        borde.Tapped += (_, __) =>
        {
            if (_enviando || !Editable()) return;
            accion();
            RecalcularPendiente();
            Pintar();
        };
        return borde;
    }

    /// <summary>El `.nudfila` del HTML: ícono opcional + rótulo + NUD + unidad
    /// opcional.</summary>
    private Control FilaNud(string? icono, string rotulo, TextBox nud, string? unidad)
    {
        var fila = CfgUi.Fila(8);

        if (icono != null)
            fila.Children.Add(new Image
            {
                Source = Icono(icono),
                Width = 44, Height = 44, MaxWidth = 44, MaxHeight = 44,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            });

        fila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(rotulo),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        fila.Children.Add(nud);

        if (unidad != null)
            fila.Children.Add(new TextBlock
            {
                Text = unidad,
                Foreground = CfgUi.TextoMuted, FontSize = 13, FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

        return fila;
    }

    /// <summary>El `.nud` del CSS: 22 px bold centrado, fondo aliceblue, alto
    /// táctil de 52. Va 110 de ancho y no los 130 de las pestañas hermanas: acá
    /// la fila lleva ADEMÁS el dibujo de 44 y la unidad, y con 130 no entra en
    /// la carta de 340 (la unidad "s" se salía). Pide el teclado nativo al
    /// enfocarse.</summary>
    private TextBox Nud(string titulo)
    {
        var t = new TextBox
        {
            Width = 110, MinHeight = 52,
            FontSize = 22, FontWeight = FontWeight.Bold,
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = BgNud, Foreground = CfgUi.Texto,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 2, 4, 2),
        };
        string tit = titulo;
        t.GotFocus  += (_, __) => _ = C.Client?.TecladoAsync(true, true, tit) ?? Task.CompletedTask;
        t.LostFocus += (_, __) => _ = C.Client?.TecladoAsync(false) ?? Task.CompletedTask;
        // El `input` del HTML. Ojo: acá el evento NO es el `input` del
        // navegador — Avalonia también dispara TextChanged al armar/desarmar el
        // template del TextBox, y hasta desde controles de un rebuild anterior
        // que ya no están en pantalla. Por eso NO se marca pendiente "porque
        // llegó un evento": se RECALCULA comparando contra el snapshot (ver
        // RecalcularPendiente). Si no, la pestaña se abría con el indicador
        // prendido y el botón resaltado sin que el operario tocara nada, justo
        // en la pantalla que manda un PGN al módulo.
        t.TextChanged += (_, __) => { if (!_cargando) RecalcularPendiente(); };
        return t;
    }

    /// <summary>
    /// El `maPendiente()` del HTML, pero CALCULADO en vez de "prendido por
    /// evento": pendiente = lo que hay en pantalla difiere de lo que informó el
    /// motor. Es más robusto (los TextChanged espurios de Avalonia no lo
    /// encienden) y más honesto: si los valores coinciden con el snapshot, el
    /// módulo YA los tiene y avisar de un pendiente sería mentir.
    /// Divergencia consciente con el HTML: allá cualquier tecleo prende el
    /// aviso aunque el valor vuelva al original. Acá vuelve a apagarse.
    /// NO llama a MarcarSucio del shell: el shell no tiene botón de guardado en
    /// esta pestaña (TieneGuardar=false) y encenderlo mostraría un "Guardar"
    /// que no guarda nada.
    /// </summary>
    private void RecalcularPendiente()
    {
        var z = C.Snap?.Maquina;
        bool cambio;
        if (z == null)
        {
            cambio = false;   // sin snapshot no hay contra qué comparar
        }
        else
        {
            cambio = _hydOn != z.HydOn
                  || _invert != z.InvertRelays
                  || Difiere(_txtRaise,     z.RaiseTime.ToString(Inv))
                  || Difiere(_txtLower,     z.LowerTime.ToString(Inv))
                  || Difiere(_txtLookAhead, Num(z.HydLiftLookAhead))
                  || Difiere(_txtUser1,     z.User1.ToString(Inv))
                  || Difiere(_txtUser2,     z.User2.ToString(Inv))
                  || Difiere(_txtUser3,     z.User3.ToString(Inv))
                  || Difiere(_txtUser4,     z.User4.ToString(Inv));
        }
        if (cambio == _pendiente) return;
        _pendiente = cambio;
        PintarPendiente();
        PintarBotonEnviar();
    }

    /// <summary>Comparación tolerante a la coma decimal y a los espacios: "3,7"
    /// y "3.7" son el mismo número para el operario.</summary>
    private static bool Difiere(TextBox? t, string valor)
        => t != null && (t.Text ?? "").Trim().Replace(',', '.') != valor;

    // =======================================================================
    //  Envío (réplica EXACTA de btnMachineSend)
    // =======================================================================

    private async Task EnviarAsync()
    {
        if (_enviando) return;
        if (!Editable())
        {
            // Mudo NO: el botón puede quedar habilitado si el servicio se cayó
            // mientras había cambios pendientes (Live() no rearma en ese caso,
            // justamente para no pisarlos). Tocarlo y que no pase nada le hace
            // creer al operario que el PGN salió.
            C.Estado?.Invoke(C.SinDatos ? "Sin conexión con PilotX"
                           : C.ServicioCaido ? "Servicio de configuración no disponible"
                           : "PilotX no informó la configuración del módulo de máquina", "err");
            return;
        }
        if (C.Client == null) { C.Estado?.Invoke("Sin conexión con PilotX", "err"); return; }

        // Se validan LOS 7, aunque los del levante estén deshabilitados: el
        // original los manda siempre (ver cabecera).
        int? raise = LeerNud(_txtRaise, LimTiempo);
        int? lower = LeerNud(_txtLower, LimTiempo);
        double? la = LeerNudDec(_txtLookAhead, LimLookAhead);
        int? u1 = LeerNud(_txtUser1, LimUser);
        int? u2 = LeerNud(_txtUser2, LimUser);
        int? u3 = LeerNud(_txtUser3, LimUser);
        int? u4 = LeerNud(_txtUser4, LimUser);

        if (raise == null || lower == null || la == null
            || u1 == null || u2 == null || u3 == null || u4 == null)
        {
            // El pendiente queda visible: hay cambios que el módulo no tiene.
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return;
        }

        _enviando = true;
        Pintar();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            // Los 9 campos SIEMPRE (el backend hace merge, pero el original
            // manda el estado completo y así queda el PGN 238 bien armado).
            var r = await C.Client.GuardarAsync("maquina", new
            {
                hyd_on = _hydOn,
                invert_relays = _invert,
                raise_time = raise.Value,
                lower_time = lower.Value,
                hyd_lift_look_ahead = la.Value,
                user1 = u1.Value,
                user2 = u2.Value,
                user3 = u3.Value,
                user4 = u4.Value,
            }).ConfigureAwait(true);

            if (r == null)
            {
                C.Estado?.Invoke("Sin conexión con PilotX", "err");
                return;
            }
            if (!r.Ok)
            {
                C.Estado?.Invoke("Error: " + (string.IsNullOrWhiteSpace(r.Error) ? "desconocido" : r.Error), "err");
                return;
            }

            // Texto propio de esta pestaña: "Enviado al módulo", no el "Guardado"
            // genérico. Y significa que el PGN SALIÓ del motor — no que el módulo
            // lo haya tomado (SendPgnToLoop no confirma recepción).
            _pendiente = false;
            C.Estado?.Invoke("Enviado al módulo ✔", "ok");
            AvisarEnviado();

            // Re-GET: el backend clampea server-side (1..255, 1..20). Sin esto
            // la cabina mostraría lo tipeado y el celular lo realmente guardado.
            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }
            // Si el re-GET NO contestó, C.Snap sigue siendo el de ANTES del
            // envío (el shell no borra el último snapshot bueno cuando el Hub
            // no responde). Repintar con eso le dejaría al operario los valores
            // VIEJOS abajo de un "Enviado al módulo ✔" — le diría que mandó 3 s
            // cuando mandó 7. En ese caso se deja en pantalla lo que salió en el
            // PGN y el pendiente apagado, que es la verdad.
            if (!C.RefrescoCaido)
            {
                LeerDelSnapshot();
                PintarValores();
                RecalcularPendiente();
            }
        }
        finally
        {
            _enviando = false;
            Pintar();
        }
    }

    /// <summary>Botón a "Enviado ✔" por 1,5 s (el `.ok` del botón flotante).</summary>
    private void AvisarEnviado()
    {
        if (_txtBtnEnviar == null) return;
        _txtBtnEnviar.Text = PilotX.Cockpit.Bars.Traductor.T("Enviado ✔");
        try { _timerEnviado?.Stop(); } catch { }
        _timerEnviado = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timerEnviado.Tick += (s, __) =>
        {
            try { (s as DispatcherTimer)?.Stop(); } catch { }
            if (_txtBtnEnviar != null)
                _txtBtnEnviar.Text = PilotX.Cockpit.Bars.Traductor.T("Enviar + Guardar");
        };
        _timerEnviado.Start();
    }

    // =======================================================================
    //  Validación (réplica de leerNud / leerNudDec)
    // =======================================================================

    /// <summary>
    /// `leerNud(input, min, max)` del JS: acepta coma decimal, marca en rojo lo
    /// que no es número, y para lo válido hace Math.abs, clampea al rango,
    /// redondea a entero y REESCRIBE el campo (así el operario ve con qué se
    /// envió).
    /// Divergencia consciente con parseFloat: "3s" acá es inválido (rojo) en vez
    /// de valer 3 — en una máquina que siembra es mejor preguntar que adivinar.
    /// </summary>
    private int? LeerNud(TextBox? t, (int min, int max) lim)
    {
        if (t == null) return null;
        string s = (t.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, Inv, out double v)
            || double.IsNaN(v) || double.IsInfinity(v))
        {
            Invalido(t, true);
            return null;
        }
        v = Math.Abs(v);                       // el `conSigno` del JS acá va en false
        if (v < lim.min) v = lim.min;
        if (v > lim.max) v = lim.max;
        // Math.round de JavaScript (mitades hacia +infinito), NO el Math.Round
        // de .NET (banqueros): con 2,5 el JS da 3 y .NET daría 2.
        int r = (int)CfgCtx.RedondeoJs(v);
        Invalido(t, false);
        SetTexto(t, r.ToString(Inv));
        return r;
    }

    /// <summary>`leerNudDec(input, min, max)` del JS: igual pero con UN decimal
    /// y SIN Math.abs — el mínimo es 1, así que el clamp ya se come lo negativo.
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
        v = CfgCtx.RedondeoJs(v * 10.0) / 10.0;
        Invalido(t, false);
        SetTexto(t, Num(v));
        return v;
    }

    /// <summary>Marca/desmarca el rojo. El estado queda en el Tag para que
    /// Apagar() pueda repintar el fondo sin perder el "inválido": un campo del
    /// levante puede estar gris Y rojo a la vez, y si el gris tapara el rojo el
    /// operario no vería por qué el envío no sale.</summary>
    private static void Invalido(TextBox t, bool mal)
    {
        t.Tag = mal ? "mal" : null;
        PintarFondo(t);
    }

    private static bool EsInvalido(TextBox t) => (t.Tag as string) == "mal";

    /// <summary>
    /// El `disabled` del HTML, hecho con IsReadOnly y NO con IsEnabled a
    /// propósito: el tema Fluent pinta el fondo/borde de un control
    /// deshabilitado por su cuenta y le gana a los colores locales, así que un
    /// campo deshabilitado Y en rojo perdía el rojo — el operario veía el envío
    /// bloqueado sin ninguna pista de cuál campo revisar. Con IsReadOnly el
    /// control sigue "habilitado" para el tema y los colores son nuestros; el
    /// Focusable/IsHitTestVisible en false lo dejan igual de intocable (y sin
    /// abrir el teclado nativo).
    /// </summary>
    private static void Apagar(TextBox t, bool apagado)
    {
        t.IsReadOnly = apagado;
        t.Focusable = !apagado;
        t.IsHitTestVisible = !apagado;
        PintarFondo(t);
    }

    private static void PintarFondo(TextBox t)
    {
        bool mal = EsInvalido(t);
        bool vivo = !t.IsReadOnly;
        t.BorderBrush = mal ? CfgUi.Err : CfgUi.Borde;
        t.BorderThickness = new Thickness(mal ? 2 : 1);
        t.Background = mal ? BgNudMal : (vivo ? BgNud : BgNudOff);
        t.Foreground = vivo ? CfgUi.Texto : CfgUi.TextoMuted;
    }

    /// <summary>Escritura por código: no cuenta como cambio del operario.</summary>
    private void SetTexto(TextBox t, string s)
    {
        bool antes = _cargando;
        _cargando = true;
        try { t.Text = s; }
        finally { _cargando = antes; }
    }

    /// <summary>Número como lo imprimiría JavaScript, SIEMPRE invariante
    /// (2 → "2", 2.5 → "2.5"). El punto es el separador del wire.</summary>
    private static string Num(double v) => v.ToString("0.####", Inv);

    // =======================================================================
    //  Pintura (el maPintar() del HTML, réplica exacta)
    // =======================================================================

    /// <summary>`enter()`: los 7 valores tal cual del snapshot, sin conversión.
    /// </summary>
    private void PintarValores()
    {
        var z = C.Snap?.Maquina;
        SetVal(_txtRaise,     z == null ? "" : z.RaiseTime.ToString(Inv));
        SetVal(_txtLower,     z == null ? "" : z.LowerTime.ToString(Inv));
        SetVal(_txtLookAhead, z == null ? "" : Num(z.HydLiftLookAhead));
        SetVal(_txtUser1,     z == null ? "" : z.User1.ToString(Inv));
        SetVal(_txtUser2,     z == null ? "" : z.User2.ToString(Inv));
        SetVal(_txtUser3,     z == null ? "" : z.User3.ToString(Inv));
        SetVal(_txtUser4,     z == null ? "" : z.User4.ToString(Inv));
    }

    private void SetVal(TextBox? t, string s)
    {
        if (t == null) return;
        SetTexto(t, s);
        Invalido(t, false);
    }

    private void Pintar()
    {
        bool editable = Editable() && !_enviando;

        // Toggle del levante: imagen DINÁMICA + resaltado.
        if (_imgHyd != null) _imgHyd.Source = Icono(_hydOn ? "SwitchOn.png" : "SwitchOff.png");
        PintarFila(_filaHyd, _hydOn, editable);
        // Toggle de invertir relés: imagen FIJA, solo resaltado.
        PintarFila(_filaInvert, _invert, editable);

        PintarHabilitado();
        PintarPendiente();
        PintarBotonEnviar();
    }

    private static void PintarFila(Border? b, bool sel, bool editable)
    {
        if (b == null) return;
        b.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
        b.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
        b.Opacity = editable ? 1.0 : 0.55;
        b.IsHitTestVisible = editable;
    }

    /// <summary>Réplica de maPintar: el levante apagado deshabilita sus 3 NUD.
    /// Los 4 de usuario NUNCA dependen del levante.</summary>
    private void PintarHabilitado()
    {
        bool editable = Editable() && !_enviando;
        foreach (var t in new[] { _txtRaise, _txtLower, _txtLookAhead })
        {
            if (t == null) continue;
            bool apagar = !(editable && _hydOn);
            // Si el campo que se apaga TENÍA el foco hay que cerrarle el teclado
            // a mano: Focusable=false no dispara LostFocus, así que el teclado
            // nativo quedaba abierto encima de un campo que ya no acepta nada
            // (el operario teclea y no pasa nada). Pasa siempre que se apaga el
            // levante con el cursor adentro de un tiempo.
            if (apagar && t.IsFocused) _ = C.Client?.TecladoAsync(false);
            Apagar(t, apagar);
        }
        foreach (var t in new[] { _txtUser1, _txtUser2, _txtUser3, _txtUser4 })
        {
            if (t == null) continue;
            Apagar(t, !editable);
        }
    }

    private void PintarPendiente()
    {
        if (_imgPendiente != null) _imgPendiente.IsVisible = _pendiente;
    }

    /// <summary>El botón hereda el rol del `.dirty` del flotante: con cambios
    /// pendientes se resalta en verde y en negrita.</summary>
    private void PintarBotonEnviar()
    {
        if (_btnEnviar == null) return;
        bool editable = Editable() && !_enviando;
        _btnEnviar.IsEnabled = editable;
        _btnEnviar.Opacity = editable ? 1.0 : 0.55;
        _btnEnviar.BorderBrush = _pendiente ? CfgUi.Verde : CfgUi.Borde;
        if (_txtBtnEnviar != null)
            _txtBtnEnviar.FontWeight = _pendiente ? FontWeight.Bold : FontWeight.SemiBold;
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor y el POST iría al
    /// mismo Hub que no contesta. Sin la sección `maquina` tampoco se toca: el
    /// modelo local arrancaría en los defaults de C# (todo cero/false) y el
    /// primer envío mandaría un PGN 238 que apaga el levante y pone los tiempos
    /// en el mínimo, sin que nadie lo haya pedido. El HTML directamente se rompe
    /// en ese caso (lee `snap.maquina.…`); acá se prefiere la pantalla muerta.
    /// </summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido && C.Snap?.Maquina != null;

    /// <summary>0 sin datos · 1 servicio caído · 2 respondió pero sin la sección
    /// `maquina` · 3 todo bien.</summary>
    private int EstadoActual()
        => C.SinDatos ? 0 : C.ServicioCaido ? 1 : C.Snap?.Maquina == null ? 2 : 3;

    private bool AlgunCampoConFoco()
        => (_txtRaise?.IsFocused ?? false)
        || (_txtLower?.IsFocused ?? false)
        || (_txtLookAhead?.IsFocused ?? false)
        || (_txtUser1?.IsFocused ?? false)
        || (_txtUser2?.IsFocused ?? false)
        || (_txtUser3?.IsFocused ?? false)
        || (_txtUser4?.IsFocused ?? false);

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
        catch { bmp = null; }   // falta el asset ⇒ fila sin dibujo, nunca una excepción
        _iconos[nombre] = bmp;
        return bmp;
    }
}
