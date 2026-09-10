// ============================================================================
// RumboTab.cs — pestaña "GPS / IMU › Rumbo" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=heading` (la sección data-tab="heading" +
// tabs.heading / hdPintar() / hdPintarFusion() de config.js).
//
// QUÉ QUEDÓ NATIVO: las cuatro cartas (Tipo de antena, Antena dual, Antena
// simple (Fix), Alarmas GPS) con sus 14 controles, la cascada de habilitación,
// los textos dinámicos de paso mínimo, la conversión km/h↔mph, la validación
// con rangos y el guardado (POST /api/aog/config/rumbo).
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (Rolido, U-Turn, Tram) más los módulos embebidos.
//
// Qué configura esta pantalla, en criollo: DE DÓNDE SACA EL RUMBO el tractor.
//   · Dual  = dos antenas GPS: el rumbo es real aunque el tractor esté parado.
//   · Fix   = una sola antena: el rumbo sale del movimiento, y por eso hay
//             "paso mínimo" (cuánto tiene que avanzar para creerle) y fusión
//             con la IMU (que sí sabe para dónde apunta estando quieto).
//   · Auto Dual ↔ Fix = por debajo de cierta velocidad usa Dual y por arriba
//             Fix (o al revés, según el motor): con eso activo NO se puede
//             forzar Fix a mano, porque el auto-switch le pelearía la fuente en
//             cada conmutación.
// Un rumbo mal configurado no es cosmético: el guiado sigue una línea con el
// tractor apuntando a otro lado, y eso se paga en el lote.
//
// Qué configuración toca (la trampa de las TRES superficies de config):
//   · SOLO /api/aog/config (la de PilotX / el guiado), sección "rumbo". NO
//     toca /api/implemento (surcos y semillas/ha de la pantalla de siembra) ni
//     /api/tool (ToolConfigDto camelCase del QuantiXEditor). Acá no hay nada
//     que espejar, pero conviene saberlo antes de "arreglar" una discrepancia.
//   · Los 11 campos viven en Settings (setGPS_*, setIMU_*, setAutoSwitch*,
//     setF_minHeadingStepDistance). NINGUNO baja a tool.json: quien los
//     persiste es Settings.Save() → <Documentos>\AgOpenGPS\Vehicles\<perfil>.XML.
//
// ---------------------------------------------------------------------------
// SEMÁNTICA DE GUARDADO MIXTA — ES DEL ORIGINAL, NO UN DESPROLIJO
//   1. Dual / Fix y "Paso mínimo" GUARDAN AL TOQUE (POST chico e inmediato:
//      {heading_source} o {min_gps_step}). Es la réplica exacta del FormConfig
//      viejo, que escribía el setting al togglear y cambiaba la fuente de rumbo
//      del motor EN CALIENTE, con el tractor andando. No se batchea ni se pide
//      confirmación: quien toca eso quiere el efecto ya. Si el POST falla se
//      REVIERTE el modelo y se avisa por toast (nunca modal).
//   2. Todo lo demás (auto-switch, reversa, RTK, RTK-frena, slider de fusión y
//      los 4 numéricos) va por el botón Guardar del shell / al salir de la
//      pestaña, igual que las hermanas y que el `leave()` del HTML.
// El body de leave manda SIEMPRE los 9 campos completos, aunque haya cambiado
// uno solo. No es paranoia: ver el bug del backend acá abajo.
// ---------------------------------------------------------------------------
//
// ⚠️ BUG DEL BACKEND (confirmado, EngineConfigVehiculoService.GuardarRumbo):
// al comentar las asignaciones "solo display" de RTK quedó una cadena de ifs
// anidados SIN LLAVES:
//     if (b.IsRtk.HasValue)
//         // comentado…
//         if (b.IsRtkKillAutosteer.HasValue)
//             // comentado…
//             if (b.JumpFixDistance.HasValue)
//                 s.setGPS_jumpFixAlarmDistance = Clamp(…);
// Consecuencias HOY:
//   · is_rtk e is_rtk_kill_autosteer NO SE PERSISTEN NUNCA (los dos toggles de
//     alarma vuelven al valor viejo apenas se relee el snapshot);
//   · jump_fix_distance solo se guarda si el body trae ADEMÁS los otros dos no
//     nulos ⇒ un cliente que mande {jump_fix_distance} suelto no guarda nada.
// Por eso el body va completo SIEMPRE y por eso, tras guardar, esta pestaña
// RELEE el snapshot y repinta: el toggle RTK "vuelve solo" a la vista del
// operario en vez de quedar en verde mintiendo que quedó activo.
// El arreglo (poner las llaves y restaurar s.setGPS_isRTK) es carril BACK-END y
// se coordina aparte por COORDINACION-SESIONES.md — no entra de contrabando en
// un porteo de UI. Hasta ese fix, esta pantalla va como `Prueba:` en el tablero.
//
// Trampas cubiertas:
//   · GUARD DE FIX: con auto-switch activo, tocar "Fix" NO hace nada (réplica).
//     El HTML le pone la clase `deshab` a #rbHeadFix pero NO existe regla CSS
//     `.radioimg.deshab`, así que allá el bloqueo no se VE. Acá se atenúa de
//     verdad (Opacity 0.45 + sin hit-test): divergencia deliberada, mejora
//     visual sin cambiar la semántica.
//   · VELOCIDAD DE CONMUTACIÓN: el wire es SIEMPRE km/h. La pantalla muestra
//     km/h o mph según is_metric con el MISMO factor del HTML (×0.621371 para
//     mostrar, ÷0.621371 para guardar). Un factor distinto o una doble
//     conversión desplaza la velocidad y el server clampea [1,10] km/h en
//     silencio: el operario no se entera de que quedó en otro número.
//   · FUSIÓN AL REVÉS: el valor de la barra (5..60) es el % GPS; el % IMU es
//     100 − v. "IMU" va a la IZQUIERDA (mínimo) y "GPS" a la DERECHA (máximo).
//     Etiquetarlo al revés hace que el operario mueva la fusión para el lado
//     contrario. La barra se habilita solo con IMU presente (runtime) o con
//     auto-switch activo (que la fuerza), igual que el HTML.
//   · TEXTOS DE PASO MÍNIMO: tabla FIJA de 4 combinaciones (5/10 cm ·
//     1.96/3.93 in · 50/100 cm · 19.68/39.3 in). No se calculan con un
//     formateador genérico: son los rótulos del original.
//   · CASCADA: las cartas Dual/Fix se ATENÚAN conservando sus valores (nunca se
//     resetean ni dejan de viajar en el body). Con auto-switch activo quedan
//     ACTIVAS LAS DOS a la vez.
//   · CLAMPS SILENCIOSOS: leerNud* clampea sin avisar (0.05 de reversa → 0.1) y
//     el server vuelve a clampear. Se replica el clamp local y se REESCRIBE el
//     campo, así lo que se ve es lo que quedó. Solo el texto que no parsea
//     bloquea en rojo y cancela la navegación.
//   · Coma decimal: el operario escribe "1,5" ⇒ replace + InvariantCulture en
//     los dos sentidos. Un double.Parse con cultura es-AR convierte 1.5 en 15.
//   · Salto de fix: es `leerNud` SIN signo ⇒ Math.abs antes de clampear, igual
//     que el JS (escribir −50 deja 50, no 0).
//   · imu_present es RUNTIME y se lee del snapshot: si la IMU aparece después
//     (CoreX conecta tarde), el refresco de fondo del shell la trae y la barra
//     se habilita sola en el siguiente Live() sin cambios pendientes.
//
// Quirks del HTML que NO se portan (a propósito):
//   · El botón flotante que queda pidiendo "Guardar" después de un POST
//     inmediato ya persistido: acá el flotante no existe (lo reemplaza el botón
//     Guardar del shell, que solo aparece si TieneGuardar).
//   · El redondeo asimétrico del enter (offset dual a 1 decimal en pantalla vs
//     2 en la validación): acá se muestra y se valida a 2 decimales, consistente.
//     El wire no cambia.
//   · config.js pone `hd.dirty = false` ANTES del POST, así que un guardado
//     fallido deja el segundo intento sin mandar nada. Acá el dirty se limpia
//     SOLO si el POST salió bien.
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO CON REINICIO REAL DEL MOTOR
// (banco, 2026-08-16). El método válido es POST → mirar el XML en disco →
// matar el motor → arranque limpio → GET; "guardar y releer" NO prueba nada,
// porque el GET arma el snapshot leyendo Settings EN MEMORIA.
// Se posteó a propósito un juego ASIMÉTRICO (nada de "todo true", que taparía
// un campo que no se escribe):
//   {heading_source:"Dual"} · {min_gps_step:true} · {fusion:47, is_rtk:true,
//    is_rtk_kill_autosteer:true, reverse_on:true, auto_switch_dual_fix:true,
//    auto_switch_speed:6.4, jump_fix_distance:37, dual_heading_offset:-12.34,
//    dual_reverse_distance:0.73}
// En G:\Documentos\AgOpenGPS\Vehicles\PilotX.XML quedaron: Dual ·
// minHeadingStepDistance 1 + minimumStepLimit 0.1 · fusionWeight2 0.094
// (= 47 × 0.002) · jumpFixAlarmDistance 37 · dualHeadingOffset −12.34 ·
// dualReverseDetectionDistance 0.73 · isReverseOn True · autoSwitchDualFixOn
// True · autoSwitchDualFixSpeed 6.4 — y setGPS_isRTK / setGPS_isRTK_KillAutoSteer
// en False, o sea SIN el true que se acababa de mandar.
// Tras matar el motor y levantarlo limpio, el GET devolvió exactamente esos
// mismos valores.
//   PERSISTEN (9 de 11): heading_source, min_gps_step, fusion, jump_fix_distance,
//   dual_heading_offset, dual_reverse_distance, reverse_on,
//   auto_switch_dual_fix, auto_switch_speed.
//   NO PERSISTEN (2): is_rtk e is_rtk_kill_autosteer — por el bug de las llaves
//   descrito arriba. Se posteean igual (el body completo los necesita para que
//   el salto de fix se guarde) y tras guardar se relee el snapshot, así el
//   operario VE que el toggle volvió atrás en vez de creerle a un "Guardado ✔".
// El corolario del bug también se comprobó en el mismo banco:
//   POST {"jump_fix_distance":99} SUELTO → responde ok:true y NO guarda nada
//   (el XML y el GET siguieron en 37). Por eso el body de leave va completo.
// Acá SÍ persiste Settings.Save(): el perfil existe (RegistrySettings.vehicleFileName
// = "PilotX", creado por Program.cs al arrancar) y el XML se escribe en
// <Documentos>\AgOpenGPS\Vehicles\PilotX.XML. Ninguno de estos 11 campos vive
// en tool.json — si algún día el motor arranca sin perfil, esta pestaña entera
// pasa a cantar un "Guardado ✔" que dura hasta el próximo arranque.
//
// PRUEBA DE PANTALLA (banco, 2026-08-16, PilotX.Desktop contra el motor real):
//   · tocar «Dual» ⇒ el GET devuelve "Dual" al instante y la cascada se da
//     vuelta sola (se activa la carta dual, se atenúa la simple);
//   · «Auto Dual ↔ Fix» + Guardar ⇒ auto_switch_dual_fix true, las DOS cartas
//     quedan activas y la barra de fusión se habilita aunque no haya IMU;
//   · «Alarma RTK» + Guardar ⇒ el toggle se prende, y tras la relectura VUELVE
//     A APAGARSE en pantalla: la mitigación del bug se ve funcionando.
// Falta la validación en cabina CON ANTENA DUAL REAL: en el tablero va `Prueba:`.
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

