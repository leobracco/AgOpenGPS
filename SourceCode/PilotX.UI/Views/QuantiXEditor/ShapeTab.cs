// ============================================================================
// ShapeTab.cs — tab Shape: dosis variable (shapefile + prescripciones).
//
// QUÉ QUEDÓ NATIVO: elegir .shp/.shx/.dbf y subirlos, quitar la capa activa,
// la biblioteca de prescripciones (activar/desactivar + campo de dosis), la
// ficha de la capa activa con vista previa y el toque para consultar o EDITAR
// la dosis de una zona.
// QUÉ SIGUE EN HTML: la misma tab en pages/quantix.html, para la PWA.
//
// QUÉ NO SE PORTA: el drag & drop de archivos. La cabina es táctil y no tiene
// explorador flotante: el tap abre el picker del sistema (que es como entra
// el pendrive de verdad). El drag&drop del HTML es un extra de escritorio.
//
// ÍNDICE DE ZONA: al editar la dosis viaja `fi` (feature del ARCHIVO), nunca
// el índice de dibujo — los MultiPolygon se parten y corren la numeración.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.Controls;

namespace PilotX.Desktop.Views.QuantiXEditor;

public sealed class ShapeTab : QxTab
{
    private static readonly string[] REQ = { ".shp", ".shx", ".dbf" };
    private static readonly string[] OPT = { ".prj", ".cpg" };

    private readonly List<(string Nombre, string Ext, byte[] Bytes)> _elegidos = new();

    private readonly StackPanel _listaArchivos = new() { Spacing = 4 };
    private readonly WrapPanel _chipsEstado = new();
    private readonly TextBlock _msg = QxUi.Msg();
    private Button? _btnSubir;

    private readonly StackPanel _biblioteca = new() { Spacing = 4 };
    private readonly StackPanel _capaActiva = new() { Spacing = 8 };

    private readonly Dictionary<string, ComboBox> _combosProp = new(StringComparer.Ordinal);

    private QxShapeLayer? _capa;
    private QxPrescActiva? _prescActiva;
    private readonly ShapePreviewControl _preview = new();
    private readonly StackPanel _tapOut = new() { Spacing = 6, Orientation = Orientation.Horizontal };
    private QxShapeZona? _zonaEditando;
    private double _valorEditando;

    public ShapeTab(QxEditorCtx c) : base(c)
    {
        _preview.Height = 260;
        _preview.ZonaTocada += OnZonaTocada;
    }

    public override void Rebuild()
    {
        Children.Clear();
        // Reusados adentro de contenedores que se rearman: sin soltarlos,
        // Avalonia tira "already has a visual parent" al volver a la tab.
        QxUi.Soltar(_listaArchivos); QxUi.Soltar(_chipsEstado); QxUi.Soltar(_msg);
        QxUi.Soltar(_biblioteca); QxUi.Soltar(_capaActiva);

        // ---- card de upload ----
        var up = new StackPanel { Spacing = 8 };
        up.Children.Add(QxUi.Titulo("Cargar shapefile"));
        up.Children.Add(QxUi.Sub("Subí el .shp, el .shx y el .dbf juntos (el .prj y el .cpg son "
                               + "opcionales). La dosis se lee de la columna del DBF que elijas en "
                               + "Motores → mapa de dosis."));

        var btnElegir = QxUi.Boton("Elegir archivos del pendrive", () => _ = ElegirArchivosAsync());
        up.Children.Add(btnElegir);
        up.Children.Add(_listaArchivos);
        up.Children.Add(_chipsEstado);

        var accs = QxUi.Fila();
        _btnSubir = QxUi.Boton("Subir y cargar en PilotX", () => _ = SubirAsync(), primario: true);
        _btnSubir.IsEnabled = false;
        accs.Children.Add(_btnSubir);
        accs.Children.Add(QxUi.Boton("Quitar capa actual", () => _ = QuitarCapaAsync()));
        accs.Children.Add(_msg);
        up.Children.Add(accs);
        Children.Add(QxUi.Card(up));

        // ---- biblioteca ----
        var lib = new StackPanel { Spacing = 8 };
        lib.Children.Add(QxUi.Titulo("Prescripciones guardadas"));
        lib.Children.Add(_biblioteca);
        Children.Add(QxUi.Card(lib));

        // ---- capa activa ----
        var act = new StackPanel { Spacing = 8 };
        act.Children.Add(QxUi.Titulo("Capa activa"));
        act.Children.Add(_capaActiva);
        Children.Add(QxUi.Card(act));

        RenderArchivos();
    }

