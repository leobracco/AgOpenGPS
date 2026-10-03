// ============================================================================
// UnidadesTab.cs — pestaña "Otros › Unidades" del ConfigPanel nativo.
// Porteo de la pestaña HUÉRFANA `display` de config.html (la sección
// data-tab="display" + tabs.display de config.js). Clave de NAV: "display", la
// misma de ?tab= del HTML, así un deep-link viejo cae acá.
//
// QUÉ SE PORTÓ: SOLO el conmutador Métrico / Imperial (setting persistido
// setMenu_isMetric, wire `is_metric`). Es el único del producto.
//
// QUÉ NO SE PORTÓ Y POR QUÉ (verificado el 2026-10-03 contra el código, no de
// memoria): el resto de la pestaña son flags de dibujo del AgOpenGPS WinForms
// que en PilotX NO tienen ningún consumidor. El motor los guarda en Settings y
// nadie los lee — un botón que no hace nada es peor que no tenerlo:
//   · polygons          → solo una copia runtime en EngineConfigVehiculoService
//                         (_dibujarPoligonos); nadie la lee.
//   · speedo, keyboard, brightness, start_full_screen, section_lines,
//     line_smooth, direction_markers → setDisplay_*/setMenu_*/setTool_* sin
//                         ninguna referencia fuera de Settings.cs y del propio
//                         servicio de config. El brillo de la cabina va por
//                         /api/sistema (botones brillo_up/dn), no por este flag;
//                         la ventana arranca siempre a pantalla completa (kiosko).
//   · floor             → el piso texturado del mapa (MapGlSurface.DrawFloor) se
//                         carga SIEMPRE desde img/mapa/suelo.png
//                         (MainWindow.CargarSpriteVehiculoAsync); no mira
//                         setDisplay_isTextureOn.
//   · grid              → la grilla del mapa está DESACTIVADA en MapGlSurface
//                         desde el 2026-07-28 (DrawGrid comentado, pedido del
//                         usuario) y setMenu_isGridOn no lo lee nadie.
//   · extra_guides, svenn_arrow → el motor tiene isSideGuideLines e
//                         isSvennArrowOn pero NADIE los asigna desde Settings:
//                         quedan en false para siempre. Las guías vecinas del mapa
//                         nativo las calcula MapGlSurface.RebuildGuidanceParallel
//                         por su cuenta (cubren el lote, tope 40 por lado).
//   · num_guide_lines   → ABLine.numGuideLines solo lo usan
//                         CABCurve.BuildCurveGuidelines y GuidanceDrawExtensions,
//                         los dos detrás de IsSideGuideLines (siempre false) — y
//                         el dibujo de GuidanceDrawExtensions es del GL legacy, no
//                         del mapa nativo.
//   · headland_distance → el motor SÍ lo carga (isHeadlandDistanceOn) y
//                         CHead.CheckHeadlandProximity lo mira, pero solo para
//                         llamar a PlayHeadlandSound(), que en el motor headless
//                         es un método VACÍO (GuidanceEngineHost.Models.cs), y la
//                         alarma de Sonidos (SonidosAlarmService) no tiene evento
//                         de cabecera. Efecto para el operario: ninguno.
//   · log_elevation     → vivo, pero ya tiene su pestaña (GPS / IMU › Elevación).
// Si algún día el mapa o el motor vuelven a consumir uno de esos flags, el wire
// (`display` en /api/aog/config) ya los acepta: se agrega su tile acá.
//
// ---------------------------------------------------------------------------
// MÉTRICO / IMPERIAL — QUÉ CAMBIA DE VERDAD (verificado):
//   · el wire de /api/aog/config viaja SIEMPRE en SI (metros, km/h). Las
//     pestañas convierten SOLO para mostrar/editar (CfgCtx.M2Disp/Disp2M y las
//     conversiones propias de Rumbo, Secciones, U-Turn…), y cada una relee
//     C.IsMetric al entrar. Cambiar la unidad NO reescribe ningún valor
//     guardado: no hay nada que pueda quedar "mal convertido";
//   · el motor usa setMenu_isMetric en UN lugar: el tope del ancho total de
//     secciones (50 m métrico / 48,26 m imperial, GuardarSecciones);
//   · la CABINA no cambia: el motor tiene isMetric=true fijo (nadie lo asigna)
//     y el cockpit muestra siempre km/h, ha, kg/ha. Imperial = solo la
//     Configuración (este panel y la página del celular). Se le dice así al
//     operario en la carta, para que no espere ver mph en la barra;
//   · persiste con el perfil del vehículo (Settings.Save del dispatcher, que es
//     no-op sin perfil — mismo caso que las demás pestañas de este panel);
//   · en imperial, editar y guardar un valor redondea a pulgadas enteras (hasta
//     ~1,2 cm por ciclo). Las pestañas ya viven con eso y no postean sin cambios.
//
// Guardado AL TOQUE (como el HTML, que guarda y recarga): son dos opciones
// excluyentes, no un número que se edita. Se postea SOLO `is_metric` (el
// GuardarDisplay del motor aplica únicamente los campos presentes), así no se
// pisa ningún otro flag de `display`. Después se relee el snapshot: el footer
// ("Unidades: …") y las demás pestañas toman la unidad nueva al entrar. Sin
// botón Guardar (TieneGuardar = false).
// ============================================================================

using System;
using System.Collections.Generic;
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

public sealed class UnidadesTab : ConfigTab
{
    private Border? _tileMetrico, _tileImperial;
    private bool _metrico = true;
    private bool _guardando;
    private int _estadoPintado = -1;

    public UnidadesTab(CfgCtx c) : base(c) { }

    private static string T(string s) => PilotX.Cockpit.Bars.Traductor.T(s);