public sealed class RumboTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS).</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));

    /// <summary>Fondo del NUD inválido (el `#fdf0ee` del CSS).</summary>
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    /// <summary>Factor mph↔km/h EXACTO del HTML. No redondearlo ni cambiarlo por
    /// 0.6214: el server clampea [1,10] km/h en silencio y la diferencia se
    /// come el borde del rango sin avisarle a nadie.</summary>
    private const double Mph = 0.621371;

    // ---- rangos (réplica EXACTA de tabs.heading de config.js, que son los
    // mismos del backend GuardarRumbo). MANTENER SINCRONIZADO: cabina, celular
    // y motor validan contra el mismo número.
    private static readonly (double min, double max) LimOffset = (-100.0, 100.0);   // grados
    private static readonly (double min, double max) LimReversa = (0.1, 0.9);       // m
    private static readonly (double min, double max) LimVelKmh = (1.0, 10.0);       // km/h
    private static readonly (double min, double max) LimVelMph = (0.62, 6.21);      // mph
    private static readonly (int min, int max) LimSalto = (0, 1000);                // cm

    /// <summary>Una fila táctil (el `.chkimg.sw` del HTML) o un radio-imagen
    /// (`.radioimg`). `Sel` dice si va con el borde verde.</summary>
    private sealed class Fila
    {
        public Border Borde = null!;
        public Func<bool> Sel = () => false;
    }

    // ---- modelo local (el `hd` de config.js) -------------------------------
    private string _source = "Fix";     // "Fix" | "Dual"
    private bool _minStep, _autoSwitch, _rtk, _rtkKill, _reverse, _curva;
    private bool _imu;                  // runtime, solo lectura: habilita la barra
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: todo queda muerto (anti doble-tap y anti
    /// "sigo tipeando mientras se guarda").</summary>
    private bool _guardando;

    /// <summary>Estamos escribiendo por código (carga o clamp): ese cambio NO es
    /// del operario y no tiene que ensuciar la pestaña.</summary>
    private bool _cargando;

    private TextBox? _txtOffset, _txtReversa, _txtVel, _txtSalto;
    private Slider? _slFusion;
    private TextBlock? _lblVelUnidad, _lblPasoMin, _lblDistRumbo, _lblFusion;
    private Border? _cartaDual, _cartaSingle, _tileDual, _tileFix;

    private readonly List<Fila> _tiles = new List<Fila>();
    private readonly List<Fila> _filas = new List<Fila>();

    /// <summary>Estado con el que se armó el árbol, para que el refresco de
    /// fondo sepa si tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public RumboTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>El shell pregunta esto antes de cantar "Guardado ✔": tocar
    /// Guardar sin haber cambiado nada no manda ningún POST.</summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: lee el snapshot, repinta y descarta cualquier cambio
    /// sin guardar, igual que el HTML.</summary>
    public override Task AlEntrarAsync()
    {
        LeerDelSnapshot();
        _dirty = false;
        Rebuild();
        return Task.CompletedTask;
    }

    /// <summary>`leave()`: valida los 4 numéricos y guarda si hay cambios.
    /// false CANCELA la navegación — el operario se queda acá con el campo en
    /// rojo a la vista.</summary>
    public override async Task<bool> AlSalirAsync()
    {
        if (!_dirty) return true;
        if (C.Client == null) { C.Estado?.Invoke("Sin conexión con PilotX", "err"); return false; }
        // POST en vuelo (el inmediato del tipo de antena o del paso mínimo):
        // cancelar la navegación EN SILENCIO deja al operario tocando "Rolido"
        // sin que pase nada y sin una sola línea que lo explique. Se avisa.
        if (_guardando) { C.Estado?.Invoke("Esperá, se está guardando…", ""); return false; }

        double? off = LeerNudDec2(_txtOffset, LimOffset);
        double? rev = LeerNudDec2(_txtReversa, LimReversa);
        var limVel = C.IsMetric ? LimVelKmh : LimVelMph;
        double? vel = LeerNudDec(_txtVel, limVel);
        int? salto = LeerNudEntero(_txtSalto, LimSalto);

        if (off == null || rev == null || vel == null || salto == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        // El wire es SIEMPRE km/h: si la pantalla está en imperial, lo que el
        // operario escribió está en mph y hay que devolverlo a km/h ANTES de
        // postear (mismo factor que el HTML, ver cabecera).
        double velKmh = C.IsMetric ? vel.Value : vel.Value / Mph;

        _guardando = true;
        Pintar();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            // Body EXACTO del `leave()` del HTML: los 9 campos SIEMPRE, aunque
            // haya cambiado uno solo. NO es redundancia — con el bug de las
            // llaves del backend, si is_rtk o is_rtk_kill_autosteer faltan, el
            // salto de fix tampoco se guarda (ver cabecera).
            // heading_source y min_gps_step NO van acá: esos ya se guardaron al
            // toque, como en el original.
            var r = await C.Client.GuardarAsync("rumbo", new
            {
                fusion = ValorFusion(),
                is_rtk = _rtk,
                is_rtk_kill_autosteer = _rtkKill,
                reverse_on = _reverse,
                curve_speed_comp = _curva,
                auto_switch_dual_fix = _autoSwitch,
                auto_switch_speed = velKmh,
                jump_fix_distance = salto.Value,
                dual_heading_offset = off.Value,
                dual_reverse_distance = rev.Value,
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

            // Relectura OBLIGATORIA: el motor clampea de su lado y —sobre todo—
            // hoy NO persiste los dos toggles de RTK. Repintar con lo que
            // devuelve el GET hace que el operario VEA que volvieron atrás, en
            // vez de mirar un verde que ya no existe en el motor.
            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }

            LeerDelSnapshot();
            PintarValores();
            Pintar();
            return true;
        }
        finally
        {
            _guardando = false;
            Pintar();
        }
    }

    /// <summary>Mapeo snapshot → modelo (el `enter()` del JS). Sin la sección
    /// no se inventan defaults: se deja lo que había.</summary>
    private void LeerDelSnapshot()
    {
        var z = C.Snap?.Rumbo;
        if (z == null) return;
        // Cualquier string que no sea "Dual" cae a "Fix", igual que el JS.
        _source = z.HeadingSource == "Dual" ? "Dual" : "Fix";
        _minStep = z.MinGpsStep;
        _autoSwitch = z.AutoSwitchDualFix;
        _rtk = z.IsRtk;
        _rtkKill = z.IsRtkKillAutosteer;
        _reverse = z.ReverseOn;
        _curva = z.CurveSpeedComp;
        _imu = z.ImuPresent;
    }

    /// <summary>Refresco de fondo (3 s). Esta pestaña tiene campos: reconstruir
    /// abajo del dedo tira el foco y cierra el teclado nativo. Solo se rearma si
    /// cambió el estado de conexión y nadie está tipeando ni tiene cambios sin
    /// guardar. Sin cambios pendientes SÍ se resincroniza el modelo (alguien
    /// pudo tocar la config desde el celular, y `imu_present` es runtime: la IMU
    /// puede aparecer después de abrir la pestaña).</summary>
    public override void Live()
    {
        if (_estadoPintado != EstadoActual())
        {
            if (_dirty || _guardando || AlgunCampoConFoco()) return;
            LeerDelSnapshot();
            Rebuild();
            return;
        }
        if (_dirty || _guardando || AlgunCampoConFoco()) return;
        if (!DifiereDelSnapshot()) return;
        LeerDelSnapshot();
        PintarValores();
        Pintar();
    }

    /// <summary>¿El snapshot dice algo distinto de lo que hay en pantalla?
    /// (solo los booleanos + imu_present: los numéricos se comparan por texto y
    /// darían falsos positivos por redondeo).</summary>
    private bool DifiereDelSnapshot()
    {
        var z = C.Snap?.Rumbo;
        if (z == null) return false;
        return (z.HeadingSource == "Dual" ? "Dual" : "Fix") != _source
            || z.MinGpsStep != _minStep
            || z.AutoSwitchDualFix != _autoSwitch
            || z.IsRtk != _rtk
            || z.IsRtkKillAutosteer != _rtkKill
            || z.ReverseOn != _reverse
            || z.ImuPresent != _imu;
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _tiles.Clear();
        _filas.Clear();
        _estadoPintado = EstadoActual();

        // MaxWidth + Left OBLIGATORIOS, las dos cosas. El TabHost cuelga de un
        // ScrollViewer con scroll HORIZONTAL, así que sin un tope el panel mide
        // "infinito": la nota de abajo (TextBlock con Wrap) se estira a una sola
        // línea larguísima, el contenido queda de ~1200 px y las cartas se van
        // de la pantalla. Y con Stretch (el default de Avalonia) un hijo con
        // MaxWidth se CENTRA en el sobrante en vez de arrancar a la izquierda:
        // sin el Left, las cartas aparecían corridas a la derecha y cortadas.
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
        else if (C.Snap?.Rumbo == null)
        {
            // El motor contestó ok pero sin la sección: se muestra lo último
            // conocido y NO se deja tocar (ver Editable()).
            Children.Add(CfgUi.ChipError("PilotX no informó la configuración de rumbo", "AGP-NET-201"));
        }

        Children.Add(CartaTipoAntena());

        // Las dos cartas al lado (el `.dosCol` del HTML). MaxWidth OBLIGATORIO:
        // el TabHost cuelga de un ScrollViewer con scroll horizontal, así que
        // sin un tope el WrapPanel mide "infinito" y nada envuelve.
        var dosCol = CfgUi.Grilla();
        dosCol.HorizontalAlignment = HorizontalAlignment.Left;
        dosCol.Children.Add(CartaDual());
        dosCol.Children.Add(CartaSimple());
        Children.Add(dosCol);

        Children.Add(CartaAlarmas());

        Children.Add(CfgUi.Nota(
            "«Dual» usa dos antenas y sabe para dónde apunta el tractor aunque esté parado; "
            + "«Fix» lo deduce del movimiento y por eso necesita paso mínimo y fusión con la IMU. "
            + "Con «Auto Dual ↔ Fix» activo no se puede forzar Fix a mano."));

        PintarValores();
        Pintar();
    }

    // ---- carta 1: tipo de antena -------------------------------------------

    private Border CartaTipoAntena()
    {
        var col = new StackPanel { Spacing = 10 };
        col.Children.Add(CfgUi.Titulo("Tipo de antena"));

        var fila = CfgUi.Grilla();
        fila.HorizontalAlignment = HorizontalAlignment.Left;
        _tileDual = Tile("Con_SourcesGPSDual.png", "Dual", () => _source == "Dual", ElegirDual);
        _tileFix = Tile("Con_SourcesGPSSingle.png", "Fix", () => _source == "Fix", ElegirFix);
        fila.Children.Add(_tileDual);
        fila.Children.Add(_tileFix);
        col.Children.Add(fila);

        col.Children.Add(CfgUi.Nota(
            "Cambiar la fuente de rumbo se aplica AL TOQUE, con el tractor andando: "
            + "es el comportamiento del original."));

        return CfgUi.Carta(col);
    }

    /// <summary>El `.radioimg.dircol` del HTML: ícono arriba, rótulo abajo.</summary>
    private Border Tile(string icono, string caption, Func<bool> sel, Action accion)
    {
        var pila = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // El PNG viene con fondo blanco: recuadro blanco para que la card
        // elegida (fondo verde claro) no muestre un rectángulo suelto.
        pila.Children.Add(new Border
        {
            Background = CfgUi.BgFila,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = new Image
            {
                Source = Icono(icono),
                Width = 86, Height = 58,
                // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml (que la ventana
                // incluye para las barras del cockpit) trae un
                // `Style Selector="Image"` con máximos de 34 px que le gana al
                // Width local. Sin esto el pictograma sale de estampilla.
                MaxWidth = 86, MaxHeight = 58,
                Stretch = Stretch.Uniform,
            },
        });

        pila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(caption),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var card = new Border
        {
            Width = 124, Height = 112,
            Margin = new Thickness(0, 0, 10, 0),
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };
        card.Tapped += (_, __) => accion();

        _tiles.Add(new Fila { Borde = card, Sel = sel });
        return card;
    }

    // ---- carta 2: antena dual ----------------------------------------------

    private Border CartaDual()
    {
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(CfgUi.Titulo("Antena dual"));

        // En el HTML el dibujo va al costado (flex-wrap); en una pantalla de 10"
        // al costado no entra sin comerse el ancho de los campos, así que va
        // arriba y centrado. Es adaptación de layout, no de semántica.
        col.Children.Add(new Border
        {
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image
            {
                Source = Icono("Con_SourcesHead.png"),
                Height = 104, MaxHeight = 104, MaxWidth = 240,
                Stretch = Stretch.Uniform,
            },
        });

        _txtOffset = Nud("Offset de rumbo");
        _txtReversa = Nud("Distancia de reversa");
        _txtVel = Nud("Velocidad de conmutación");
        _lblVelUnidad = Unidad(C.IsMetric ? "km/h" : "mph");

        col.Children.Add(FilaNud("Offset de rumbo", _txtOffset, Unidad("°")));
        col.Children.Add(FilaNud("Distancia de reversa", _txtReversa, Unidad("m")));

        col.Children.Add(FilaToggle("Con_SourcesGPSDual.png", "Auto Dual ↔ Fix",
            () => _autoSwitch, () => { _autoSwitch = !_autoSwitch; }));

        col.Children.Add(FilaNud("Velocidad de conmutación", _txtVel, _lblVelUnidad));

        _cartaDual = Carta(col);
        return _cartaDual;
    }

    // ---- carta 3: antena simple (Fix) --------------------------------------

    private Border CartaSimple()
    {
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(CfgUi.Titulo("Antena simple (Fix)"));

        // Paso mínimo: ÚNICO toggle de esta carta que guarda AL TOQUE (réplica).
        var filaPaso = FilaToggle("ConS_SourceFix.png", "Paso mínimo", () => _minStep, null);
        // El caption de esta fila es DINÁMICO (cambia con el valor y con las
        // unidades), así que se saca el TextBlock de adentro de la fila para
        // reescribirlo en PintarPasoMinimo. El pictograma es fijo: la selección
        // la marca el borde verde, igual que en las hermanas.
        _lblPasoMin = (TextBlock)((StackPanel)filaPaso.Child!).Children[1];
        filaPaso.Tapped += (_, __) => TocarPasoMinimo();
        col.Children.Add(filaPaso);

        _lblDistRumbo = CfgUi.Nota("Distancia de rumbo: —");
        col.Children.Add(_lblDistRumbo);

        // Barra de fusión: el valor ES el % GPS. "IMU" a la izquierda (mínimo),
        // "GPS" a la derecha (máximo) — ver la trampa R8 de la cabecera.
        _slFusion = new Slider
        {
            Minimum = 5, Maximum = 60,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 5,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = CfgUi.Verde,
        };
        _slFusion.ValueChanged += (_, __) =>
        {
            if (_cargando) return;
            Ensuciar();
            PintarFusion();
        };

        var filaBarra = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var izq = Unidad("IMU");
        var der = Unidad("GPS");
        Grid.SetColumn(izq, 0);
        Grid.SetColumn(_slFusion, 1);
        Grid.SetColumn(der, 2);
        filaBarra.Children.Add(izq);
        filaBarra.Children.Add(_slFusion);
        filaBarra.Children.Add(der);
        col.Children.Add(filaBarra);

        _lblFusion = CfgUi.Nota("Fusión: —");
        col.Children.Add(_lblFusion);

        col.Children.Add(FilaToggle("YouTurnReverse.png", "Detección de reversa",
            () => _reverse, () => { _reverse = !_reverse; }));

        // Compensar velocidad por sección en curva. Va acá y no en QuantiX
        // porque abarca a todos los módulos que dosifican por velocidad de
        // sección (QuantiX, SectionX…). Pedido 2026-09-09 (Gringas): con un
        // receptor sin compensación de terreno el rumbo vibra y la dosis de
        // cada surco se movía todo el tiempo.
        col.Children.Add(FilaToggle("SectionOnLookAhead.png", "Compensar velocidad por sección en curva",
            () => _curva, () => { _curva = !_curva; }));
        col.Children.Add(CfgUi.Nota("Prendido: en curva la sección de afuera va más rápido y recibe más dosis. "
                                  + "Apagado: todos los motores reciben la misma velocidad y la misma dosis y "
                                  + "prenden y apagan juntos (una sola posición para todo el implemento). "
                                  + "Apagalo si el GPS no compensa terreno y la dosis varía sola."));

        _cartaSingle = Carta(col);
        return _cartaSingle;
    }

    // ---- carta 4: alarmas GPS ----------------------------------------------

    private Border CartaAlarmas()
    {
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(CfgUi.Titulo("Alarmas GPS"));

        _txtSalto = Nud("Salto de fix");
        col.Children.Add(FilaNud("Salto de fix", _txtSalto, Unidad("cm (0 desactiva)")));

        var filas = CfgUi.Grilla();
        filas.HorizontalAlignment = HorizontalAlignment.Left;
        filas.Children.Add(Ancho(FilaToggle("Con_SourcesRTKAlarm.png", "Alarma RTK",
            () => _rtk, () => { _rtk = !_rtk; })));
        filas.Children.Add(Ancho(FilaToggle("AutoSteerOff.png", "La alarma frena el autoguiado",
            () => _rtkKill, () => { _rtkKill = !_rtkKill; })));
        col.Children.Add(filas);

        col.Children.Add(CfgUi.Nota(
            "Estos dos toggles todavía NO los persiste el motor (bug conocido del backend, "
            + "ver la cabecera del código): al guardar vuelven al valor anterior."));

        return CfgUi.Carta(col);
    }

    private static Border Ancho(Border b)
    {
        b.Width = 300;
        b.Margin = new Thickness(0, 0, 10, 10);
        return b;
    }

    // ---- piezas comunes ----------------------------------------------------

    /// <summary>Ancho fijo para que las dos cartas queden lado a lado y, cuando
    /// no entran, la segunda baje entera en vez de encogerse.</summary>
    private static Border Carta(Control contenido)
    {
        var b = CfgUi.Carta(contenido);
        b.Width = 326;
        b.Margin = new Thickness(0, 0, 10, 10);
        return b;
    }

    /// <summary>El `.chkimg.sw` del HTML: fila táctil con el ícono a la
    /// izquierda y el rótulo al lado. `accion` (si viene) solo toca el modelo
    /// local y marca sucio — el guardado va por el botón Guardar del shell o al
    /// salir de la pestaña. La fila de Paso mínimo pasa `null` y engancha su
    /// propio handler, porque esa SÍ guarda al toque.</summary>
    private Border FilaToggle(string icono, string caption, Func<bool> sel, Action? accion)
    {
        var img = new Image
        {
            Source = Icono(icono),
            Width = 44, Height = 44,
            // MaxWidth/MaxHeight EXPLÍCITOS: ver el comentario de Tile().
            MaxWidth = 44, MaxHeight = 44,
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
        if (accion != null) borde.Tapped += (_, __) => Tocar(accion);

        _filas.Add(new Fila { Borde = borde, Sel = sel });
        return borde;
    }

    /// <summary>La `.nudfila` del HTML: rótulo, NUD y unidad en una línea.</summary>
    private static Control FilaNud(string etiqueta, TextBox nud, TextBlock unidad)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var lbl = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(etiqueta),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
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

    private static TextBlock Unidad(string t) => new TextBlock
    {
        Text = PilotX.Cockpit.Bars.Traductor.T(t),
        Foreground = CfgUi.TextoDim, FontSize = 12, FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>El `.nud` del CSS: 22 px bold centrado, fondo aliceblue, alto
    /// táctil. Pide el teclado nativo al enfocarse.</summary>
    private TextBox Nud(string titulo)
    {
        var t = new TextBox
        {
            Width = 104, MinHeight = 52,
            FontSize = 20, FontWeight = FontWeight.Bold,
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = BgNud, Foreground = CfgUi.Texto,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 2, 4, 2),
        };
        string tit = titulo;
        t.GotFocus += (_, __) => _ = C.Client?.TecladoAsync(true, true, tit) ?? Task.CompletedTask;
        t.LostFocus += (_, __) => _ = C.Client?.TecladoAsync(false) ?? Task.CompletedTask;
        t.TextChanged += (_, __) => { if (!_cargando) Ensuciar(); };
        return t;
    }

    // =======================================================================
    //  Acciones
    // =======================================================================

    private void Ensuciar()
    {
        _dirty = true;
        C.MarcarSucio?.Invoke();
    }

    /// <summary>Toggle común: toca el modelo, marca sucio y repinta. NO postea.</summary>
    private void Tocar(Action accion)
    {
        if (_guardando) return;
        if (!Editable()) return;
        accion();
        Ensuciar();
        Pintar();
    }

    /// <summary>Click en «Dual»: efecto INMEDIATO del original (POST chico que
    /// cambia la fuente de rumbo del motor en caliente). Si ya es Dual no hace
    /// nada.</summary>
    private void ElegirDual()
    {
        if (_guardando || !Editable()) return;
        if (_source == "Dual") return;
        _ = CambiarFuenteAsync("Dual");
    }

    /// <summary>Click en «Fix»: mismo POST inmediato, pero con el GUARD del
    /// original — con auto-switch activo NO se puede forzar Fix a mano (le
    /// pelearía la fuente al auto-switch en cada conmutación).</summary>
    private void ElegirFix()
    {
        if (_guardando || !Editable()) return;
        if (_source == "Fix" || _autoSwitch) return;
        _ = CambiarFuenteAsync("Fix");
    }

    private async Task CambiarFuenteAsync(string fuente)
    {
        string antes = _source;
        _source = fuente;
        _guardando = true;
        Pintar();                     // el operario ve el cambio YA (la cascada de cartas también)
        try
        {
            var r = C.Client == null
                ? null
                : await C.Client.GuardarAsync("rumbo", new { heading_source = fuente }).ConfigureAwait(true);
            if (r == null || !r.Ok)
            {
                // Revertir: el motor NO cambió de fuente y dejarlo marcado sería
                // mentirle al operario sobre de dónde saca el rumbo.
                _source = antes;
                C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo cambiar la fuente de rumbo"));
                C.Estado?.Invoke("No se pudo cambiar la fuente de rumbo", "err");
            }
            else await ResincronizarAsync().ConfigureAwait(true);
        }
        finally
        {
            _guardando = false;
            Pintar();
        }
    }

    /// <summary>«Paso mínimo»: el otro control que guarda AL TOQUE (el original
    /// escribe el setting al togglear y además fuerza el recálculo de rumbo del
    /// motor).</summary>
    private void TocarPasoMinimo()
    {
        if (_guardando || !Editable()) return;
        _ = CambiarPasoMinimoAsync();
    }

    private async Task CambiarPasoMinimoAsync()
    {
        bool antes = _minStep;
        _minStep = !_minStep;
        _guardando = true;
        PintarPasoMinimo();
        Pintar();
        try
        {
            var r = C.Client == null
                ? null
                : await C.Client.GuardarAsync("rumbo", new { min_gps_step = _minStep }).ConfigureAwait(true);
            if (r == null || !r.Ok)
            {
                _minStep = antes;
                C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo cambiar el paso mínimo"));
                C.Estado?.Invoke("No se pudo cambiar el paso mínimo", "err");
            }
            else await ResincronizarAsync().ConfigureAwait(true);
        }
        finally
        {
            _guardando = false;
            PintarPasoMinimo();
            Pintar();
        }
    }

    /// <summary>
    /// Re-lee el snapshot después de un POST INMEDIATO que salió bien.
    /// No es cosmético: el shell refresca cada 3 s y `Live()` compara el modelo
    /// contra `C.Snap`. Si el GET del tick ya estaba en vuelo cuando el POST
    /// terminó, ese GET vuelve con el valor VIEJO y `Live()` daría vuelta la
    /// pantalla sola — el tile de antena volvería a "Fix" y con él toda la
    /// cascada, tres segundos, sin que nadie haya tocado nada. Dejar `C.Snap`
    /// alineado con lo que el motor acaba de aceptar cierra esa ventana.
    /// Se llama con `_guardando` todavía en true, así el tick no se cuela en el
    /// medio. NO toca el modelo local: puede haber ediciones sin guardar en los
    /// numéricos y pisarlas sería perderle el trabajo al operario.
    /// </summary>
    private async Task ResincronizarAsync()
    {
        if (C.RefrescarSnapshot == null) return;
        try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch { }
    }

    // =======================================================================
    //  Validación (réplica de leerNud / leerNudDec / leerNudDec2)
    // =======================================================================

    /// <summary>`leerNudDec2(input, min, max)`: acepta coma decimal, marca en
    /// rojo lo que no es número, y para lo válido clampea al rango y redondea a
    /// DOS decimales REESCRIBIENDO el campo, así el operario ve con qué se
    /// guardó. Conserva el signo (el offset de rumbo puede ser negativo).</summary>
    private double? LeerNudDec2(TextBox? t, (double min, double max) lim)
        => LeerNud(t, lim, 100.0, false);

    /// <summary>`leerNudDec(input, min, max)`: idem pero a UN decimal.</summary>
    private double? LeerNudDec(TextBox? t, (double min, double max) lim)
        => LeerNud(t, lim, 10.0, false);

    /// <summary>`leerNud(input, min, max)` SIN `conSigno`: entero y con
    /// Math.abs ANTES de clampear (escribir −50 deja 50, no 0), igual que el JS.
    /// </summary>
    private int? LeerNudEntero(TextBox? t, (int min, int max) lim)
    {
        double? v = LeerNud(t, (lim.min, lim.max), 1.0, true);
        return v == null ? (int?)null : (int)v.Value;
    }

    /// <summary>
    /// Núcleo de los tres helpers del JS. `escala` es 100/10/1 según los
    /// decimales; `abs` replica el `if (!conSigno) v = Math.abs(v)`.
    /// Divergencia consciente con parseFloat: "1,5 m" acá es inválido (rojo) en
    /// vez de valer 1,5 — en una máquina que siembra es mejor preguntar que
    /// adivinar.
    /// </summary>
    private double? LeerNud(TextBox? t, (double min, double max) lim, double escala, bool abs)
    {
        if (t == null) return null;
        string s = (t.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, Inv, out double v)
            || double.IsNaN(v) || double.IsInfinity(v))
        {
            Invalido(t, true);
            return null;
        }
        if (abs) v = Math.Abs(v);
        if (v < lim.min) v = lim.min;
        if (v > lim.max) v = lim.max;
        // Math.round de JavaScript (mitades hacia +infinito), NO el Math.Round
        // de .NET (banqueros): con 0,25 el JS da 0,3 y .NET daría 0,2.
        v = CfgCtx.RedondeoJs(v * escala) / escala;
        Invalido(t, false);
        SetTexto(t, Num(v));
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
        bool antes = _cargando;
        _cargando = true;
        try { t.Text = s; }
        finally { _cargando = antes; }
    }

    /// <summary>Número como lo imprimiría JavaScript, SIEMPRE invariante
    /// (1 → "1", 1.5 → "1.5"). El punto es el separador del wire.</summary>
    private static string Num(double v) => v.ToString("0.####", Inv);

    private int ValorFusion()
    {
        if (_slFusion == null) return 30;
        int v = (int)Math.Round(_slFusion.Value, MidpointRounding.AwayFromZero);
        return v < 5 ? 5 : v > 60 ? 60 : v;
    }

    // =======================================================================
    //  Pintura (el hdPintar() del HTML, réplica exacta)
    // =======================================================================

    /// <summary>`enter()`: los valores del snapshot a los campos. Va todo por
    /// SetTexto para que no ensucie la pestaña recién abierta.</summary>
    private void PintarValores()
    {
        var z = C.Snap?.Rumbo;

        // Divergencia consciente con el HTML: allá el offset se MUESTRA a 1
        // decimal pero se VALIDA a 2. Acá los dos a 2, así lo que se ve es lo
        // que se guarda.
        if (_txtOffset != null)
        {
            SetTexto(_txtOffset, z == null ? "" : Num(CfgCtx.RedondeoJs(z.DualHeadingOffset * 100.0) / 100.0));
            Invalido(_txtOffset, false);
        }
        if (_txtReversa != null)
        {
            SetTexto(_txtReversa, z == null ? "" : Num(CfgCtx.RedondeoJs(z.DualReverseDistance * 100.0) / 100.0));
            Invalido(_txtReversa, false);
        }
        if (_txtVel != null)
        {
            // El wire es km/h SIEMPRE; la pantalla muestra km/h o mph.
            double disp = z == null ? 0 : z.AutoSwitchSpeed * (C.IsMetric ? 1.0 : Mph);
            SetTexto(_txtVel, z == null ? "" : Num(CfgCtx.RedondeoJs(disp * 10.0) / 10.0));
            Invalido(_txtVel, false);
        }
        if (_txtSalto != null)
        {
            SetTexto(_txtSalto, z == null ? "" : z.JumpFixDistance.ToString("0", Inv));
            Invalido(_txtSalto, false);
        }
        if (_lblVelUnidad != null)
            _lblVelUnidad.Text = PilotX.Cockpit.Bars.Traductor.T(C.IsMetric ? "km/h" : "mph");

        if (_slFusion != null && z != null)
        {
            bool antes = _cargando;
            _cargando = true;
            try
            {
                double v = z.Fusion;
                _slFusion.Value = v < 5 ? 5 : v > 60 ? 60 : v;
            }
            finally { _cargando = antes; }
        }
        PintarFusion();
    }

    private void Pintar()
    {
        bool editable = Editable() && !_guardando;

        // Cascada de habilitación (réplica SetAutoSwitchDualFixPanelOptions):
        // con auto-switch activo quedan ACTIVAS LAS DOS cartas a la vez. Las
        // cartas atenuadas CONSERVAN sus valores y siguen viajando en el body.
        bool dualOn = _source == "Dual" || _autoSwitch;
        bool singleOn = _source == "Fix" || _autoSwitch;
        PintarCarta(_cartaDual, dualOn && editable, dualOn);
        PintarCarta(_cartaSingle, singleOn && editable, singleOn);

        foreach (var f in _tiles) PintarSel(f);
        foreach (var f in _filas) PintarSel(f);

        // Con auto-switch activo, "Fix" no se puede elegir a mano. El HTML le
        // pone la clase `deshab` pero no hay CSS para ella: acá se atenúa de
        // verdad (divergencia deliberada, ver cabecera).
        if (_tileFix != null)
        {
            bool fixVivo = editable && !_autoSwitch;
            _tileFix.Opacity = _autoSwitch ? 0.45 : (editable ? 1.0 : 0.55);
            _tileFix.IsHitTestVisible = fixVivo;
        }
        if (_tileDual != null)
        {
            _tileDual.Opacity = editable ? 1.0 : 0.55;
            _tileDual.IsHitTestVisible = editable;
        }

        // La barra de fusión solo se toca con IMU presente (runtime) o con
        // auto-switch activo, que la fuerza.
        if (_slFusion != null)
            _slFusion.IsEnabled = editable && singleOn && (_imu || _autoSwitch);

        foreach (var t in new[] { _txtOffset, _txtReversa, _txtVel, _txtSalto })
        {
            if (t == null) continue;
            t.IsEnabled = editable;
        }

        PintarPasoMinimo();
    }

    private static void PintarSel(Fila f)
    {
        bool sel = f.Sel();
        f.Borde.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
        f.Borde.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
    }

    /// <summary>El `hdCarta()` del HTML: opacidad + pointer-events. Los valores
    /// NO se resetean — solo se atenúan.</summary>
    private static void PintarCarta(Border? b, bool viva, bool on)
    {
        if (b == null) return;
        b.Opacity = on ? 1.0 : 0.45;
        b.IsHitTestVisible = viva;
    }

    /// <summary>Tabla FIJA de rótulos del original (réplica UpdateStepDistanceUI).
    /// Son 4 combinaciones exactas y no se calculan con un formateador genérico:
    /// el "5 cm"/"10 cm" no es el valor del setting (0,5 m / 1,0 m), es el
    /// rótulo que el operario conoce.</summary>
    private void PintarPasoMinimo()
    {
        bool met = C.IsMetric;
        if (_lblPasoMin != null)
            _lblPasoMin.Text = PilotX.Cockpit.Bars.Traductor.T("Paso mínimo") + ": "
                + (_minStep ? (met ? "10 cm" : "3.93 in") : (met ? "5 cm" : "1.96 in"));
        if (_lblDistRumbo != null)
            _lblDistRumbo.Text = PilotX.Cockpit.Bars.Traductor.T("Distancia de rumbo") + ": "
                + (_minStep ? (met ? "100 cm" : "39.3 in") : (met ? "50 cm" : "19.68 in"));
    }

    /// <summary>El `hdPintarFusion()`: el valor de la barra es el % GPS y el %
    /// IMU es 100 − v. Si se invierte, el operario mueve la fusión al revés.
    /// </summary>
    private void PintarFusion()
    {
        if (_lblFusion == null) return;
        int v = ValorFusion();
        _lblFusion.Text = PilotX.Cockpit.Bars.Traductor.T("Fusión") + ": "
            + (100 - v).ToString("0", Inv) + "% IMU / " + v.ToString("0", Inv) + "% GPS   ("
            + PilotX.Cockpit.Bars.Traductor.T("por defecto") + ": 70% IMU)";
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor y el POST iría al
    /// mismo Hub que no contesta: tocar a ciegas es peor que no poder tocar.
    /// Sin la sección `rumbo` tampoco se toca: el modelo local arrancaría en los
    /// defaults de C# (Fix, todo false) y el primer POST inmediato cambiaría la
    /// fuente de rumbo del motor sin que nadie lo haya pedido.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido && C.Snap?.Rumbo != null;

    /// <summary>0 sin datos · 1 servicio caído · 2 respondió pero sin la sección
    /// `rumbo` · 3 todo bien.</summary>
    private int EstadoActual()
        => C.SinDatos ? 0 : C.ServicioCaido ? 1 : C.Snap?.Rumbo == null ? 2 : 3;

    private bool AlgunCampoConFoco()
        => (_txtOffset?.IsFocused ?? false)
        || (_txtReversa?.IsFocused ?? false)
        || (_txtVel?.IsFocused ?? false)
        || (_txtSalto?.IsFocused ?? false);

    // =======================================================================
    //  Íconos
    // =======================================================================

    /// <summary>Los PNG se decodifican UNA vez y se comparten entre filas y
    /// entre rebuilds. Solo se toca desde el hilo de UI.</summary>
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
