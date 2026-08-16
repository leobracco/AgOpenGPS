// ============================================================================
// SeccionesTab.cs — pestaña "Implemento › Secciones" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=tsections` (la sección data-tab="tsections"
// + el bloque tabs.tsections de config.js, que es el más largo de la página).
//
// QUÉ QUEDÓ NATIVO: las cinco cartas enteras — modo de secciones (individuales
// ⇄ simétricas por zonas), la grilla de anchos por sección, la grilla de fines
// de zona, los TRENES de siembra (lista, pincel y tira de surcos) y el control
// de corte (fuera del lote / velocidad mínima / cobertura mínima), con su
// guardado en el orden del original.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (Switches, Máquina, Rumbo, Rolido, U-Turn, Tram). config-implemento.html
// sigue MOSTRANDO los trenes, pero manda esta pantalla.
//
// ---------------------------------------------------------------------------
// LAS DOS CONFIGURACIONES DE IMPLEMENTO — ESTA PESTAÑA TOCA LAS DOS
// ---------------------------------------------------------------------------
//   · /api/aog/config/secciones → geometría del GUIADO y del corte (cantidad,
//     anchos, zonas, cutoff, cobertura). Persiste en GuidanceEngineData\tool.json
//     vía ToolGeometryStore (Settings.Save() es no-op sin perfil de vehículo).
//   · PUT /api/implemento → implemento CENTRAL (surcos, trenes, secciones
//     nombradas). Es OTRO archivo: implementos\<slug>.json.
// No están sincronizadas y acá se tocan las dos A PROPÓSITO, en este orden:
// primero la geometría (que manda) y recién si sale bien el implemento, con la
// cantidad de surcos ya firme. Invertirlo o unificarlo rompe la semántica de
// reintento (ver "Guardado" más abajo).
//
// ⚠️ EL PUT DEL IMPLEMENTO PISA GEOMETRÍA DEL GUIADO — verificado en banco el
// 2026-08-16 con el motor real, y NO lo introdujo este porteo: la página HTML
// hace exactamente el mismo PUT. ImplementoService.SyncToolIfChanged →
// MapToToolConfig → EngineVehicleToolService.SaveTool baja la copia del
// implemento al motor, y MapToToolConfig NO manda tres cosas:
//   1. `isSectionsNotZones`: el ToolConfigDto lo trae en `true` por default ⇒
//      guardar trenes en MODO ZONAS deja el modo en "individuales" y la
//      cantidad en min(surcos, 16). Medido: zonas 24×0,4375 + PUT ⇒
//      is_sections_not_zones true, num_sections 16. Desde la UI no se puede
//      evitar sin tocar contrato, así que se AVISA al operario (ver
//      GuardarTrenesAsync). Arreglarlo de verdad es backend.
//   2. `sectionWidths`: SaveTool rellena con Width/N ⇒ guardar trenes en modo
//      individuales UNIFORMIZA los anchos desiguales. Medido: 0,61 / 7×0,43 /
//      0,61 + PUT ⇒ nueve secciones de 0,47. El ancho TOTAL y la cantidad se
//      respetan; el reparto se pierde. Tampoco se arregla desde la UI, así que
//      TAMBIÉN se avisa (mueve por dónde corta la máquina).
//   3. `section_off_when_out`: el implemento tiene su propia copia y la baja al
//      motor, deshaciendo lo que se acababa de guardar acá. ESTO SÍ se mitiga:
//      el PUT sincroniza el flag con lo que el operario eligió en esta pantalla
//      (es el único lugar de la UI donde se edita). Mejora consciente sobre el
//      HTML, que se come el pisotón.
// Mismo cuelgue que PivoteTab y TimingTab documentan para el pivote y los
// look-ahead. Nota: sin trenes sucios NO hay PUT, así que 1 y 2 solo aparecen
// cuando se tocan los trenes. Y como 1 y 2 pisan lo que se ve en pantalla, tras
// un PUT exitoso se RELEE el snapshot: si no, la pantalla seguiría mostrando
// "zonas, 24 secciones" con el motor ya cortando en "individuales, 16".
//
// ⚠️ EL BUG "PUSE 14 Y VOLVIÓ A 3": el PUT del implemento tiene que
// re-sincronizar la lista `secciones` Y `numero_surcos` a la cantidad recién
// guardada. El write-back central→Tool deriva NumSections de esa lista: si
// viaja la vieja, pisa la geometría que se acaba de guardar. El backend se
// defiende regenerando, pero la defensa PIERDE los nombres — por eso la
// sincronización se hace acá, preservando nombre y lookaheads de cada sección.
//
// Trampas de unidades conservadas TAL CUAL (cambiar cualquiera desalinea la
// cabina con el celular y con el motor):
//   · cap de ancho total 5000 cm | 1900 in (NO son equivalentes exactos);
//   · clamp de reset 99 cm | 19 in cuando cantidad × ancho pasa el cap;
//   · ancho por defecto [10, 1000] cm | [3, 393] in (el 3 imperial es un quirk
//     del original: Min/3.0);
//   · cutoff [0, 30] km/h | [0, 18.6] MPH, pero el setting viaja SIEMPRE en
//     km/h (÷ 0.621371 si la pantalla está en imperial);
//   · `_widths` vive en unidades de DISPLAY (cm|in) y `_widthMulti` en METROS.
//     La asimetría es del original y se respeta para que los redondeos den
//     idénticos a los del HTML.
//
// Quirks de comportamiento replicados:
//   · cambiar la CANTIDAD pisa los 16 anchos con el ancho por defecto (y si no
//     entra en el cap, los resetea a 99|19 avisando);
//   · el ancho por defecto se aplica al CONFIRMAR (perder foco), no por tecla:
//     tipear "120" pasa por "1" y resetearía todo a 1 cm;
//   · el reparto de zonas es división entera con la última absorbiendo el resto,
//     y PISA los fines editados a mano al cambiar cantidad o cantidad de zonas;
//   · la última zona va deshabilitada: el backend la fuerza a la última sección;
//   · elegir el pincel de trenes NO ensucia (no cambia datos); pintar un surco sí;
//   · "Quitar" un tren devuelve sus surcos al tren 1 TAMBIÉN EN LA MEMORIA, si
//     no reaparecen al retipear la cantidad;
//   · máximo 4 trenes; el tren 1 es el delantero y va SIEMPRE a 0 m (no tiene
//     campo de distancia ni botón Quitar: se garantiza por construcción).
//
// Divergencias conscientes con el HTML:
//   · el JS escucha `input`/`change` del DOM; acá `input` ⇒ TextChanged e
//     `input`-de-confirmación ⇒ LostFocus. Toda escritura por código va con el
//     flag _cargando para que no cuente como gesto del operario (si no, pintar
//     los valores al entrar marcaría la pestaña como sucia sola).
//   · los `marcarSucio()` sueltos del JS (quirk del DOM re-renderizado que se
//     comía los clicks en la tira de trenes) no existen: acá el dirty se setea
//     en el mismo handler.
//   · el JS marca `sec.dirty = true` al tocar CUALQUIER cosa de trenes, así que
//     pintar la tira le hace re-postear la geometría entera. Acá los dos dirty
//     están separados: tocar solo trenes hace solo el PUT. No es cosmética —
//     re-postear la geometría vuelve a mandar PGN al módulo de máquina y a
//     redondear los anchos por el viaje display→metros. Es lo que pide la spec
//     y lo que necesita el reintento del §"Guardado".
//
// ---------------------------------------------------------------------------
// PERSISTENCIA — VERIFICADA CONTRA EL DISCO CON REINICIO DEL MOTOR
// (banco, 2026-08-16; método: POST → mirar el archivo → matar el motor →
//  arranque limpio → GET. El round-trip NO prueba nada: BuildSecciones lee
//  Settings.Default EN MEMORIA y devuelve lo que se acaba de mandar):
//   · num_sections, section_widths (como section_positions), default_section_width,
//     num_sections_multi, section_width_multi, zones + zone_ranges,
//     is_sections_not_zones, is_section_off_when_out, slow_speed_cutoff y
//     min_coverage → PERSISTEN, en GuidanceEngineData\tool.json.
//   · trenes / surcos / secciones del implemento → PERSISTEN, en
//     implementos\<slug>.json (archivo aparte, del implemento activo).
//     Medido: 2 trenes (Delantero 0 m / Trasero 4,5 m) con los surcos 6..9 en
//     el trasero sobrevivieron al reinicio tal cual, con marca/modelo/nodos
//     intactos (el DTO se round-trippea entero).
//     PERO la asignación surco→tren se re-deriva del Tool en el arranque: si
//     DESPUÉS de guardar trenes se cambia la cantidad de secciones por otro
//     lado, al reiniciar los surcos vuelven todos al tren 1. Por eso el orden
//     de guardado es geometría PRIMERO y trenes DESPUÉS, sobre la cantidad ya
//     firme — invertirlo pierde lo que el operario pintó.
//   · <Documentos>\AgOpenGPS\Vehicles\*.XML NO se escribe: Settings.Save() es
//     no-op sin perfil de vehículo elegido (trampa conocida del repo). Quien
//     persiste de verdad es ToolGeometryStore.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class SeccionesTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fondo de un NUD (el `aliceblue` del CSS) y su versión inválida.</summary>
    private static readonly IBrush BgNud = new SolidColorBrush(Color.Parse("#F0F6FB"));
    private static readonly IBrush BgNudMal = new SolidColorBrush(Color.Parse("#FBECEC"));

    /// <summary>Colores de tren por ÍNDICE en la lista (módulo 4). Son los
    /// mismos del HTML a propósito: "el tren naranja" tiene que ser el mismo en
    /// la cabina y en el celular.</summary>
    private static readonly string[] TrnColores = { "#4ABA3E", "#E0A33E", "#5B8DD9", "#B06AC4" };

    private const int MaxTrenes = 4;

    // ---- modelo local (el `sec` de config.js) ------------------------------
    private string _modo = "ind";              // "ind" | "zonas"
    private int _num = 1;
    private readonly double[] _widths = new double[16];   // display (cm|in)
    private double _defWidth;                             // display (cm|in)
    private int _numMulti = 1;
    private double _widthMulti;                           // METROS (asimetría del original)
    private int _zonas = 2;
    private readonly int[] _ranges = new int[8];
    private bool _boundary;
    private bool _dirtySec;

    // ---- modelo local de trenes (el `trn` de config.js) --------------------
    private ImplementoDto? _impl;
    private List<SurcoDto> _memoria = new List<SurcoDto>();
    private int _pincel = 1;
    private bool _dirtyTrn;

    /// <summary>Arranca en true a propósito: el shell hace Rebuild() ANTES del
    /// AlEntrarAsync que dispara el GET, y sin esto la carta de trenes mostraría
    /// "no se pudo cargar el implemento" durante un parpadeo, cuando todavía ni
    /// se intentó.</summary>
    private bool _implCargando = true;

    // ---- flags de UI -------------------------------------------------------
    private bool _guardando;

    /// <summary>Estamos escribiendo por código: ese TextChanged /
    /// SelectionChanged NO es un gesto del operario.</summary>
    private bool _cargando;

    private int _estadoPintado = -1;

    // ---- controles ---------------------------------------------------------
    private Image? _imgModo;
    private TextBlock? _capModo;
    private Border? _cartaInd, _cartaZonas;
    private ComboBox? _cboNum, _cboZonas;
    private TextBox? _txtDefWidth, _txtNumMulti, _txtWidthMulti, _txtCutoff, _txtCoverage;
    private WrapPanel? _grillaAnchos, _grillaZonas, _tiraTrenes;
    private TextBlock? _lblTotalInd, _lblTotalZonas, _lblUnidadDef, _lblUnidadMulti, _lblUnidadCutoff;
    private StackPanel? _listaTrenes, _panelSurcos;
    private WrapPanel? _pincelHost;
    private TextBlock? _msgTrenes;
    private Image? _imgBoundary;
    private Border? _cardBoundary;

    public SeccionesTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    public override bool HayCambios => _dirtySec || _dirtyTrn;

    // =======================================================================
    //  Límites (réplica exacta de config.js)
    // =======================================================================

    /// <summary>Cap del ancho total: 5000 cm | 1900 in. No son equivalentes
    /// (1900 in = 4826 cm) — es así en el original y en el motor.</summary>
    private double CapDisp() => C.IsMetric ? 5000.0 : 1900.0;

    private (double min, double max) LimDefWidth() => C.IsMetric ? (10.0, 1000.0) : (3.0, 393.0);

    private (double min, double max) LimCutoff() => C.IsMetric ? (0.0, 30.0) : (0.0, 18.6);

    /// <summary>Ancho al que se resetean todas las secciones cuando cantidad ×
    /// ancho no entra en el cap.</summary>
    private double AnchoReset() => C.IsMetric ? 99.0 : 19.0;

    private int MaxSecciones() => Math.Max(1, C.Snap?.Secciones?.MaxSections ?? 64);

    private double TotalInd()
    {
        double t = 0;
        for (int i = 0; i < _num && i < _widths.Length; i++) t += _widths[i];
        return t;
    }

    /// <summary>Cantidad EFECTIVA de surcos: la del modo activo, en vivo (antes
    /// de guardar). La tira de trenes la sigue.</summary>
    private int CantidadEfectiva() => _modo == "ind" ? _num : _numMulti;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    public override async Task AlEntrarAsync()
    {
        // Réplica del Enter nativo: con lote abierto APAGA los masters de
        // sección. Efecto de lado buscado — editar la geometría con el corte
        // activo deja el aplicador en un estado raro. Fire-and-forget.
        if (C.Client != null)
        {
            try { _ = C.Client.PrepararSeccionesAsync(); } catch { }
        }

        CargarDesdeSnapshot();
        _dirtySec = false;
        Rebuild();

        // Los trenes viven en OTRO backend: se cargan sin bloquear el enter,
        // igual que el trnCargar() del HTML.
        await CargarImplementoAsync().ConfigureAwait(true);
    }

    /// <summary>`leave()`: mismo orden que el original. false CANCELA la
    /// navegación.</summary>
    public override async Task<bool> AlSalirAsync()
    {
        if (_guardando) return false;

        // Los trenes pueden estar sucios aunque la geometría no (se pintó la
        // tira sin tocar cantidades): se guardan igual y su resultado manda.
        if (!_dirtySec) return await GuardarTrenesAsync().ConfigureAwait(true);

        if (C.Client == null) { C.Estado?.Invoke("Sin conexión con PilotX", "err"); return false; }

        double? cut = LeerNudDec(_txtCutoff, LimCutoff());
        int? cov = LeerNud(_txtCoverage, (0, 100));
        if (cut == null || cov == null)
        {
            C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
            return false;
        }

        // El cutoff SIEMPRE viaja en km/h, aunque la pantalla muestre MPH.
        double cutKmh = C.IsMetric ? cut.Value : cut.Value / 0.621371;

        object body;
        if (_modo == "ind")
        {
            if (TotalInd() > CapDisp())
            {
                C.Estado?.Invoke(
                    "Ancho total excedido (máx " + Ent(CapDisp()) + " " + C.Unidad() + ")", "err");
                return false;
            }
            int? dw = LeerNud(_txtDefWidth, LimDefWidth());
            if (dw == null)
            {
                C.Estado?.Invoke("Revisá los valores marcados en rojo", "err");
                return false;
            }
            var anchosM = new double[16];
            for (int i = 0; i < 16; i++) anchosM[i] = C.Disp2M(_widths[i]);
            body = new
            {
                is_sections_not_zones = true,
                is_section_off_when_out = _boundary,
                slow_speed_cutoff = cutKmh,
                min_coverage = cov.Value,
                num_sections = _num,
                default_section_width = C.Disp2M(dw.Value),
                section_widths = anchosM,
            };
        }
        else
        {
            if (_zonas > _numMulti)
            {
                C.Estado?.Invoke("No puede haber más zonas que secciones", "err");
                return false;
            }
            body = new
            {
                is_sections_not_zones = false,
                is_section_off_when_out = _boundary,
                slow_speed_cutoff = cutKmh,
                min_coverage = cov.Value,
                num_sections_multi = _numMulti,
                section_width_multi = _widthMulti,
                zones = _zonas,
                zone_ranges = (int[])_ranges.Clone(),
            };
        }

        _guardando = true;
        PintarHabilitado();
        try
        {
            C.Estado?.Invoke("Guardando…", "");
            var r = await C.Client.GuardarAsync("secciones", body).ConfigureAwait(true);
            if (r == null) { C.Estado?.Invoke("Sin conexión con PilotX", "err"); return false; }
            if (!r.Ok)
            {
                C.Estado?.Invoke("Error: " + ErrorEnCriollo(r.Error), "err");
                return false;
            }

            // La geometría ya quedó firme: limpiar el dirty ANTES de los trenes
            // es lo que permite que un reintento poste SOLO el implemento.
            _dirtySec = false;
            C.Estado?.Invoke("Guardado ✔", "ok");

            // Relectura obligatoria: el POST recalcula Tool.width, re-centra las
            // posiciones y clampea lo suyo. Lo que se muestra tiene que ser lo
            // que el motor tiene de verdad, no lo que se tipeó.
            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }

            return await GuardarTrenesAsync().ConfigureAwait(true);
        }
        finally
        {
            _guardando = false;
            PintarHabilitado();
        }
    }

    /// <summary>Errores crudos del motor, en criollo. Los que no están mapeados
    /// se muestran tal cual (mejor un código raro que tragarse el motivo).</summary>
    private static string ErrorEnCriollo(string? e) => (e ?? "") switch
    {
        "ancho-total-excedido"   => "Ancho total excedido",
        "ancho-total-cero"       => "El ancho total no puede ser cero",
        "ancho-seccion-cero"     => "El ancho de sección no puede ser cero",
        "mas-zonas-que-secciones" => "No puede haber más zonas que secciones",
        "zonas-desordenadas"     => "Los fines de zona tienen que ir en aumento",
        "service-unavailable"    => "Servicio de configuración no disponible",
        "" => "desconocido",
        _ => e!,
    };

    public override void Live()
    {
        if (_estadoPintado == EstadoActual()) return;
        if (_dirtySec || _dirtyTrn || _guardando) return;
        if (AlgunCampoConFoco()) return;
        CargarDesdeSnapshot();
        Rebuild();   // Rebuild ya repinta los trenes con lo que haya en memoria
    }

    /// <summary>Pasa el snapshot al modelo local (el bloque de carga del
    /// `enter()`). Los anchos van a DISPLAY y widthMulti se queda en metros.</summary>
    private void CargarDesdeSnapshot()
    {
        var z = C.Snap?.Secciones;
        if (z == null) return;

        _modo = z.IsSectionsNotZones ? "ind" : "zonas";
        _num = Math.Max(1, Math.Min(z.NumSections, 16));
        for (int i = 0; i < 16; i++)
        {
            double m = (z.SectionWidths != null && i < z.SectionWidths.Length) ? z.SectionWidths[i] : 0.0;
            _widths[i] = C.M2Disp(m);
        }
        _defWidth = C.M2Disp(z.DefaultSectionWidth ?? 0.0);
        _numMulti = Math.Max(1, z.NumSectionsMulti);
        _widthMulti = z.SectionWidthMulti ?? 0.0;
        _zonas = Math.Max(2, Math.Min(z.Zones, 8));
        for (int k = 0; k < 8; k++)
            _ranges[k] = (z.ZoneRanges != null && k < z.ZoneRanges.Length) ? z.ZoneRanges[k] : 0;
        _boundary = z.IsSectionOffWhenOut;
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _estadoPintado = EstadoActual();

        // MaxWidth OBLIGATORIO: el TabHost cuelga de un ScrollViewer con scroll
        // horizontal; sin tope el StackPanel mide "infinito" y nada envuelve.
        // 686 y no 860, como las hermanas (Rolido, Rumbo, Tram, Uturn): el área
        // útil de la tarjeta es ~698 px (940 − 28 de padding − 196 del menú −
        // márgenes), así que con 860 la grilla de 16 anchos envolvía a un ancho
        // que no existe y la pestaña pedía arrastrar de costado en 1024.
        var raiz = new StackPanel { Spacing = 12, MaxWidth = 686 };

        if (C.SinDatos)
        {
            raiz.Children.Add(new TextBlock
            {
                Text = T("PilotX no responde — todavía no llegaron los datos."),
                Foreground = CfgUi.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (C.ServicioCaido)
        {
            raiz.Children.Add(CfgUi.ChipError("Servicio de configuración no disponible", "AGP-NET-201"));
        }

        raiz.Children.Add(CartaModo());
        _cartaInd = CartaIndividuales();
        _cartaZonas = CartaZonas();
        raiz.Children.Add(_cartaInd);
        raiz.Children.Add(_cartaZonas);
        raiz.Children.Add(CartaTrenes());
        raiz.Children.Add(CartaControl());

        Children.Add(raiz);

        PintarModo();
        PintarInd();
        PintarZonas();
        PintarBoundary();
        PintarControl();
        PintarTrenes();
        PintarHabilitado();
    }

    // ---- Carta 1: modo -----------------------------------------------------

    private Border CartaModo()
    {
        var carta = new StackPanel { Spacing = 10 };
        carta.Children.Add(CfgUi.Titulo("Modo de secciones"));

        _imgModo = new Image
        {
            Width = 64, Height = 64, MaxWidth = 64, MaxHeight = 64,
            Stretch = Stretch.Uniform,
        };
        _capModo = new TextBlock
        {
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };

        var pila = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        pila.Children.Add(new Border
        {
            Background = CfgUi.BgFila, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center,
            Child = _imgModo,
        });
        pila.Children.Add(_capModo);

        var boton = new Border
        {
            Width = 200, MinHeight = 118,
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Verde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };
        boton.Tapped += (_, __) => AlternarModo();

        var fila = CfgUi.Grilla();
        var celdaBoton = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
        celdaBoton.Children.Add(boton);
        fila.Children.Add(celdaBoton);

        var nota = new StackPanel { Spacing = 4, MaxWidth = 420, VerticalAlignment = VerticalAlignment.Center };
        nota.Children.Add(CfgUi.Nota("Individuales: hasta 16 secciones, cada una con su ancho."));
        nota.Children.Add(CfgUi.Nota("Simétricas (zonas): hasta 64 secciones del mismo ancho, agrupadas en 2–8 zonas."));
        fila.Children.Add(nota);

        carta.Children.Add(fila);
        return CfgUi.Carta(carta);
    }

    /// <summary>Tap en el toggle de modo: cambia el modelo local, repinta TODO y
    /// también los trenes (el modo cambia la cantidad efectiva de surcos). NO
    /// guarda: eso lo hace el botón Guardar o el salir de la pestaña.</summary>
    private void AlternarModo()
    {
        if (_guardando || !Editable()) return;
        _modo = _modo == "ind" ? "zonas" : "ind";
        Ensuciar();
        PintarModo();
        if (_modo == "ind") PintarInd(); else PintarZonas();
        PintarTrenes();
    }

    // ---- Carta 2: secciones individuales -----------------------------------

    private Border CartaIndividuales()
    {
        var carta = new StackPanel { Spacing = 10 };
        carta.Children.Add(CfgUi.Titulo("Secciones individuales"));

        _cboNum = CfgUi.Combo();
        _cboNum.MinWidth = 100;
        for (int i = 1; i <= 16; i++) _cboNum.Items.Add(i.ToString(Inv));
        _cboNum.SelectionChanged += (_, __) => { if (!_cargando) CambiarCantidad(); };

        _txtDefWidth = Nud("Ancho por defecto", 120);
        _txtDefWidth.TextChanged += (_, __) =>
        {
            if (_cargando) return;
            // Solo actualiza el modelo: NO pisa las celdas. Tipear "120" pasaría
            // por "1" y dejaría todas las secciones en 1 cm.
            double v = ParseDouble(_txtDefWidth);
            if (!double.IsNaN(v)) _defWidth = Math.Abs(v);
            Ensuciar();
        };
        // El `change` del DOM: al CONFIRMAR (perder el foco) sí pisa los anchos.
        _txtDefWidth.LostFocus += (_, __) => { if (!_cargando) ConfirmarAnchoDefecto(); };

        _lblUnidadDef = Unidad();

        var fila = CfgUi.Grilla();
        fila.Children.Add(CampoFila("Cantidad", _cboNum));
        fila.Children.Add(CampoFila("Ancho por defecto", _txtDefWidth, _lblUnidadDef));
        carta.Children.Add(fila);

        carta.Children.Add(CfgUi.Nota("Cambiar la cantidad pisa todos los anchos con el ancho por defecto."));

        _grillaAnchos = CfgUi.Grilla();
        carta.Children.Add(_grillaAnchos);

        _lblTotalInd = new TextBlock
        {
            Foreground = CfgUi.Texto, FontSize = 14, FontWeight = FontWeight.Bold,
            FontFamily = CfgUi.Mono, TextWrapping = TextWrapping.Wrap,
        };
        carta.Children.Add(EtiquetaConValor("Ancho total:", _lblTotalInd));

        return CfgUi.Carta(carta);
    }

    /// <summary>Cambio de cantidad: PISA los 16 anchos con el ancho por defecto
    /// validado. Si cantidad × ancho no entra en el cap, resetea a 99|19 y
    /// avisa (clamp del original).</summary>
    private void CambiarCantidad()
    {
        if (_guardando || !Editable() || _cboNum == null) return;
        int idx = _cboNum.SelectedIndex;
        if (idx < 0) return;
        _num = idx + 1;

        int? dw = LeerNud(_txtDefWidth, LimDefWidth());
        double wide = dw ?? _defWidth;
        if (_num * wide > CapDisp())
        {
            wide = AnchoReset();
            C.Estado?.Invoke(
                "Demasiado ancho — anchos reseteados a " + Ent(wide) + " " + C.Unidad(), "err");
        }
        _defWidth = wide;
        for (int i = 0; i < 16; i++) _widths[i] = wide;

        Ensuciar();
        PintarInd();
        PintarTrenes();   // la tira de trenes acompaña la cantidad
    }

    /// <summary>`change` del ancho por defecto: valida, aplica el mismo clamp
    /// del cap y pisa el ancho de las 16 secciones.</summary>
    private void ConfirmarAnchoDefecto()
    {
        if (_guardando || !Editable()) return;
        int? dw = LeerNud(_txtDefWidth, LimDefWidth());
        if (dw == null) return;
        double wide = dw.Value;
        if (_num * wide > CapDisp())
        {
            wide = AnchoReset();
            C.Estado?.Invoke(
                "Demasiado ancho — anchos reseteados a " + Ent(wide) + " " + C.Unidad(), "err");
            if (_txtDefWidth != null) SetTexto(_txtDefWidth, Ent(wide));
        }
        _defWidth = wide;
        for (int i = 0; i < 16; i++) _widths[i] = wide;
        Ensuciar();
        PintarInd();
    }

    // ---- Carta 3: secciones simétricas (zonas) -----------------------------

    private Border CartaZonas()
    {
        var carta = new StackPanel { Spacing = 10 };
        carta.Children.Add(CfgUi.Titulo("Secciones simétricas"));

        _txtNumMulti = Nud("Secciones", 110);
        _txtNumMulti.LostFocus += (_, __) => { if (!_cargando) ConfirmarNumMulti(); };

        _cboZonas = CfgUi.Combo();
        _cboZonas.MinWidth = 90;
        for (int z = 2; z <= 8; z++) _cboZonas.Items.Add(z.ToString(Inv));
        _cboZonas.SelectionChanged += (_, __) => { if (!_cargando) CambiarZonas(); };

        _txtWidthMulti = Nud("Ancho de sección", 120);
        _txtWidthMulti.TextChanged += (_, __) =>
        {
            if (_cargando) return;
            double v = ParseDouble(_txtWidthMulti);
            if (!double.IsNaN(v)) _widthMulti = C.Disp2M(Math.Abs(v));
            Ensuciar();
            PintarTotalZonas();
        };

        _lblUnidadMulti = Unidad();

        var fila = CfgUi.Grilla();
        fila.Children.Add(CampoFila("Secciones", _txtNumMulti));
        fila.Children.Add(CampoFila("Zonas", _cboZonas));
        fila.Children.Add(CampoFila("Ancho de sección", _txtWidthMulti, _lblUnidadMulti));
        carta.Children.Add(fila);

        _grillaZonas = CfgUi.Grilla();
        carta.Children.Add(_grillaZonas);

        _lblTotalZonas = new TextBlock
        {
            Foreground = CfgUi.Texto, FontSize = 14, FontWeight = FontWeight.Bold,
            FontFamily = CfgUi.Mono, TextWrapping = TextWrapping.Wrap,
        };
        carta.Children.Add(EtiquetaConValor("Ancho total:", _lblTotalZonas));

        return CfgUi.Carta(carta);
    }

    /// <summary>`change` de la cantidad de secciones del modo zonas. Si queda
    /// por debajo de la cantidad de zonas se RESTAURA el valor previo (no se
    /// clampea a la brava: perder zonas configuradas sin avisar es peor).</summary>
    private void ConfirmarNumMulti()
    {
        if (_guardando || !Editable() || _txtNumMulti == null) return;
        double d = ParseDouble(_txtNumMulti);
        if (double.IsNaN(d)) { Invalido(_txtNumMulti, true); return; }
        int v = (int)Math.Truncate(d);
        v = Math.Max(1, Math.Min(v, MaxSecciones()));
        if (v < _zonas)
        {
            C.Estado?.Invoke("No puede haber más zonas que secciones", "err");
            SetTexto(_txtNumMulti, _numMulti.ToString(Inv));
            return;
        }
        Invalido(_txtNumMulti, false);
        _numMulti = v;
        Ensuciar();
        ZonasDefault();
        PintarZonas();
        PintarTrenes();   // la tira sigue a la cantidad también en modo zonas
    }

    private void CambiarZonas()
    {
        if (_guardando || !Editable() || _cboZonas == null) return;
        int idx = _cboZonas.SelectedIndex;
        if (idx < 0) return;
        int z = idx + 2;
        if (z > _numMulti)
        {
            C.Estado?.Invoke("No puede haber más zonas que secciones", "err");
            SetCombo(_cboZonas, _zonas - 2);
            return;
        }
        _zonas = z;
        Ensuciar();
        ZonasDefault();
        PintarZonas();
    }

    /// <summary>Reparto default del original: división entera y la última zona
    /// absorbe el resto. PISA los fines editados a mano — es lo que hace el
    /// HTML y lo que espera el operario al cambiar cantidad o zonas.</summary>
    private void ZonasDefault()
    {
        int defa = _numMulti / _zonas;
        for (int k = 0; k < 8; k++) _ranges[k] = 0;
        for (int k = 1; k < _zonas; k++) _ranges[k - 1] = k * defa;
        _ranges[_zonas - 1] = _numMulti;
    }

    // ---- Carta 4: trenes ---------------------------------------------------

    private Border CartaTrenes()
    {
        var carta = new StackPanel { Spacing = 10 };
        carta.Children.Add(CfgUi.Titulo("Trenes de siembra"));
        carta.Children.Add(CfgUi.Nota(
            "Para máquinas con tren delantero y trasero: cada surco pertenece a un tren, "
            + "y el trasero corta N metros después, sobre la misma pasada. "
            + "Con un solo tren esta parte no afecta nada."));

        _listaTrenes = new StackPanel { Spacing = 8 };
        carta.Children.Add(_listaTrenes);

        carta.Children.Add(CfgUi.Boton("+ Agregar tren", AgregarTren));

        _panelSurcos = new StackPanel { Spacing = 8 };
        _panelSurcos.Children.Add(CfgUi.Nota("Qué surcos van en cada tren (elegí un tren y tocá los surcos):"));
        _pincelHost = CfgUi.Grilla();
        _panelSurcos.Children.Add(_pincelHost);
        _tiraTrenes = CfgUi.Grilla();
        _panelSurcos.Children.Add(_tiraTrenes);
        carta.Children.Add(_panelSurcos);

        _msgTrenes = CfgUi.Nota("");
        carta.Children.Add(_msgTrenes);

        return CfgUi.Carta(carta);
    }

    // ---- Carta 5: control de secciones -------------------------------------

    private Border CartaControl()
    {
        var carta = new StackPanel { Spacing = 10 };
        carta.Children.Add(CfgUi.Titulo("Control de secciones"));

        _imgBoundary = new Image
        {
            Width = 64, Height = 64, MaxWidth = 64, MaxHeight = 64, Stretch = Stretch.Uniform,
        };
        var pilaB = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        pilaB.Children.Add(new Border
        {
            Background = CfgUi.BgFila, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center,
            Child = _imgBoundary,
        });
        pilaB.Children.Add(new TextBlock
        {
            Text = T("Cortar fuera del lote"),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        _cardBoundary = new Border
        {
            Width = 190, MinHeight = 118,
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 16, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pilaB,
        };
        _cardBoundary.Tapped += (_, __) => AlternarBoundary();

        _txtCutoff = Nud("Cortar por debajo de", 120);
        _txtCutoff.TextChanged += (_, __) => { if (!_cargando) Ensuciar(); };
        _lblUnidadCutoff = Unidad();

        _txtCoverage = Nud("Cobertura mínima", 120);
        _txtCoverage.TextChanged += (_, __) => { if (!_cargando) Ensuciar(); };

        var derecha = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        var filaCut = CfgUi.Fila();
        filaCut.Children.Add(new Image
        {
            Source = Icono("SectionOffBelow.png"),
            Height = 44, MaxHeight = 44, MaxWidth = 88, Stretch = Stretch.Uniform,
        });
        filaCut.Children.Add(CampoFila("Cortar por debajo de", _txtCutoff, _lblUnidadCutoff));
        derecha.Children.Add(filaCut);

        derecha.Children.Add(CampoFila("Cobertura mínima", _txtCoverage, Unidad("%")));

        var fila = CfgUi.Grilla();
        fila.Children.Add(_cardBoundary);
        fila.Children.Add(derecha);
        carta.Children.Add(fila);

        return CfgUi.Carta(carta);
    }

    private void AlternarBoundary()
    {
        if (_guardando || !Editable()) return;
        _boundary = !_boundary;
        Ensuciar();
        PintarBoundary();
    }

    // =======================================================================
    //  Pintura
    // =======================================================================

    private void PintarModo()
    {
        bool esZonas = _modo == "zonas";
        if (_imgModo != null)
            _imgModo.Source = Icono(esZonas ? "ConT_Symmetric.png" : "ConT_Asymmetric.png");
        if (_capModo != null)
            _capModo.Text = T(esZonas ? "Secciones simétricas (zonas)" : "Secciones individuales");
        if (_cartaInd != null) _cartaInd.IsVisible = !esZonas;
        if (_cartaZonas != null) _cartaZonas.IsVisible = esZonas;
    }

    private void PintarBoundary()
    {
        if (_imgBoundary != null)
            _imgBoundary.Source = Icono(_boundary ? "SectionOffBoundary.png" : "SectionOnBoundary.png");
        if (_cardBoundary != null)
        {
            _cardBoundary.BorderBrush = _boundary ? CfgUi.Verde : CfgUi.Borde;
            _cardBoundary.Background = _boundary ? CfgUi.BgFilaSel : CfgUi.BgFila;
        }
    }

    /// <summary>Cutoff (con conversión a MPH), su unidad y la cobertura.</summary>
    private void PintarControl()
    {
        var z = C.Snap?.Secciones;
        double cut = z?.SlowSpeedCutoff ?? 0.0;
        double disp = C.IsMetric ? cut : cut * 0.621371;
        if (_txtCutoff != null) { SetTexto(_txtCutoff, Num(CfgCtx.RedondeoJs(disp * 10.0) / 10.0)); Invalido(_txtCutoff, false); }
        if (_lblUnidadCutoff != null) _lblUnidadCutoff.Text = C.IsMetric ? "km/h" : "MPH";
        if (_txtCoverage != null) { SetTexto(_txtCoverage, (z?.MinCoverage ?? 0).ToString(Inv)); Invalido(_txtCoverage, false); }
        if (_lblUnidadDef != null) _lblUnidadDef.Text = C.Unidad();
        if (_lblUnidadMulti != null) _lblUnidadMulti.Text = C.Unidad();
    }

    /// <summary>Grilla de anchos por sección. Se reconstruye entera (igual que
    /// el innerHTML='' del HTML) pero SOLO en cambios de cantidad / ancho por
    /// defecto: tipear en una celda no la rearma, así no se pierde el foco ni
    /// se cierra el teclado nativo.</summary>
    private void PintarInd()
    {
        if (_cboNum != null) SetCombo(_cboNum, _num - 1);
        if (_txtDefWidth != null) SetTexto(_txtDefWidth, Ent(_defWidth));

        if (_grillaAnchos == null) return;
        _grillaAnchos.Children.Clear();
        for (int j = 0; j < _num && j < 16; j++)
        {
            int idx = j;
            var celda = new StackPanel
            {
                Spacing = 4, Width = 120, Margin = new Thickness(0, 0, 8, 8),
            };
            celda.Children.Add(new TextBlock
            {
                Text = (idx + 1).ToString(Inv),
                Foreground = CfgUi.TextoDim, FontSize = 11, FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var inp = Nud("Ancho sección " + (idx + 1), 108);
            SetTexto(inp, Ent(_widths[idx]));
            inp.TextChanged += (_, __) =>
            {
                if (_cargando) return;
                double v = ParseDouble(inp);
                if (!double.IsNaN(v)) _widths[idx] = Math.Abs(v);
                Ensuciar();
                PintarTotalInd();   // total en vivo, sin rearmar la grilla
            };
            celda.Children.Add(inp);
            _grillaAnchos.Children.Add(celda);
        }
        PintarTotalInd();
    }

    private void PintarTotalInd()
    {
        if (_lblTotalInd == null) return;
        double t = TotalInd();
        _lblTotalInd.Text = Ent(t) + " " + C.Unidad() + "  (" + C.FmtMedium(C.Disp2M(t)) + ")";
        _lblTotalInd.Foreground = t > CapDisp() ? CfgUi.Err : CfgUi.Texto;
    }

    /// <summary>Grilla de fines de zona. Cada celda dice dónde ARRANCA la zona
    /// (cascada del fin anterior + 1) y edita dónde TERMINA. La última va
    /// deshabilitada: el backend la fuerza a la última sección.</summary>
    private void PintarZonas()
    {
        if (_txtNumMulti != null) SetTexto(_txtNumMulti, _numMulti.ToString(Inv));
        if (_cboZonas != null) SetCombo(_cboZonas, _zonas - 2);
        if (_txtWidthMulti != null)
            SetTexto(_txtWidthMulti, Num(CfgCtx.RedondeoJs(C.M2Disp(_widthMulti) * 10.0) / 10.0));

        if (_grillaZonas == null) return;
        _grillaZonas.Children.Clear();
        int inicio = 1;
        for (int k = 0; k < _zonas && k < 8; k++)
        {
            int idx = k;
            bool ultima = idx == _zonas - 1;
            var celda = new StackPanel { Spacing = 4, Width = 130, Margin = new Thickness(0, 0, 8, 8) };
            celda.Children.Add(new TextBlock
            {
                Text = T("Zona") + " " + (idx + 1) + ": " + inicio + " →",
                Foreground = CfgUi.TextoDim, FontSize = 11, FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var inp = Nud("Fin de la zona " + (idx + 1), 118);
            SetTexto(inp, _ranges[idx].ToString(Inv));
            inp.IsEnabled = !ultima;
            inp.Opacity = ultima ? 0.55 : 1.0;
            if (!ultima)
            {
                inp.LostFocus += (_, __) =>
                {
                    if (_cargando || _guardando || !Editable()) return;
                    double d = ParseDouble(inp);
                    if (!double.IsNaN(d))
                        _ranges[idx] = Math.Max(0, Math.Min((int)Math.Truncate(d), _numMulti));
                    Ensuciar();
                    PintarZonas();   // refresca los "inicio" de las zonas siguientes
                };
            }
            celda.Children.Add(inp);
            _grillaZonas.Children.Add(celda);
            inicio = _ranges[idx] + 1;
        }
        PintarTotalZonas();
    }

    private void PintarTotalZonas()
    {
        if (_lblTotalZonas == null) return;
        double tot = _numMulti * _widthMulti;   // metros
        _lblTotalZonas.Text = Ent(C.M2Disp(tot)) + " " + C.Unidad() + "  (" + C.FmtMedium(tot) + ")";
    }

    // =======================================================================
    //  Trenes de siembra (el implemento central)
    // =======================================================================

    /// <summary>`trnCargar()`: GET del implemento activo. La verdad del server
    /// PISA la memoria local. Si no carga, la carta se vacía, avisa y NO se
    /// guarda nada de trenes (jamás se inventa un implemento vacío que después
    /// pisaría al bueno con un PUT).</summary>
    private async Task CargarImplementoAsync()
    {
        _impl = null;
        _implCargando = true;
        PintarTrenes();

        if (C.Implemento != null)
        {
            ImplementoRespuesta? r = null;
            try { r = await C.Implemento.GetActivoAsync().ConfigureAwait(true); }
            catch (OperationCanceledException) { }
            catch { }

            if (r != null && r.Ok && r.Implemento != null)
            {
                _impl = r.Implemento;
                if (_impl.Trenes == null || _impl.Trenes.Count == 0)
                    _impl.Trenes = new List<TrenDto> { new TrenDto { Id = 1, Nombre = "Delantero", DistanciaM = 0 } };
                if (_impl.Surcos == null) _impl.Surcos = new List<SurcoDto>();
                _memoria = _impl.Surcos
                    .Select(s => new SurcoDto { Numero = s.Numero, TrenId = s.TrenId < 1 ? 1 : s.TrenId })
                    .ToList();
                _dirtyTrn = false;
            }
        }

        _implCargando = false;
        PintarTrenes();
    }

    /// <summary>Regenera la tira a la cantidad efectiva conservando la
    /// asignación por índice desde la MEMORIA (no desde el array vivo, que un
    /// retipeo transitorio puede haber truncado). Los surcos nuevos heredan el
    /// tren del último de la memoria.</summary>
    private List<SurcoDto> SurcosActuales()
    {
        int n = CantidadEfectiva();
        var salida = new List<SurcoDto>(Math.Max(0, n));
        int ultimo = _memoria.Count > 0 ? _memoria[_memoria.Count - 1].TrenId : 1;
        for (int i = 1; i <= n; i++)
        {
            int t = i <= _memoria.Count ? _memoria[i - 1].TrenId : ultimo;
            if (t < 1) t = 1;
            salida.Add(new SurcoDto { Numero = i, TrenId = t, SeccionPilotX = i });
        }
        return salida;
    }

    private void ActualizarMemoria(List<SurcoDto> surcos)
        => _memoria = surcos.Select(s => new SurcoDto { Numero = s.Numero, TrenId = s.TrenId }).ToList();

    private static IBrush ColorTren(int indice)
        => new SolidColorBrush(Color.Parse(TrnColores[((indice % TrnColores.Length) + TrnColores.Length) % TrnColores.Length]));

    private void PintarTrenes()
    {
        if (_listaTrenes == null || _msgTrenes == null || _pincelHost == null
            || _tiraTrenes == null || _panelSurcos == null) return;

        _listaTrenes.Children.Clear();
        _pincelHost.Children.Clear();
        _tiraTrenes.Children.Clear();

        if (_impl == null)
        {
            _panelSurcos.IsVisible = false;
            _msgTrenes.Text = _implCargando
                ? T("Cargando el implemento…")
                : T("No se pudo cargar el implemento — los trenes no se pueden editar ahora.");
            return;
        }
        _msgTrenes.Text = "";

        var trenes = _impl.Trenes;

        // --- lista de trenes ---
        for (int i = 0; i < trenes.Count; i++)
        {
            var t = trenes[i];
            int id = t.Id;
            var fila = CfgUi.Fila();

            fila.Children.Add(new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                Background = ColorTren(i), VerticalAlignment = VerticalAlignment.Center,
            });

            var txtNombre = Nud("Nombre del tren", 160, numerico: false);
            txtNombre.FontSize = 14;
            txtNombre.FontWeight = FontWeight.Normal;
            txtNombre.TextAlignment = TextAlignment.Left;
            txtNombre.HorizontalContentAlignment = HorizontalAlignment.Left;
            SetTexto(txtNombre, string.IsNullOrWhiteSpace(t.Nombre) ? "Tren " + id : t.Nombre);
            txtNombre.LostFocus += (_, __) =>
            {
                if (_cargando || _guardando) return;
                var tr = _impl?.Trenes.FirstOrDefault(x => x.Id == id);
                if (tr == null) return;
                string nuevo = (txtNombre.Text ?? "").Trim();
                if (nuevo.Length == 0) nuevo = "Tren " + id;
                if (nuevo == tr.Nombre) return;
                tr.Nombre = nuevo;
                EnsuciarTrenes();
                PintarTrenes();
            };
            fila.Children.Add(txtNombre);

            fila.Children.Add(new TextBlock
            {
                Text = T("corta"), Foreground = CfgUi.TextoMuted, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });

            if (id == 1)
            {
                // El tren 1 es el DELANTERO y va siempre en 0 m: el backend solo
                // lo warnea, así que acá se garantiza por construcción (no tiene
                // campo de distancia ni botón Quitar).
                fila.Children.Add(new TextBlock
                {
                    Text = T("al paso (0 m)"), Foreground = CfgUi.TextoMuted, FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else
            {
                var txtDist = Nud("Distancia del tren (m)", 90);
                SetTexto(txtDist, Num(CfgCtx.RedondeoJs(t.DistanciaM * 100.0) / 100.0));
                txtDist.LostFocus += (_, __) =>
                {
                    if (_cargando || _guardando) return;
                    var tr = _impl?.Trenes.FirstOrDefault(x => x.Id == id);
                    if (tr == null) return;
                    double v = ParseDouble(txtDist);
                    if (double.IsNaN(v) || v < 0) v = 0;
                    if (v > 20) v = 20;   // tope duro del dominio
                    if (Math.Abs(v - tr.DistanciaM) < 1e-9) return;
                    tr.DistanciaM = v;
                    EnsuciarTrenes();
                    PintarTrenes();
                };
                fila.Children.Add(txtDist);
                fila.Children.Add(new TextBlock
                {
                    Text = T("m después"), Foreground = CfgUi.TextoMuted, FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                fila.Children.Add(CfgUi.Boton("Quitar", () => QuitarTren(id), peligro: true));
            }

            _listaTrenes.Children.Add(fila);
        }

        // --- pincel + tira: solo con más de un tren ---
        bool varios = trenes.Count > 1;
        _panelSurcos.IsVisible = varios;
        if (!varios) return;

        for (int i = 0; i < trenes.Count; i++)
        {
            var t = trenes[i];
            int id = t.Id;
            bool activo = _pincel == id;
            var color = ColorTren(i);
            var b = CfgUi.Boton(string.IsNullOrWhiteSpace(t.Nombre) ? "Tren " + id : t.Nombre,
                                () => { _pincel = id; PintarTrenes(); });   // elegir pincel NO ensucia
            b.BorderBrush = color;
            b.BorderThickness = new Thickness(2);
            b.Background = activo ? color : CfgUi.BgFila;
            // Texto SIEMPRE oscuro, también sobre el color del tren: en blanco
            // los cuatro colores daban 2,2 a 3,7:1 (el naranja #E0A33E, 2,2) y
            // el nombre del tren se borraba con sol. Con #101612 van de 5,0 a
            // 8,3:1. Los colores no se tocan: son los mismos del celular.
            b.Foreground = CfgUi.Texto;
            b.Margin = new Thickness(0, 0, 8, 8);
            _pincelHost.Children.Add(b);
        }

        var surcos = SurcosActuales();
        foreach (var s in surcos)
        {
            int numero = s.Numero;
            int idxTren = 0;
            for (int k = 0; k < trenes.Count; k++) if (trenes[k].Id == s.TrenId) { idxTren = k; break; }

            var celda = new StackPanel { Spacing = 4, Width = 56, Margin = new Thickness(0, 0, 6, 6) };
            celda.Children.Add(new TextBlock
            {
                Text = numero.ToString(Inv),
                Foreground = CfgUi.TextoDim, FontSize = 11, FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            celda.Children.Add(new Border
            {
                Height = 44, MinHeight = 44, CornerRadius = new CornerRadius(6),
                Background = ColorTren(idxTren),
                Cursor = new Cursor(StandardCursorType.Hand),
            });
            celda.Cursor = new Cursor(StandardCursorType.Hand);
            celda.Tapped += (_, __) => PintarSurco(numero);
            _tiraTrenes.Children.Add(celda);
        }
    }

    /// <summary>Tocar un surco lo asigna al tren del pincel. Trabaja sobre la
    /// regeneración fresca (no sobre el array del server, que puede estar
    /// desfasado de la cantidad en pantalla) y actualiza la memoria.</summary>
    private void PintarSurco(int numero)
    {
        if (_guardando || _impl == null) return;
        var arr = SurcosActuales();
        if (numero < 1 || numero > arr.Count) return;
        if (arr[numero - 1].TrenId == _pincel) return;
        arr[numero - 1].TrenId = _pincel;
        ActualizarMemoria(arr);
        EnsuciarTrenes();
        PintarTrenes();
    }

    private void AgregarTren()
    {
        if (_guardando || _impl == null) return;
        var trenes = _impl.Trenes;
        if (trenes.Count >= MaxTrenes) { C.Estado?.Invoke("Máximo 4 trenes", "err"); return; }
        int maxId = 0;
        foreach (var t in trenes) if (t.Id > maxId) maxId = t.Id;
        trenes.Add(new TrenDto
        {
            Id = maxId + 1,
            Nombre = trenes.Count == 1 ? "Trasero" : ("Tren " + (maxId + 1)),
            DistanciaM = 0,
        });
        EnsuciarTrenes();
        PintarTrenes();
    }

    /// <summary>Quitar un tren: sus surcos vuelven al tren 1 TAMBIÉN EN LA
    /// MEMORIA — si no, reaparecen al retipear la cantidad.</summary>
    private void QuitarTren(int id)
    {
        if (_guardando || _impl == null || id == 1) return;
        _impl.Trenes = _impl.Trenes.Where(t => t.Id != id).ToList();
        foreach (var s in _memoria) if (s.TrenId == id) s.TrenId = 1;
        if (_pincel == id) _pincel = 1;
        EnsuciarTrenes();
        PintarTrenes();
    }

    /// <summary>`trnGuardar()`: PUT del implemento COMPLETO. Solo si hay algo
    /// que guardar. Ver la advertencia del bug "puse 14 y volvió a 3".</summary>
    private async Task<bool> GuardarTrenesAsync()
    {
        if (_impl == null || !_dirtyTrn) return true;
        if (C.Implemento == null) return true;

        var surcos = SurcosActuales();
        _impl.Surcos = surcos;
        _impl.NumeroSurcos = surcos.Count;

        // CLAVE: sincronizar la lista de secciones del implemento a la misma
        // cantidad, preservando nombre y lookaheads de las existentes. El
        // write-back central→Tool deriva NumSections de acá.
        var secs = new List<SeccionImplementoDto>(surcos.Count);
        for (int i = 1; i <= surcos.Count; i++)
        {
            var prev = (_impl.Secciones != null && i - 1 < _impl.Secciones.Count) ? _impl.Secciones[i - 1] : null;
            secs.Add(new SeccionImplementoDto
            {
                Id = i,
                Nombre = string.IsNullOrWhiteSpace(prev?.Nombre) ? ("Sección " + i) : prev!.Nombre,
                LookaheadOn = prev?.LookaheadOn ?? 0,
                LookaheadOff = prev?.LookaheadOff ?? 0,
            });
        }
        _impl.Secciones = secs;

        double anchoM = _modo == "ind" ? C.Disp2M(TotalInd()) : _numMulti * _widthMulti;
        _impl.AnchoTotalM = anchoM;
        if (surcos.Count > 0) _impl.DistanciaEntreSurcosM = anchoM / surcos.Count;

        // Mejora consciente sobre el HTML (ver "EL PUT DEL IMPLEMENTO PISA…"):
        // el implemento guarda su PROPIA copia de "cortar fuera del lote" y el
        // write-back la baja al motor, deshaciendo el valor que se acaba de
        // guardar dos líneas más arriba. Se sincroniza con lo que el operario
        // eligió ACÁ — es el único lugar de la UI donde ese flag se edita, así
        // que no se le pisa la decisión a nadie.
        _impl.SectionOffWhenOut = _boundary;

        ImplementoGuardado? r = null;
        try { r = await C.Implemento.PutActivoAsync(_impl).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch { }

        if (r == null || !r.Ok)
        {
            // La geometría YA quedó guardada: el reintento postea SOLO los
            // trenes (_dirtySec ya está limpio).
            C.Estado?.Invoke("Secciones guardadas, trenes NO: " + (r?.Motivo() ?? "sin conexión"), "err");
            return false;
        }

        _dirtyTrn = false;
        ActualizarMemoria(surcos);

        // El write-back central→Tool NO manda el modo de secciones, y el DTO
        // nativo trae isSectionsNotZones = true por default: guardar el
        // implemento DEJA EL MODO EN "INDIVIDUALES" (verificado en banco). Es un
        // agujero del backend, no del porteo — la página HTML hace lo mismo.
        // Mientras no se arregle allá, el operario tiene que enterarse: quedarse
        // callado sería dejarlo sembrando con un modo de corte que él no eligió.
        if (_modo == "zonas")
            C.Aviso?.Invoke(T("Trenes guardados. Ojo: guardar el implemento deja el modo de secciones en «individuales» — revisalo antes de salir al lote."));
        else if (AnchosDesiguales())
        {
            // Hermano del anterior y igual de caro: SaveTool rellena los anchos
            // con Width/N cuando el DTO no trae sectionWidths (MapToToolConfig
            // nunca los manda), así que guardar trenes EMPAREJA los anchos que
            // el operario había puesto distintos. Las secciones dejan de
            // empezar y terminar donde él las midió: eso mueve por dónde corta
            // la máquina. Avisar es lo mínimo mientras el backend no lo arregle.
            C.Aviso?.Invoke(T("Trenes guardados. Ojo: guardar el implemento empareja los anchos de las secciones — revisá las casillas antes de salir al lote."));
        }

        // El PUT acaba de PISAR geometría del guiado (modo y anchos, arriba).
        // Sin releer, la pantalla se queda mostrando lo que el motor YA NO
        // tiene: el operario vería "zonas, 24 secciones" con el motor cortando
        // en "individuales, 16". Preferimos un GET de más antes que mentirle
        // sobre cómo está partido el implemento.
        if (C.RefrescarSnapshot != null)
        {
            try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
            catch (OperationCanceledException) { }
            catch { }
        }

        return true;
    }

    /// <summary>¿Las secciones activas del modo individuales tienen anchos
    /// distintos entre sí? (1 mm de tolerancia: los viajes display→metros
    /// dejan colas de redondeo que no son una diferencia real).</summary>
    private bool AnchosDesiguales()
    {
        for (int i = 1; i < _num && i < _widths.Length; i++)
            if (Math.Abs(_widths[i] - _widths[0]) > C.M2Disp(0.001)) return true;
        return false;
    }

    // =======================================================================
    //  Validación (réplicas de leerNud / leerNudDec)
    // =======================================================================

    /// <summary>`leerNud`: entero con Math.abs, clamp al rango y reescritura del
    /// campo. null = no es número (queda en rojo).</summary>
    private int? LeerNud(TextBox? t, (double min, double max) lim)
    {
        if (t == null) return null;
        double v = ParseDouble(t);
        if (double.IsNaN(v) || double.IsInfinity(v)) { Invalido(t, true); return null; }
        v = Math.Abs(v);
        if (v < lim.min) v = lim.min;
        if (v > lim.max) v = lim.max;
        int r = (int)CfgCtx.RedondeoJs(v);
        Invalido(t, false);
        SetTexto(t, r.ToString(Inv));
        return r;
    }

    /// <summary>`leerNudDec`: un decimal, clamp al rango, sin Math.abs (los
    /// mínimos son ≥ 0, así que el clamp ya se come los negativos).</summary>
    private double? LeerNudDec(TextBox? t, (double min, double max) lim)
    {
        if (t == null) return null;
        double v = ParseDouble(t);
        if (double.IsNaN(v) || double.IsInfinity(v)) { Invalido(t, true); return null; }
        if (v < lim.min) v = lim.min;
        if (v > lim.max) v = lim.max;
        v = CfgCtx.RedondeoJs(v * 10.0) / 10.0;
        Invalido(t, false);
        SetTexto(t, Num(v));
        return v;
    }

    /// <summary>parseFloat con coma o punto. NaN = no parsea.</summary>
    private static double ParseDouble(TextBox? t)
    {
        if (t == null) return double.NaN;
        string s = (t.Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, Inv, out double v) ? v : double.NaN;
    }

    private static void Invalido(TextBox t, bool mal)
    {
        t.BorderBrush = mal ? CfgUi.Err : CfgUi.Borde;
        t.Background = mal ? BgNudMal : BgNud;
    }

    // =======================================================================
    //  Piezas y helpers
    // =======================================================================

    private static string T(string s) => PilotX.Cockpit.Bars.Traductor.T(s);

    /// <summary>Math.round de JS sobre un entero de display (los anchos y los
    /// totales se muestran enteros).</summary>
    private static string Ent(double v) => CfgCtx.RedondeoJs(v).ToString("0", Inv);

    /// <summary>Número como lo imprimiría JavaScript, siempre invariante.</summary>
    private static string Num(double v) => v.ToString("0.####", Inv);

    private void Ensuciar()
    {
        _dirtySec = true;
        C.MarcarSucio?.Invoke();
    }

    /// <summary>Tocar trenes ensucia LAS DOS cosas, igual que el HTML: el
    /// `leave()` con solo la geometría limpia postea únicamente el implemento.
    /// </summary>
    private void EnsuciarTrenes()
    {
        _dirtyTrn = true;
        C.MarcarSucio?.Invoke();
    }

    /// <summary>Escritura por código: no cuenta como gesto del operario.</summary>
    private void SetTexto(TextBox t, string s)
    {
        bool antes = _cargando;
        _cargando = true;
        try { t.Text = s; }
        finally { _cargando = antes; }
    }

    private void SetCombo(ComboBox c, int indice)
    {
        bool antes = _cargando;
        _cargando = true;
        try { c.SelectedIndex = Math.Max(0, Math.Min(indice, Math.Max(0, c.Items.Count - 1))); }
        finally { _cargando = antes; }
    }

    /// <summary>El `.nud` del CSS, versión compacta. Pide el teclado nativo al
    /// enfocarse (sin él el operario no puede tipear en cabina).</summary>
    private TextBox Nud(string titulo, double ancho, bool numerico = true)
    {
        var t = new TextBox
        {
            Width = ancho, MinHeight = 44,
            FontSize = 16, FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = BgNud, Foreground = CfgUi.Texto,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
        };
        string tit = titulo;
        bool num = numerico;
        t.GotFocus += (_, __) => _ = C.Client?.TecladoAsync(true, num, tit) ?? Task.CompletedTask;
        t.LostFocus += (_, __) => _ = C.Client?.TecladoAsync(false) ?? Task.CompletedTask;
        return t;
    }

    private static TextBlock Unidad(string texto = "")
        => new TextBlock
        {
            Text = texto, Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>Etiqueta chica arriba + control (+ unidad al costado).</summary>
    private static Control CampoFila(string etiqueta, Control control, TextBlock? unidad = null)
    {
        var sp = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 14, 8) };
        sp.Children.Add(CfgUi.Etiqueta(etiqueta));
        if (unidad == null) { sp.Children.Add(control); return sp; }
        var fila = CfgUi.Fila(6);
        fila.Children.Add(control);
        fila.Children.Add(unidad);
        sp.Children.Add(fila);
        return sp;
    }

    private static Control EtiquetaConValor(string etiqueta, TextBlock valor)
    {
        var fila = CfgUi.Fila(8);
        fila.Children.Add(new TextBlock
        {
            Text = T(etiqueta), Foreground = CfgUi.Texto, FontSize = 13, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        fila.Children.Add(valor);
        return fila;
    }

    private void PintarHabilitado()
    {
        bool editable = Editable() && !_guardando;
        foreach (var t in new Control?[] { _cboNum, _cboZonas, _txtDefWidth, _txtNumMulti,
                                           _txtWidthMulti, _txtCutoff, _txtCoverage,
                                           _grillaAnchos, _grillaZonas })
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
        => (_txtDefWidth?.IsFocused ?? false)
        || (_txtNumMulti?.IsFocused ?? false)
        || (_txtWidthMulti?.IsFocused ?? false)
        || (_txtCutoff?.IsFocused ?? false)
        || (_txtCoverage?.IsFocused ?? false);

    // =======================================================================
    //  Dibujos
    // =======================================================================

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
