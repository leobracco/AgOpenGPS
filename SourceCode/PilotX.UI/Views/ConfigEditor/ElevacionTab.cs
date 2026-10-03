// ============================================================================
// ElevacionTab.cs — pestaña "GPS / IMU › Elevación" del ConfigPanel nativo.
//
// Planimetría fase 1: el toggle "Registrar elevación" (setting EXISTENTE
// setDisplay_isLogElevation, sección `display` del wire) y, debajo, el estado
// VIVO del registro: si está grabando o en pausa y por qué, y cuántos puntos
// tiene Elevation.txt del lote abierto.
//
// Por qué vive acá y no en Display: el toggle existía solo en la pestaña
// Display de config.html, que salió del menú (2026-08-03) — o sea, en la
// cabina no se llegaba. Es un tema de GPS (necesita RTK fijo), así que va al
// lado de Rumbo y Rolido. La página HTML no se toca (la usa la PWA).
//
// Guardado AL TOQUE (como "Invertir rolido"): es un interruptor, no un número
// que se edita; si quedara pendiente hasta Guardar el estado de abajo
// mentiría. Sin botón Guardar (TieneGuardar = false).
//
// El vivo sale del propio snapshot (GET /api/aog/config → `elevacion`), que
// el shell refresca cada 3 s y después llama a Live(): no hay poll propio.
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

public sealed class ElevacionTab : ConfigTab
{
    private Border? _tile;
    private TextBlock? _txtTile;
    private Border? _punto;
    private TextBlock? _txtEstado;
    private TextBlock? _txtPuntos;
    private TextBlock? _txtError;
    private bool _activo;
    private bool _guardando;
    private int _estadoPintado = -1;

    public ElevacionTab(CfgCtx c) : base(c) { }

    private static string T(string s) => PilotX.Cockpit.Bars.Traductor.T(s);