    public override Task AlEntrarAsync()
    {
        Rebuild();
        return Task.CompletedTask;
    }

    public override void Rebuild()
    {
        Children.Clear();
        _estadoPintado = EstadoActual();

        // Mismo criterio que TramTab: el TabHost cuelga de un ScrollViewer con
        // scroll horizontal; sin tope + Left la carta se estira o se centra.
        MaxWidth = 686;
        HorizontalAlignment = HorizontalAlignment.Left;

        if (C.SinDatos)
        {
            Children.Add(CfgUi.Carta(CfgUi.Nota("PilotX no responde — todavía no llegaron los datos.")));
            return;
        }
        if (C.ServicioCaido)
        {
            Children.Add(CfgUi.ChipError("Servicio de configuración no disponible", "AGP-NET-201"));
            return;
        }

        _metrico = C.IsMetric;

        var col = new StackPanel { Spacing = 10 };
        col.Children.Add(CfgUi.Titulo("Unidades de la configuración"));

        var fila = new WrapPanel { Orientation = Orientation.Horizontal };
        _tileMetrico = Tile("ConD_Metric.png", "Métrico", "cm · m · km/h", true);
        _tileImperial = Tile("ConD_Imperial.png", "Imperial", "in · ft · mph", false);
        fila.Children.Add(_tileMetrico);
        fila.Children.Add(_tileImperial);
        col.Children.Add(fila);

        col.Children.Add(CfgUi.Nota(
            "Cambia en qué unidades se ven y se cargan las medidas de esta Configuración "
            + "(vehículo, implemento, secciones, U-Turn, trochas…). Los valores guardados no se tocan: "
            + "solo cambia cómo se muestran."));
        col.Children.Add(CfgUi.Nota(
            "La pantalla de trabajo sigue en km/h, hectáreas y kg/ha con cualquiera de las dos."));

        var carta = CfgUi.Carta(col);
        carta.MaxWidth = 560;
        carta.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(carta);
        Pintar();
    }

    /// <summary>Refresco de fondo (3 s): sin campos de texto, se puede repintar
    /// sin miedo a tirar el foco. Si la unidad cambió desde otro lado (el
    /// celular), se refleja.</summary>
    public override void Live()
    {
        if (_estadoPintado != EstadoActual()) { Rebuild(); return; }
        if (!_guardando) _metrico = C.IsMetric;
        Pintar();
    }

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    // ---- opciones -------------------------------------------------------------

    private Border Tile(string icono, string titulo, string detalle, bool esMetrico)
    {
        // MaxWidth/MaxHeight EXPLÍCITOS: el Style de BarStyles.axaml limita
        // toda imagen de la ventana y le gana al Width local.
        var img = new Image
        {
            Source = Icono(icono),
            Width = 56, Height = 56, MaxWidth = 56, MaxHeight = 56,
            Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var pila = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        pila.Children.Add(img);
        pila.Children.Add(new TextBlock
        {
            Text = T(titulo), Foreground = CfgUi.Texto, FontSize = 16, FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        pila.Children.Add(new TextBlock
        {
            Text = detalle, Foreground = CfgUi.TextoMuted, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var b = new Border
        {
            Width = 150, MinHeight = 120,
            Margin = new Thickness(0, 0, 12, 0),
            Background = CfgUi.BgFila, BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };
        // Tapped, no PointerPressed: el táctil manda Tapped y el PointerPressed
        // salta también con el arrastre del scroll.
        b.Tapped += (_, __) => { if (!_guardando && _metrico != esMetrico) _ = CambiarAsync(esMetrico); };
        return b;
    }

    private async Task CambiarAsync(bool metrico)
    {
        if (C.Client == null) { C.Estado?.Invoke("Sin conexión con PilotX", "err"); return; }
        bool antes = _metrico;
        _metrico = metrico;
        _guardando = true;
        Pintar();
        try
        {
            C.Estado?.Invoke("Guardando…", "");
            var r = await C.Client.GuardarAsync("display", new { is_metric = metrico }).ConfigureAwait(true);
            if (r == null || !r.Ok)
            {
                _metrico = antes;   // el motor no cambió nada: no mentir
                C.Aviso?.Invoke(T("No se pudieron cambiar las unidades"));
                C.Estado?.Invoke(r == null ? "Sin conexión con PilotX"
                    : "Error: " + (string.IsNullOrWhiteSpace(r.Error) ? "desconocido" : r.Error), "err");
                return;
            }
            C.Estado?.Invoke(metrico ? "Unidades: métrico ✔" : "Unidades: imperial ✔", "ok");
            // Relectura: CfgCtx.IsMetric sale del snapshot, y de ahí el footer y
            // las conversiones de todas las pestañas.
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

    // ---- pintura ----------------------------------------------------------------

    private void Pintar()
    {
        PintarTile(_tileMetrico, _metrico);
        PintarTile(_tileImperial, !_metrico);
    }

    private void PintarTile(Border? b, bool sel)
    {
        if (b == null) return;
        b.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
        b.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
        b.Opacity = _guardando ? 0.6 : 1.0;
    }

    private static readonly Dictionary<string, Bitmap?> _iconos = new Dictionary<string, Bitmap?>(StringComparer.Ordinal);

    private static Bitmap? Icono(string nombre)
    {
        if (_iconos.TryGetValue(nombre, out var cacheado)) return cacheado;
        Bitmap? bmp;
        try { bmp = new Bitmap(AssetLoader.Open(new Uri("avares://PilotX.UI/Assets/config/" + nombre))); }
        catch { bmp = null; }   // falta el asset ⇒ tile sin dibujo, nunca una excepción
        _iconos[nombre] = bmp;
        return bmp;
    }
}
