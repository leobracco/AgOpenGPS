// ============================================================================
// PlanimetriaTab.cs — pestaña "GPS / IMU › Planimetría" del ConfigPanel nativo.
//
// Planimetría fase 3 en la cabina, con los alturas que graba la fase 1
// (pestaña Elevación): mapa de alturas del lote abierto sobre el mapa, guía
// por curva de nivel y ambientes por altura → prescripción.
//
// TODO APAGADO de fábrica: el interruptor general vive en el motor
// (planimetria.json). Apagado, la pestaña muestra solo el interruptor y el
// motor no lee ni calcula nada. Prendido, nada cambia en el guiado ni en la
// dosis hasta que el operario toca "Crear guía" o "Generar prescripción".
//
// Guardado AL TOQUE (como Elevación): son interruptores y acciones, no un
// formulario. Las dosis se escriben y se guardan con su botón.
//
// Sin poll propio: el shell llama a Live() cada 3 s y ahí se pide
// GET /api/planimetria. Se reconstruye solo si cambió la ESTRUCTURA (prendida,
// estado, modo); si no, se refrescan los textos vivos y no se pierde el foco
// de una dosis a medio escribir.
// ============================================================================

using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class PlanimetriaTab : ConfigTab
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // MaxWidth OBLIGATORIO (mismo criterio que AntenaTab): el TabHost cuelga de
    // un ScrollViewer con scroll horizontal, así que sin tope las notas largas
    // no se parten en renglones y la carta se estira fuera de la pantalla.
    private const double AnchoCarta = 760;

    private PlanimetriaClient? _cli;
    private PlaniEstado? _est;
    private bool _consultando;
    private bool _ocupado;
    private string _firma = "";
    private double? _cota;               // cota elegida para la guía (se resalta en el mapa)

    private TextBlock? _txtEstado, _txtCotaTractor, _txtCota, _txtResultadoGuia, _txtResultadoPresc;
    private Border? _punto;
    private TextBox? _dosisBajo, _dosisMedia, _dosisLoma;

    public PlanimetriaTab(CfgCtx c) : base(c) { }

    private static string T(string s) => PilotX.Cockpit.Bars.Traductor.T(s);

    private static string M(double? v, string fmt = "0.00")
        => v.HasValue ? v.Value.ToString(fmt, Inv).Replace('.', ',') + " m" : "—";

    private static string N(double? v, string fmt = "0.##")
        => v.HasValue ? v.Value.ToString(fmt, Inv).Replace('.', ',') : "—";

    public override async Task AlEntrarAsync()
    {
        await RefrescarAsync().ConfigureAwait(true);
    }

    public override void Live() => _ = RefrescarAsync();

    private PlanimetriaClient? Cli()
    {
        if (_cli == null && C.Client != null) _cli = new PlanimetriaClient(C.Client.BaseUrl);
        return _cli;
    }

    private async Task RefrescarAsync()
    {
        var cli = Cli();
        if (cli == null || _consultando) return;
        _consultando = true;
        try
        {
            var e = await cli.EstadoAsync().ConfigureAwait(true);
            Aplicar(e);
        }
        catch { }
        finally { _consultando = false; }
    }

    private void Aplicar(PlaniEstado? e)
    {
        _est = e;
        if (e != null && _cota == null) _cota = e.CotaGuiaM ?? e.CotaTractorM;
        string firma = e == null ? "null" : string.Join("|", e.Ok, e.Habilitada, e.Estado, e.CapaVisible, e.ModoCapa,
            e.Ambientes?.Modo, e.Lote, e.ZMinM.HasValue);
        if (firma != _firma) { _firma = firma; Rebuild(); }
        else PintarVivo();
    }

    // =========================================================================
    //  Armado
    // =========================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _txtEstado = _txtCotaTractor = _txtCota = _txtResultadoGuia = _txtResultadoPresc = null;
        _dosisBajo = _dosisMedia = _dosisLoma = null;
        var e = _est;
        if (e == null)
        {
            Children.Add(CfgUi.Carta(CfgUi.Nota(C.SinDatos ? "PilotX no responde — todavía no llegaron los datos." : "Consultando…")));
            return;
        }
        if (!e.Ok && e.Error == "service-unavailable")
        {
            Children.Add(CfgUi.ChipError("Esta versión del motor no trae planimetría", "AGP-SYS-404"));
            return;
        }

        // ---- interruptor general --------------------------------------------
        var col = new StackPanel { Spacing = 10, MaxWidth = AnchoCarta, HorizontalAlignment = HorizontalAlignment.Left };
        col.Children.Add(CfgUi.Titulo("Planimetría en la cabina"));
        col.Children.Add(CfgUi.Nota(
            "Con las alturas que registra PilotX (pestaña Elevación, solo RTK fijo) arma el mapa de alturas del lote "
            + "abierto: curvas de nivel y bajos sobre el mapa, guía por curva de nivel y ambientes loma / media loma / bajo "
            + "para una prescripción. Se calcula en esta PC, sin internet."));
        col.Children.Add(Interruptor(T("Planimetría"), e.Habilitada, () => ConfigurarAsync(new { habilitada = !e.Habilitada })));
        if (!e.Habilitada)
        {
            col.Children.Add(CfgUi.Nota("Apagada: no se calcula nada y no cambia nada en el mapa, el guiado ni la dosis."));
            Children.Add(CfgUi.Carta(col));
            return;
        }

        var filaEstado = CfgUi.Fila(8);
        _punto = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
        _txtEstado = new TextBlock { Foreground = CfgUi.Texto, FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        filaEstado.Children.Add(_punto);
        filaEstado.Children.Add(_txtEstado);
        col.Children.Add(filaEstado);

        bool hayMapa = e.ZMinM.HasValue;
        if (hayMapa)
        {
            var kv = CfgUi.GrillaKv(2);
            CfgUi.AgregarKv(kv, 0, 0, "Desnivel", M(e.DesnivelM));
            CfgUi.AgregarKv(kv, 0, 2, "Cotas", M(e.ZMinM) + " – " + M(e.ZMaxM));
            CfgUi.AgregarKv(kv, 1, 0, "Pendiente media", N(e.PendienteMediaPct, "0.0") + " %");
            CfgUi.AgregarKv(kv, 1, 2, "Pendiente máx.", N(e.PendienteMaxPct, "0.0") + " %");
            CfgUi.AgregarKv(kv, 2, 0, "Bajos", e.BajosCantidad + " · " + N(e.BajosAreaHa, "0.00") + " ha");
            CfgUi.AgregarKv(kv, 2, 2, "Superficie", N(e.AreaHa, "0.0") + " ha");
            CfgUi.AgregarKv(kv, 3, 0, "Puntos RTK", e.PuntosRtk.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = ".", NumberGroupSizes = new[] { 3 } }));
            CfgUi.AgregarKv(kv, 3, 2, "Nivelación", e.NivelacionAplicada
                ? N(e.SesgoAntesCm, "0.0") + " → " + N(e.SesgoDespuesCm, "0.0") + " cm" : T("no hizo falta"));
            col.Children.Add(kv);
        }

        var acciones = CfgUi.Fila(10);
        acciones.Children.Add(CfgUi.Boton(hayMapa ? "Recalcular" : "Calcular", () => _ = AccionEstadoAsync(c => c.CalcularAsync())));
        col.Children.Add(acciones);

        if (hayMapa)
        {
            col.Children.Add(Interruptor(T("Mostrar en el mapa"), e.CapaVisible, () => ConfigurarAsync(new { capa_visible = !e.CapaVisible })));
            var modos = CfgUi.Fila(8);
            modos.Children.Add(Segmento(T("Alturas"), e.ModoCapa != "ambientes", () => ConfigurarAsync(new { modo_capa = "alturas" })));
            modos.Children.Add(Segmento(T("Ambientes"), e.ModoCapa == "ambientes", () => ConfigurarAsync(new { modo_capa = "ambientes" })));
            col.Children.Add(CfgUi.Campo("Qué se pinta", modos));
        }
        col.Children.Add(CfgUi.Nota(
            "El mapa se calcula solo la primera vez que abrís el lote; después, con Recalcular (por ejemplo al terminar "
            + "el día, con más pasadas registradas)."));
        Children.Add(CfgUi.Carta(col));

        if (!hayMapa) { PintarVivo(); return; }

        Children.Add(CfgUi.Carta(CartaGuia(e)));
        Children.Add(CfgUi.Carta(CartaAmbientes(e)));
        PintarVivo();
    }

    private Control CartaGuia(PlaniEstado e)
    {
        var col = new StackPanel { Spacing = 10, MaxWidth = AnchoCarta, HorizontalAlignment = HorizontalAlignment.Left };
        col.Children.Add(CfgUi.Titulo("Guía por curva de nivel"));
        col.Children.Add(CfgUi.Nota(
            "Para sembrar en contorno: elegí la cota y PilotX arma una guía curva que sigue esa curva de nivel (la que "
            + "queda más cerca del tractor). La curva elegida se resalta en magenta en el mapa."));
        _txtCotaTractor = new TextBlock { Foreground = CfgUi.TextoMuted, FontSize = 13 };
        col.Children.Add(_txtCotaTractor);

        double paso = e.IntervaloM is > 0 ? e.IntervaloM.Value : 0.1;
        var fila = CfgUi.Fila(8);
        fila.Children.Add(CfgUi.Boton("−", () => MoverCota(-paso)));
        _txtCota = new TextBlock
        {
            MinWidth = 110, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = CfgUi.Texto,
            FontFamily = CfgUi.Mono, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        fila.Children.Add(_txtCota);
        fila.Children.Add(CfgUi.Boton("+", () => MoverCota(paso)));
        fila.Children.Add(CfgUi.Boton("Cota del tractor", () =>
        {
            if (_est?.CotaTractorM is double ct) { _cota = ct; _ = ConfigurarAsync(new { cota_guia_m = ct }); PintarVivo(); }
            else C.Aviso?.Invoke(T("Sin GPS o con el tractor fuera del mapa"));
        }));
        col.Children.Add(CfgUi.Campo("Cota de la curva", fila));

        var crear = CfgUi.Fila(10);
        crear.Children.Add(CfgUi.Boton("Crear guía sobre esta curva", () => _ = CrearGuiaAsync(), primario: true));
        col.Children.Add(crear);
        _txtResultadoGuia = new TextBlock { Foreground = CfgUi.TextoMuted, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        col.Children.Add(_txtResultadoGuia);
        col.Children.Add(CfgUi.Nota(
            "La guía queda guardada y activa como cualquier AB + Curva (se elige o borra en Barra de la pasada › Guías › Guías guardadas). "
            + "El piloto la sigue solo si lo enganchás. Con \"Guía por última pasada\" prendida no se puede crear."));
        return col;
    }

    private Control CartaAmbientes(PlaniEstado e)
    {
        var a = e.Ambientes ?? new PlaniAmbientes();
        var col = new StackPanel { Spacing = 10, MaxWidth = AnchoCarta, HorizontalAlignment = HorizontalAlignment.Left };
        col.Children.Add(CfgUi.Titulo("Ambientes por altura → prescripción"));
        col.Children.Add(CfgUi.Nota(
            "Divide el lote en loma, media loma y bajo según la altura, y arma una prescripción con una dosis por "
            + "ambiente. Usa las prescripciones de siempre: queda en Configuración › Campo › Prescripciones y en QuantiX › Configurar › Shape, y dosifica con QuantiX."));

        var criterio = CfgUi.Fila(8);
        criterio.Children.Add(Segmento(T("Por porcentaje"), a.Modo != "desnivel", () => ConfigurarAsync(new { modo_ambientes = "percentil" })));
        criterio.Children.Add(Segmento(T("Por desnivel"), a.Modo == "desnivel", () => ConfigurarAsync(new { modo_ambientes = "desnivel" })));
        col.Children.Add(CfgUi.Campo("Criterio", criterio));

        if (a.Modo == "desnivel")
        {
            col.Children.Add(CfgUi.Nota("Respecto de la altura mediana del lote."));
            col.Children.Add(Ajuste("Bajo: más de", M(a.DBajoM) + " " + T("abajo"),
                () => ConfigurarAsync(new { d_bajo_m = Math.Round(a.DBajoM - 0.05, 2) }),
                () => ConfigurarAsync(new { d_bajo_m = Math.Round(a.DBajoM + 0.05, 2) })));
            col.Children.Add(Ajuste("Loma: más de", M(a.DLomaM) + " " + T("arriba"),
                () => ConfigurarAsync(new { d_loma_m = Math.Round(a.DLomaM - 0.05, 2) }),
                () => ConfigurarAsync(new { d_loma_m = Math.Round(a.DLomaM + 0.05, 2) })));
        }
        else
        {
            col.Children.Add(Ajuste("Bajo: lo más bajo", N(a.PBajo, "0") + " %",
                () => ConfigurarAsync(new { p_bajo = a.PBajo - 5 }),
                () => ConfigurarAsync(new { p_bajo = a.PBajo + 5 })));
            col.Children.Add(Ajuste("Loma: lo más alto", N(100 - a.PLoma, "0") + " %",
                () => ConfigurarAsync(new { p_loma = a.PLoma + 5 }),
                () => ConfigurarAsync(new { p_loma = a.PLoma - 5 })));
        }

        var kv = CfgUi.GrillaKv(1);
        CfgUi.AgregarKv(kv, 0, 0, "Loma", "> " + M(a.CotaLomaM) + " · " + N(a.AreaLomaHa, "0.0") + " ha");
        CfgUi.AgregarKv(kv, 1, 0, "Media loma", N(a.AreaMediaHa, "0.0") + " ha");
        CfgUi.AgregarKv(kv, 2, 0, "Bajo", "< " + M(a.CotaBajoM) + " · " + N(a.AreaBajoHa, "0.0") + " ha");
        col.Children.Add(kv);

        var d = e.Dosis ?? new PlaniDosis();
        var dosis = CfgUi.Grilla();
        _dosisLoma = CfgUi.Entrada(C, N(d.Loma, "0.###"), true, T("Dosis en loma"));
        _dosisMedia = CfgUi.Entrada(C, N(d.Media, "0.###"), true, T("Dosis en media loma"));
        _dosisBajo = CfgUi.Entrada(C, N(d.Bajo, "0.###"), true, T("Dosis en bajo"));
        var cLoma = CfgUi.Campo("Dosis loma", _dosisLoma);
        var cMedia = CfgUi.Campo("Dosis media loma", _dosisMedia);
        var cBajo = CfgUi.Campo("Dosis bajo", _dosisBajo);
        cLoma.Margin = cMedia.Margin = cBajo.Margin = new Thickness(0, 0, 12, 8);
        dosis.Children.Add(cLoma);
        dosis.Children.Add(cMedia);
        dosis.Children.Add(cBajo);
        col.Children.Add(dosis);
        col.Children.Add(CfgUi.Nota("En la unidad del producto de QuantiX (kg/ha, sem/ha…). Dosis 0 = ese ambiente usa la dosis fija del producto."));

        var botones = CfgUi.Fila(10);
        botones.Children.Add(CfgUi.Boton("Guardar dosis", () => _ = GuardarDosisAsync(false)));
        botones.Children.Add(CfgUi.Boton("Generar prescripción y activarla", () => _ = GuardarDosisAsync(true), primario: true));
        col.Children.Add(botones);
        _txtResultadoPresc = new TextBlock { Foreground = CfgUi.TextoMuted, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        col.Children.Add(_txtResultadoPresc);
        col.Children.Add(CfgUi.Nota("FlowX todavía no lee prescripciones: con FlowX sigue la dosis fija."));
        return col;
    }

    // =========================================================================
    //  Vivo
    // =========================================================================

    private void PintarVivo()
    {
        var e = _est;
        if (e == null) return;
        if (_txtEstado != null && _punto != null)
        {
            (string texto, IBrush color) = e.Estado switch
            {
                "listo" => (T("Mapa listo") + (e.Lote != null ? " · " + e.Lote : ""), CfgUi.Ok),
                "calculando" => (T("Calculando el mapa de alturas…"), CfgUi.Warn),
                "sin_datos" => (string.IsNullOrWhiteSpace(e.Motivo) ? T("Este lote no tiene alturas registradas") : T(e.Motivo), CfgUi.Warn),
                "sin_lote" => (T("Abrí un lote para ver su mapa de alturas"), CfgUi.Dim),
                "error" => (T("No se pudo calcular") + (string.IsNullOrWhiteSpace(e.Motivo) ? "" : ": " + e.Motivo), CfgUi.Err),
                _ => ("—", CfgUi.Dim),
            };
            _txtEstado.Text = texto;
            _punto.Background = color;
        }
        if (_txtCotaTractor != null)
            _txtCotaTractor.Text = T("Cota bajo el tractor") + ": " + (e.CotaTractorM.HasValue ? M(e.CotaTractorM) : T("sin GPS o fuera del mapa"));
        if (_txtCota != null) _txtCota.Text = _cota.HasValue ? M(_cota) : "—";
        if (_txtResultadoGuia != null && string.IsNullOrEmpty(_txtResultadoGuia.Text) && !string.IsNullOrEmpty(e.UltimaGuia))
            _txtResultadoGuia.Text = T("Última guía creada") + ": " + e.UltimaGuia;
        if (_txtResultadoPresc != null && string.IsNullOrEmpty(_txtResultadoPresc.Text) && !string.IsNullOrEmpty(e.UltimaPrescripcion))
            _txtResultadoPresc.Text = T("Última prescripción") + ": " + e.UltimaPrescripcion;
    }

    private void MoverCota(double delta)
    {
        double basec = _cota ?? _est?.CotaTractorM ?? _est?.ZMinM ?? 0;
        double paso = Math.Abs(delta);
        // A múltiplos del intervalo: la curva coincide con una de las dibujadas.
        double nueva = Math.Round((basec + delta) / paso) * paso;
        if (_est?.ZMinM is double lo && nueva < lo) nueva = Math.Ceiling(lo / paso) * paso;
        if (_est?.ZMaxM is double hi && nueva > hi) nueva = Math.Floor(hi / paso) * paso;
        _cota = Math.Round(nueva, 3);
        PintarVivo();
        _ = ConfigurarAsync(new { cota_guia_m = _cota });
    }

    // =========================================================================
    //  Acciones
    // =========================================================================

    private async Task ConfigurarAsync(object cambios)
    {
        var cli = Cli();
        if (cli == null || _ocupado) return;
        _ocupado = true;
        try
        {
            var e = await cli.ConfigurarAsync(cambios).ConfigureAwait(true);
            if (e == null) { C.Aviso?.Invoke(T("Sin conexión con PilotX")); return; }
            Aplicar(e);
        }
        finally { _ocupado = false; }
    }

    private async Task AccionEstadoAsync(Func<PlanimetriaClient, Task<PlaniEstado?>> accion)
    {
        var cli = Cli();
        if (cli == null || _ocupado) return;
        _ocupado = true;
        try
        {
            var e = await accion(cli).ConfigureAwait(true);
            if (e == null) { C.Aviso?.Invoke(T("Sin conexión con PilotX")); return; }
            Aplicar(e);
        }
        finally { _ocupado = false; }
    }

    private async Task CrearGuiaAsync()
    {
        var cli = Cli();
        if (cli == null || _ocupado) return;
        _ocupado = true;
        try
        {
            C.Estado?.Invoke(T("Creando la guía…"), "");
            var r = await cli.CrearGuiaAsync(_cota).ConfigureAwait(true);
            string msg = r == null ? T("Sin conexión con PilotX") : T(r.Mensaje ?? (r.Ok ? "Guía creada" : r.Error ?? "Error"));
            if (_txtResultadoGuia != null) _txtResultadoGuia.Text = msg;
            C.Estado?.Invoke(msg, r != null && r.Ok ? "ok" : "err");
            if (r != null && r.Ok && r.CotaM.HasValue) _cota = r.CotaM;
        }
        finally { _ocupado = false; }
        await RefrescarAsync().ConfigureAwait(true);
    }

    private async Task GuardarDosisAsync(bool generar)
    {
        var cli = Cli();
        if (cli == null || _ocupado) return;
        var d = _est?.Dosis ?? new PlaniDosis();
        double bajo = Math.Max(0, CfgUi.LeerDouble(_dosisBajo, d.Bajo));
        double media = Math.Max(0, CfgUi.LeerDouble(_dosisMedia, d.Media));
        double loma = Math.Max(0, CfgUi.LeerDouble(_dosisLoma, d.Loma));
        _ocupado = true;
        try
        {
            var e = await cli.ConfigurarAsync(new { dosis_bajo = bajo, dosis_media = media, dosis_loma = loma }).ConfigureAwait(true);
            if (e == null) { C.Aviso?.Invoke(T("Sin conexión con PilotX")); return; }
            _est = e;
            if (!generar)
            {
                C.Estado?.Invoke(T("Dosis guardadas ✔"), "ok");
                return;
            }
            C.Estado?.Invoke(T("Generando la prescripción…"), "");
            var r = await cli.CrearPrescripcionAsync().ConfigureAwait(true);
            string msg = r == null ? T("Sin conexión con PilotX") : T(r.Mensaje ?? (r.Ok ? "Prescripción activa" : r.Error ?? "Error"));
            if (r != null && r.Ok) msg += " (" + r.Poligonos + " " + T("zonas") + ")";
            if (_txtResultadoPresc != null) _txtResultadoPresc.Text = msg;
            C.Estado?.Invoke(msg, r != null && r.Ok ? "ok" : "err");
        }
        finally { _ocupado = false; }
    }

    // =========================================================================
    //  Controles
    // =========================================================================

    private static Border Interruptor(string texto, bool activo, Func<Task> alTocar)
    {
        var txt = new TextBlock
        {
            Text = texto + "  ·  " + T(activo ? "Activado" : "Apagado"),
            Foreground = CfgUi.Texto, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var b = new Border
        {
            MinHeight = 52, HorizontalAlignment = HorizontalAlignment.Left,
            Background = activo ? CfgUi.BgFilaSel : CfgUi.BgFila,
            BorderBrush = activo ? CfgUi.Verde : CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 6, 14, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = txt,
        };
        b.Tapped += (_, __) => _ = alTocar();
        return b;
    }

    private static Button Segmento(string texto, bool elegido, Func<Task> alTocar)
    {
        var b = CfgUi.Boton(texto, null, primario: elegido);
        b.Click += (_, __) => { if (!elegido) _ = alTocar(); };
        return b;
    }

    private static Control Ajuste(string etiqueta, string valor, Func<Task> menos, Func<Task> mas)
    {
        var fila = CfgUi.Fila(8);
        fila.Children.Add(new TextBlock
        {
            Text = T(etiqueta), Foreground = CfgUi.TextoMuted, FontSize = 13, FontWeight = FontWeight.Bold,
            MinWidth = 150, VerticalAlignment = VerticalAlignment.Center,
        });
        fila.Children.Add(CfgUi.Boton("−", () => _ = menos()));
        fila.Children.Add(new TextBlock
        {
            Text = valor, Foreground = CfgUi.Texto, FontSize = 15, FontWeight = FontWeight.Bold, FontFamily = CfgUi.Mono,
            MinWidth = 120, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        fila.Children.Add(CfgUi.Boton("+", () => _ = mas()));
        return fila;
    }
}