    public override void Rebuild()
    {
        Children.Clear();
        _estadoPintado = EstadoActual();

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

        _activo = C.Snap?.Display?.LogElevation ?? false;

        var col = new StackPanel { Spacing = 10 };
        col.Children.Add(CfgUi.Titulo("Registro de alturas del lote"));
        col.Children.Add(CfgUi.Nota(
            "Mientras trabajás, PilotX guarda la altura del suelo un punto por metro en el lote abierto, "
            + "para armar después el mapa de alturas en OrbitX. Solo graba con RTK FIJO: con RTK flotante "
            + "o sin corrección la altura varía metros y no sirve."));
        col.Children.Add(TileToggle());

        // ---- estado vivo ---------------------------------------------------
        var filaEstado = CfgUi.Fila(8);
        _punto = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
            Background = CfgUi.Dim, VerticalAlignment = VerticalAlignment.Center,
        };
        _txtEstado = new TextBlock
        {
            Text = "—", Foreground = CfgUi.Texto, FontSize = 15, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        filaEstado.Children.Add(_punto);
        filaEstado.Children.Add(_txtEstado);
        col.Children.Add(filaEstado);

        _txtPuntos = new TextBlock
        {
            Text = "", Foreground = CfgUi.TextoMuted, FontSize = 13, FontFamily = CfgUi.Mono,
        };
        col.Children.Add(_txtPuntos);

        _txtError = new TextBlock
        {
            Text = "", Foreground = CfgUi.TextoError, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        col.Children.Add(_txtError);

        col.Children.Add(CfgUi.Nota(
            "Se pausa solo: sin RTK fijo, con la máquina parada o en marcha atrás. "
            + "Los puntos quedan en Elevation.txt dentro de la carpeta del lote y viajan a OrbitX con el resto del lote. "
            + "La altura de la antena (Vehículo › Antena) tiene que estar bien medida: se resta para llegar al suelo."));

        Children.Add(CfgUi.Carta(col));
        Pintar();
    }

    public override void Live()
    {
        if (_estadoPintado != EstadoActual()) { Rebuild(); return; }
        if (!_guardando) _activo = C.Snap?.Display?.LogElevation ?? _activo;
        Pintar();
    }

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    // ---- toggle --------------------------------------------------------------

    private Border TileToggle()
    {
        var img = new Image
        {
            Source = Icono("ConD_LogElevation.png"),
            Width = 44, Height = 44, MaxWidth = 44, MaxHeight = 44,
            Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        };
        _txtTile = new TextBlock
        {
            Foreground = CfgUi.Texto, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        var pila = CfgUi.Fila(10);
        pila.Children.Add(img);
        pila.Children.Add(_txtTile);

        _tile = new Border
        {
            MinHeight = 56, HorizontalAlignment = HorizontalAlignment.Left,
            Background = CfgUi.BgFila, BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 4, 14, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };
        _tile.Tapped += (_, __) => { if (!_guardando) _ = ToggleAsync(); };
        return _tile;
    }

    private async Task ToggleAsync()
    {
        if (C.Client == null) return;
        bool antes = _activo;
        _activo = !_activo;
        _guardando = true;
        Pintar();
        try
        {
            C.Estado?.Invoke("Guardando…", "");
            var r = await C.Client.GuardarAsync("display", new { log_elevation = _activo }).ConfigureAwait(true);
            if (r == null || !r.Ok)
            {
                _activo = antes;   // el motor no cambió nada: no mentir
                C.Aviso?.Invoke(T("No se pudo cambiar el registro de alturas"));
                C.Estado?.Invoke(r == null ? "Sin conexión con PilotX"
                    : "Error: " + (string.IsNullOrWhiteSpace(r.Error) ? "desconocido" : r.Error), "err");
                return;
            }
            C.Estado?.Invoke(_activo ? "Registro de alturas activado ✔" : "Registro de alturas apagado ✔", "ok");
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

    // ---- pintura ---------------------------------------------------------------

    private void Pintar()
    {
        if (_tile != null)
        {
            _tile.BorderBrush = _activo ? CfgUi.Verde : CfgUi.Borde;
            _tile.Background = _activo ? CfgUi.BgFilaSel : CfgUi.BgFila;
            _tile.Opacity = _guardando ? 0.6 : 1.0;
        }
        if (_txtTile != null)
            _txtTile.Text = T("Registrar elevación") + "  ·  " + T(_activo ? "Activado" : "Apagado");

        var e = C.Snap?.Elevacion;
        string estado = e?.Estado ?? "";
        (string texto, IBrush color) = estado switch
        {
            "grabando"     => ("Grabando alturas (RTK fijo)", CfgUi.Ok),
            "sin_rtk"      => ("En pausa: sin RTK fijo", CfgUi.Warn),
            "detenido"     => ("En pausa: máquina parada", CfgUi.Warn),
            "marcha_atras" => ("En pausa: marcha atrás", CfgUi.Warn),
            "sin_lote"     => ("En pausa: abrí un lote para grabar", CfgUi.Dim),
            "apagado"      => ("No se están registrando alturas", CfgUi.Dim),
            _              => ("—", CfgUi.Dim),
        };
        if (_txtEstado != null) _txtEstado.Text = T(texto);
        if (_punto != null) _punto.Background = color;

        if (_txtPuntos != null)
        {
            bool hayLote = C.Snap?.IsJobStarted ?? false;
            _txtPuntos.Text = (e != null && hayLote && estado != "apagado")
                ? T("Puntos en este lote") + ": " + e.Puntos.ToString("#,0", CultureInfo.GetCultureInfo("es-AR"))
                : "";
        }
        if (_txtError != null)
        {
            string? err = e?.Error;
            _txtError.IsVisible = !string.IsNullOrWhiteSpace(err);
            _txtError.Text = string.IsNullOrWhiteSpace(err) ? "" : T("No se pudo escribir Elevation.txt") + ": " + err;
        }
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