    public override async Task AlEntrarAsync()
    {
        await RefrescarBibliotecaAsync().ConfigureAwait(true);
        await RefrescarCapaAsync().ConfigureAwait(true);
    }

    // =======================================================================
    //  Selección de archivos
    // =======================================================================

    private async Task ElegirArchivosAsync()
    {
        // Explorador PROPIO de PilotX (card nativa táctil, USB arriba de
        // todo). El truco de las prescripciones NO cambia: el picker devuelve
        // rutas y este llamador sigue juntando el .shp con sus hermanos
        // .shx/.dbf. Fallback al StorageProvider del sistema en no-Windows.
        if (ExploradorArchivos.CardDisponible)
        {
            var rutas = await ExploradorArchivos.ElegirAsync(
                PilotX.Cockpit.Bars.Traductor.T("Elegí el .shp, el .shx y el .dbf"),
                new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" },
                multiple: true);
            if (rutas == null) return;
            foreach (var ruta in rutas)
            {
                string nombre = Path.GetFileName(ruta);
                string ext2 = Path.GetExtension(nombre).ToLowerInvariant();
                if (!REQ.Contains(ext2) && !OPT.Contains(ext2)) continue;
                try
                {
                    var bytes = File.ReadAllBytes(ruta);
                    // Re-elegir una extensión reemplaza la anterior (mismo
                    // criterio que el camino StorageProvider de abajo).
                    _elegidos.RemoveAll(x => x.Ext == ext2);
                    _elegidos.Add((nombre, ext2, bytes));
                }
                catch { }
            }
            RenderArchivos();
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider == null) { QxUi.SetMsg(_msg, "✕ no se pudo abrir el explorador", "err"); return; }

        var archivos = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = PilotX.Cockpit.Bars.Traductor.T("Elegí el .shp, el .shx y el .dbf"),
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Shapefile")
                {
                    Patterns = new[] { "*.shp", "*.shx", "*.dbf", "*.prj", "*.cpg" },
                },
            },
        }).ConfigureAwait(true);

        foreach (var f in archivos)
        {
            string nombre = f.Name ?? "";
            string ext = Path.GetExtension(nombre).ToLowerInvariant();
            // Extensiones no aceptadas: se ignoran en silencio, no se rompe el flujo.
            if (!REQ.Contains(ext) && !OPT.Contains(ext)) continue;
            try
            {
                await using var s = await f.OpenReadAsync().ConfigureAwait(true);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms).ConfigureAwait(true);
                // Re-elegir una extensión reemplaza la anterior.
                _elegidos.RemoveAll(x => x.Ext == ext);
                _elegidos.Add((nombre, ext, ms.ToArray()));
            }
            catch { }
        }
        RenderArchivos();
    }

    private void RenderArchivos()
    {
        _listaArchivos.Children.Clear();
        _chipsEstado.Children.Clear();

        foreach (var f in _elegidos)
        {
            var fila = QxUi.Fila(10);
            fila.Children.Add(QxUi.Chip(f.Ext, REQ.Contains(f.Ext) ? QxUi.Verde : QxUi.TextoMuted));
            fila.Children.Add(new TextBlock
            {
                Text = f.Nombre, Foreground = QxUi.Texto, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            fila.Children.Add(QxUi.Mono2(FormatoBytes(f.Bytes.Length)));
            _listaArchivos.Children.Add(fila);
        }

        var tengo = new HashSet<string>(_elegidos.Select(x => x.Ext), StringComparer.Ordinal);
        foreach (var ext in REQ)
            _chipsEstado.Children.Add(QxUi.Chip(ext + (tengo.Contains(ext) ? " ✓" : " " + PilotX.Cockpit.Bars.Traductor.T("falta")),
                                                tengo.Contains(ext) ? QxUi.Ok : QxUi.Err));
        foreach (var ext in OPT)
            if (tengo.Contains(ext)) _chipsEstado.Children.Add(QxUi.Chip(ext + " ✓", QxUi.Ok));

        if (_btnSubir != null)
            _btnSubir.IsEnabled = REQ.All(e => tengo.Contains(e));
    }

    private static string FormatoBytes(int n)
    {
        if (n < 1024) return n + " B";
        if (n < 1024 * 1024) return (n / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        return (n / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
    }

    private async Task SubirAsync()
    {
        if (_elegidos.Count == 0) return;
        QxUi.SetMsg(_msg, "Subiendo y cargando en PilotX…", "");
        if (_btnSubir != null) _btnSubir.IsEnabled = false;

        var payload = _elegidos.Select(x => (x.Nombre, x.Bytes)).ToList();
        var r = await C.Client.SubirShapeAsync(payload).ConfigureAwait(true);
        if (r.Ok)
        {
            QxUi.SetMsg(_msg, "✓ Cargado · " + r.PolygonCount + " polígonos", "ok");
            _elegidos.Clear();
            RenderArchivos();
            await RefrescarCamposAsync().ConfigureAwait(true);
            await RefrescarCapaAsync().ConfigureAwait(true);
        }
        else
        {
            QxUi.SetMsg(_msg, "✕ " + (string.IsNullOrEmpty(r.Error) ? "Error desconocido" : r.Error), "err");
            RenderArchivos();
        }
    }

    private async Task QuitarCapaAsync()
    {
        bool ok = C.Confirmar == null || await C.Confirmar("Quitar capa",
            "¿Quitar la capa de shapefile activa del lote?").ConfigureAwait(true);
        if (!ok) return;
        QxUi.SetMsg(_msg, "Quitando…", "");
        var r = await C.Client.QuitarShapeAsync().ConfigureAwait(true);
        if (r.Ok)
        {
            QxUi.SetMsg(_msg, "✓ Capa quitada", "ok");
            await RefrescarCamposAsync().ConfigureAwait(true);
            await RefrescarCapaAsync().ConfigureAwait(true);
        }
        else QxUi.SetMsg(_msg, "✕ " + (string.IsNullOrEmpty(r.Error) ? "no se pudo quitar" : r.Error), "err");
    }

    /// <summary>Refresca las columnas del shape: alimentan el selector
    /// mapa/fija de cada motor en la tab Siembra.</summary>
    private async Task RefrescarCamposAsync()
    {
        var campos = await C.Client.GetShapeFieldsAsync().ConfigureAwait(true);
        C.ShapeSource = campos.SourceToken;
        C.ShapeFields = campos.Fields;
    }

    // =======================================================================
    //  Biblioteca de prescripciones
    // =======================================================================

    private async Task RefrescarBibliotecaAsync()
    {
        _biblioteca.Children.Clear();
        _combosProp.Clear();
        var items = await C.Client.ListarPrescripcionesAsync().ConfigureAwait(true);
        if (items.Count == 0)
        {
            _biblioteca.Children.Add(QxUi.Sub("No hay prescripciones guardadas. Subí un shapefile arriba, "
                                            + "o llegan solas desde OrbitX."));
            return;
        }

        foreach (var it in items)
        {
            var fila = QxUi.Fila(10);

            var izq = new StackPanel { Spacing = 1, MinWidth = 220, MaxWidth = 320 };
            izq.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(it.Nombre) ? it.Id : it.Nombre!,
                Foreground = QxUi.Texto, FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            izq.Children.Add(QxUi.Mono2((it.FechaModUtc ?? "").Length >= 10 ? it.FechaModUtc!.Substring(0, 10) : ""));
            fila.Children.Add(izq);

            var props = it.PropiedadesCandidatas ?? new List<string>();
            if (props.Count > 1)
            {
                var cb = QxUi.Combo();
                cb.ItemsSource = props;
                cb.SelectedIndex = 0;
                ToolTip.SetTip(cb, PilotX.Cockpit.Bars.Traductor.T("Campo de dosis"));
                _combosProp[it.Id] = cb;
                fila.Children.Add(cb);
            }
            else
            {
                fila.Children.Add(QxUi.Mono2(props.Count == 1 ? props[0] : "DOSIS"));
            }

            if (it.Activo)
            {
                fila.Children.Add(QxUi.Chip("ACTIVA", QxUi.Ok));
                fila.Children.Add(QxUi.Boton("Quitar del mapa", () => _ = DesactivarAsync()));
            }
            else
            {
                string id = it.Id;
                fila.Children.Add(QxUi.Boton("Activar en el mapa", () => _ = ActivarAsync(id), primario: true));
            }

            _biblioteca.Children.Add(new Border
            {
                BorderBrush = QxUi.BordeSuave, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 6, 0, 6), Child = fila,
            });
        }
    }

    private async Task ActivarAsync(string id)
    {
        string prop = "";
        if (_combosProp.TryGetValue(id, out var cb) && cb.SelectedItem is string s) prop = s;
        var r = await C.Client.ActivarPrescripcionAsync(id, prop).ConfigureAwait(true);
        if (!r.Ok) { QxUi.SetMsg(_msg, "✕ No se pudo activar.", "err"); return; }
        QxUi.SetMsg(_msg, "✓ Prescripción activa en el mapa.", "ok");
        await RefrescarTodoDiferidoAsync().ConfigureAwait(true);
    }

    private async Task DesactivarAsync()
    {
        await C.Client.QuitarPrescripcionActivaAsync().ConfigureAwait(true);
        QxUi.SetMsg(_msg, "", "");
        await RefrescarTodoDiferidoAsync().ConfigureAwait(true);
    }

    /// <summary>El motor recarga y reproyecta la capa en su próximo ciclo: un
    /// refresh inmediato y otro diferido a 2,5 s para ver la geometría ya
    /// proyectada.</summary>
    private async Task RefrescarTodoDiferidoAsync()
    {
        await RefrescarBibliotecaAsync().ConfigureAwait(true);
        await RefrescarCapaAsync().ConfigureAwait(true);
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await RefrescarBibliotecaAsync().ConfigureAwait(true);
                await RefrescarCapaAsync().ConfigureAwait(true);
            });
        });
    }

    // =======================================================================
    //  Capa activa + vista previa
    // =======================================================================

    private async Task RefrescarCapaAsync()
    {
        _capaActiva.Children.Clear();
        _zonaEditando = null;

        var campos = await C.Client.GetShapeFieldsAsync().ConfigureAwait(true);
        C.ShapeSource = campos.SourceToken;
        C.ShapeFields = campos.Fields;

        if (!campos.Ok || string.IsNullOrEmpty(campos.SourceToken))
        {
            _capaActiva.Children.Add(QxUi.Sub("No hay shapefile activo en este lote."));
            return;
        }

        // Ficha: archivo + columnas DBF
        var ficha = QxUi.GrillaKv();
        QxUi.AgregarKv(ficha, 0, 0, PilotX.Cockpit.Bars.Traductor.T("Archivo"), campos.SourceToken);
        _capaActiva.Children.Add(ficha);

        var chips = new WrapPanel();
        if (campos.Fields.Count == 0) chips.Children.Add(QxUi.Sub("— sin columnas DBF —"));
        else foreach (var f in campos.Fields) chips.Children.Add(QxUi.Chip(f.Name, QxUi.Ok));
        _capaActiva.Children.Add(chips);

        // La vista previa va aparte: si falla, la ficha de arriba igual queda.
        _capa = await C.Client.GetShapeAsync().ConfigureAwait(true);
        if (_capa == null || _capa.Polygons.Count == 0) return;

        // ¿La capa del piloto ES la prescripción activa? Solo entonces el
        // toque permite EDITAR: un .shp subido a mano queda en consulta.
        _prescActiva = null;
        if ((_capa.SourceToken ?? "").IndexOf("(OrbitX)", StringComparison.Ordinal) >= 0)
            _prescActiva = await C.Client.GetPrescActivaAsync().ConfigureAwait(true);

        _preview.SetCapa(_capa);
        QxUi.Soltar(_preview);
        _capaActiva.Children.Add(new Border
        {
            Background = QxUi.BgSuave, BorderBrush = QxUi.BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = _preview,
        });

        // Pie: zonas, campo, rango, tamaño y el chip de consulta.
        var pie = QxUi.Fila(12);
        pie.Children.Add(QxUi.Sub(_capa.Polygons.Count
            + (_capa.Polygons.Count == 1 ? PilotX.Cockpit.Bars.Traductor.T(" zona")
                                         : PilotX.Cockpit.Bars.Traductor.T(" zonas"))));
        if (!string.IsNullOrEmpty(_capa.StyleField))
            pie.Children.Add(QxUi.Sub(PilotX.Cockpit.Bars.Traductor.T("campo ") + _capa.StyleField));
        if (_capa.StyleMin != null && _capa.StyleMax != null && _capa.StyleMin != _capa.StyleMax)
            pie.Children.Add(QxUi.Sub(PilotX.Cockpit.Bars.Traductor.T("dosis ")
                + _capa.StyleMin.Value.ToString("0.##", CultureInfo.InvariantCulture) + " – "
                + _capa.StyleMax.Value.ToString("0.##", CultureInfo.InvariantCulture)));
        pie.Children.Add(QxUi.Mono2(Math.Round(_preview.AnchoM) + " × " + Math.Round(_preview.AltoM) + " m"));
        _capaActiva.Children.Add(pie);

        _tapOut.Children.Clear();
        QxUi.Soltar(_tapOut);
        _tapOut.Children.Add(QxUi.Chip(_prescActiva != null
            ? PilotX.Cockpit.Bars.Traductor.T("Tocá una zona para editar su dosis")
            : PilotX.Cockpit.Bars.Traductor.T("Tocá una zona para ver su dosis")));
        _capaActiva.Children.Add(_tapOut);
    }

    private void OnZonaTocada(QxShapeZona? z)
    {
        _tapOut.Children.Clear();
        string campo = string.IsNullOrEmpty(_capa?.StyleField) ? "DOSIS" : _capa!.StyleField;

        if (z != null && _prescActiva != null && z.Fi >= 0)
        {
            _zonaEditando = z;
            _valorEditando = z.V ?? 0;
            RenderEditorZona(campo);
            return;
        }
        _zonaEditando = null;
        if (z != null && z.V != null)
            _tapOut.Children.Add(QxUi.Chip(campo + ": " + z.V.Value.ToString("0.##", CultureInfo.InvariantCulture), QxUi.Ok));
        else if (z != null)
            _tapOut.Children.Add(QxUi.Chip(PilotX.Cockpit.Bars.Traductor.T("Zona sin valor de dosis")));
        else
            _tapOut.Children.Add(QxUi.Chip(PilotX.Cockpit.Bars.Traductor.T("Fuera de las zonas")));
    }

    /// <summary>Paso de edición según magnitud: sem/m van en decimales,
    /// kg/ha en enteros.</summary>
    private static double PasoEdicion(double v) => v < 10 ? 0.5 : v < 50 ? 1 : 5;

    private void RenderEditorZona(string campo)
    {
        _tapOut.Children.Clear();
        _tapOut.Children.Add(new TextBlock
        {
            Text = campo, Foreground = QxUi.Texto, FontSize = 13, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _tapOut.Children.Add(QxUi.Boton("−", () =>
        {
            _valorEditando = Math.Max(0, Math.Round((_valorEditando - PasoEdicion(_valorEditando)) * 10) / 10);
            RenderEditorZona(campo);
        }));
        _tapOut.Children.Add(new TextBlock
        {
            Text = _valorEditando.ToString("0.##", CultureInfo.InvariantCulture),
            Foreground = QxUi.Texto, FontSize = 15, FontWeight = FontWeight.Bold, FontFamily = QxUi.Mono,
            MinWidth = 54, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _tapOut.Children.Add(QxUi.Boton("+", () =>
        {
            _valorEditando = Math.Round((_valorEditando + PasoEdicion(_valorEditando)) * 10) / 10;
            RenderEditorZona(campo);
        }));
        _tapOut.Children.Add(QxUi.Boton("Aplicar", () => _ = AplicarDosisZonaAsync(), primario: true));
    }

    private async Task AplicarDosisZonaAsync()
    {
        if (_zonaEditando == null || _prescActiva == null) return;
        int fi = _zonaEditando.Fi;   // feature del ARCHIVO, no el índice de dibujo
        var r = await C.Client.SetDosisZonaAsync(_prescActiva.Id, fi, _valorEditando).ConfigureAwait(true);
        if (!r.Ok) { QxUi.SetMsg(_msg, "✕ No se pudo guardar la dosis.", "err"); return; }
        QxUi.SetMsg(_msg, PilotX.Cockpit.Bars.Traductor.T("Zona ") + (fi + 1) + " → "
                        + _valorEditando.ToString("0.##", CultureInfo.InvariantCulture)
                        + PilotX.Cockpit.Bars.Traductor.T(". El mapa se actualiza solo."), "ok");
        _zonaEditando = null;
        await RefrescarTodoDiferidoAsync().ConfigureAwait(true);
    }
}
