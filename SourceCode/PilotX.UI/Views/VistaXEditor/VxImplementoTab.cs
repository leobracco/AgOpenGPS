// ============================================================================
// VxImplementoTab.cs — pantalla "Implemento" del editor nativo de VistaX.
//
// QUÉ QUEDÓ NATIVO: loadImplemento/paintImplemento/renderSensorRow/
// readImplementoFromForm/saveImplemento de vistax.js — el banner de geometría
// centralizada (solo lectura del implemento central), el objetivo de siembra,
// los parámetros de VistaX y la tabla de mapeo de sensores.
// QUÉ SIGUE EN HTML: vistax.js queda intacto para la PWA del celular.
//
// REGLAS QUE SE RESPETAN TAL CUAL:
//  · La geometría (ancho, surcos, distancia, trenes, secciones) NO se edita
//    acá: la manda el implemento central y el PUT la ignora. Se muestra como
//    referencia y el botón lleva a Configuración.
//  · "N° de torres" es de solo lectura: el backend lo re-deriva del central y
//    el PUT nunca lo guardó (en la página HTML era un input que no persistía).
//    Mostrarlo como dato evita prometer una edición que no ocurre.
//  · Los UIDs NO se tipean: solo se eligen de los nodos vistos por MQTT
//    (regla nodos-solo-auto — un UID tipeado con un typo es un sensor muerto).
//  · `pin` y `nombre` no se editan (el pin se deriva del cable, el nombre es
//    redundante con UID+bajada). A diferencia del JS, que rearmaba la fila de
//    cero y por eso PERDÍA `muted` y `objetivo` de cada sensor, acá se edita la
//    fila cargada y esos campos sobreviven al guardado.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.VistaXEditor;

public sealed class VxImplementoTab : VxTab
{
    private ComboBox?   _cboFuente;
    private TextBox?    _txtObjetivo;
    private TextBox?    _txtTolerancia;
    private TextBox?    _txtSurcosTorre;
    private ComboBox?   _cboVista;
    private StackPanel? _filasHost;
    private TextBlock?  _estado;
    private Border?     _chipError;
    private TextBlock?  _chipErrorTexto;

    private readonly List<Fila> _filas = new();
    private string _firmaNodos = "";

    // ---- anchos de la tabla (una sola fuente para header y filas) ----------
    private const double AnchoUid    = 190;
    private const double AnchoNum    = 80;
    private const double AnchoTren   = 150;
    private const double AnchoTipo   = 180;
    private const double AnchoActivo = 62;
    private const double AnchoBorrar = 44;

    private sealed class OpcionUid
    {
        public string Uid = "";
        public string Texto = "";
        public override string ToString() => Texto;
    }

    private sealed class OpcionTren
    {
        public int Id;
        public string Texto = "";
        public override string ToString() => Texto;
    }

    private sealed class OpcionTipo
    {
        public string Id = "";
        public string Texto = "";
        public override string ToString() => Texto;
    }

    /// <summary>Una fila del mapeo. `Origen` es el sensor tal cual vino del
    /// backend: se edita en su lugar para no perder los campos que esta
    /// pantalla no muestra (muted, objetivo, pin…).</summary>
    private sealed class Fila
    {
        public VxSensorCfg Origen = new VxSensorCfg();
        public Control     Raiz   = null!;
        public ComboBox    Uid    = null!;
        public TextBox     Cable  = null!;
        public TextBox     Bajada = null!;
        public ComboBox    Tren   = null!;
        public ComboBox    Tipo   = null!;
        public CheckBox    Activo = null!;
    }

    public VxImplementoTab(VxCtx c) : base(c) { }

    public override async Task AlEntrarAsync()
    {
        // Lazy-load igual que el HTML: el GET recién al entrar a la pantalla.
        if (C.Central == null || C.Imp == null || C.Tipos.Count == 0)
            await CargarAsync().ConfigureAwait(true);
        else
            Rebuild();
    }

    private async Task CargarAsync()
    {
        C.Central = await C.Client.GetImplementoCentralAsync().ConfigureAwait(true);
        if (C.Tipos.Count == 0)
            C.Tipos = await C.Client.GetSensorTiposAsync().ConfigureAwait(true);
        var imp = await C.Client.GetImplementoAsync().ConfigureAwait(true);
        if (imp != null) C.Imp = imp;
        Rebuild();
        if (imp == null) VxUi.SetEstado(_estado, "No se pudo cargar el implemento", "err");
        else VxUi.SetEstado(_estado, "Cargado"
                            + (string.IsNullOrEmpty(imp.Path) ? "" : " · " + imp.Path), "");
    }

    // =======================================================================
    //  Armado
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _filas.Clear();
        _firmaNodos = "";

        Children.Add(BannerGeometria());
        Children.Add(BloqueObjetivo());
        Children.Add(BloqueParametros());
        Children.Add(BloqueMapeo());
        Children.Add(Pie());

        RefrescarOpcionesUid(forzar: true);
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private Control BannerGeometria()
    {
        var c = C.Central;
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(VxUi.Titulo("Geometría centralizada"));
        sp.Children.Add(VxUi.Sub(
            "Ancho, número de surcos, trenes y secciones se editan una sola vez en el "
            + "implemento de PilotX. VistaX, QuantiX y SectionX leen de ahí. Acá solo "
            + "aparecen como referencia."));

        var kpis = VxUi.Grilla();
        kpis.Children.Add(VxUi.Kpi("Ancho total",
            (c?.AnchoTotalM ?? 0) > 0 ? VxUi.Num(c!.AnchoTotalM, 2) : "–", "m"));
        int nSurcos = c?.NumeroSurcos ?? 0;
        if (nSurcos <= 0) nSurcos = c?.Surcos?.Count ?? 0;
        kpis.Children.Add(VxUi.Kpi("Número de surcos", nSurcos > 0 ? nSurcos.ToString() : "–"));
        kpis.Children.Add(VxUi.Kpi("Distancia entre surcos",
            (c?.DistanciaEntreSurcosM ?? 0) > 0 ? VxUi.Num(c!.DistanciaEntreSurcosM, 3) : "–", "m"));
        int nTrenes = c?.Trenes?.Count ?? 0;
        kpis.Children.Add(VxUi.Kpi("Trenes", nTrenes > 0 ? nTrenes.ToString() : "–"));
        sp.Children.Add(kpis);

        sp.Children.Add(VxUi.Boton("Editar en Configuración", () => C.AbrirConfigCentral?.Invoke()));
        return VxUi.Banner(sp);
    }

    private Control BloqueObjetivo()
    {
        var setup = C.Imp?.Implemento?.Setup ?? new VxSetup();
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(VxUi.Titulo("Objetivo de siembra"));

        _cboFuente = VxUi.Combo(300);
        _cboFuente.ItemsSource = new List<string>
        {
            PilotX.Cockpit.Bars.Traductor.T("QuantiX — la dosis que manda al motor de cada surco"),
            PilotX.Cockpit.Bars.Traductor.T("Manual — el valor de acá abajo, siempre"),
        };
        _cboFuente.SelectedIndex = string.Equals(setup.ObjetivoFuente, "manual", StringComparison.Ordinal) ? 1 : 0;

        _txtObjetivo = VxUi.Entrada(C.Client, VxUi.Num(setup.DensidadObjetivo, 1), true,
                                    "Objetivo propio (sem/m)", 130);

        var campos = VxUi.Grilla();
        campos.Children.Add(VxUi.Campo("De dónde sale", _cboFuente, 300));
        campos.Children.Add(VxUi.Campo("Objetivo propio (sem/m)", _txtObjetivo, 130));
        sp.Children.Add(campos);

        sp.Children.Add(VxUi.Sub(
            "Con QuantiX, cada surco se compara contra la dosis que QuantiX le está mandando "
            + "a su motor (la fija, o la del mapa de prescripción si hay uno activo). Los surcos "
            + "que no tengan motor asignado usan el objetivo propio. Con Manual se compara todo "
            + "contra el objetivo propio, aunque haya QuantiX conectado."));
        return VxUi.Card(sp);
    }

    private Control BloqueParametros()
    {
        var setup = C.Imp?.Implemento?.Setup ?? new VxSetup();
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(VxUi.Titulo("Parámetros VistaX"));

        _txtTolerancia = VxUi.Entrada(C.Client, VxUi.Num(setup.ToleranciaDesvio, 0), true,
                                      "Tolerancia desvío (%)", 120);
        _txtSurcosTorre = VxUi.Entrada(C.Client, setup.SurcosPorTorre.ToString(), true,
                                       "Surcos por torre", 120);
        _cboVista = VxUi.Combo(220);
        _cboVista.ItemsSource = new List<string>
        {
            PilotX.Cockpit.Bars.Traductor.T("Surcos (uno por bajada)"),
            PilotX.Cockpit.Bars.Traductor.T("Torres (agrupado)"),
        };
        _cboVista.SelectedIndex = string.Equals(setup.VistaModoDefault, "torres", StringComparison.Ordinal) ? 1 : 0;

        var campos = VxUi.Grilla();
        campos.Children.Add(VxUi.Campo("Tolerancia desvío (%)", _txtTolerancia, 120));
        // N° de torres: dato del implemento central, no editable acá (el PUT de
        // VistaX nunca lo guardó — ver cabecera).
        campos.Children.Add(VxUi.Kpi("N° de torres", setup.Torres > 0 ? setup.Torres.ToString() : "0"));
        campos.Children.Add(VxUi.Campo("Surcos por torre", _txtSurcosTorre, 120));
        campos.Children.Add(VxUi.Campo("Vista por defecto", _cboVista, 220));
        sp.Children.Add(campos);

        sp.Children.Add(VxUi.Sub(
            "Las torres agrupan surcos físicamente próximos que comparten un dosificador. "
            + "En siembra extensiva (ej. 96 surcos / 8 torres = 12 surcos por torre), la vista "
            + "torres muestra 8 celdas — una por torre, promediando el estado de sus surcos. "
            + "0 en surcos por torre = automático (total / torres). El número de torres se "
            + "define en el implemento de PilotX."));
        sp.Children.Add(VxUi.Sub(
            "La densidad objetivo, el factor K y el límite del sensor se configuran desde "
            + "Insumos y desde la pestaña Insumo & calibración."));
        return VxUi.Card(sp);
    }

    private Control BloqueMapeo()
    {
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(VxUi.Titulo("Mapeo de sensores"));
        sp.Children.Add(Encabezado());

        _filasHost = new StackPanel { Spacing = 6 };
        sp.Children.Add(_filasHost);

        var sensores = C.Imp?.Implemento?.MapeoSensores;
        if (sensores != null)
            foreach (var s in sensores) AgregarFila(s);

        sp.Children.Add(VxUi.Boton("+ Agregar sensor", AgregarFilaNueva));
        return VxUi.Card(sp);
    }

    private Control Encabezado()
    {
        var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void Col(string t, double w) => fila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(t), Width = w,
            Foreground = VxUi.TextoDim, FontSize = 10, FontWeight = FontWeight.SemiBold,
        });
        Col("UID (nodo)", AnchoUid);
        Col("Cable", AnchoNum);
        Col("Bajada", AnchoNum);
        Col("Tren", AnchoTren);
        Col("Tipo", AnchoTipo);
        Col("Activo", AnchoActivo);
        Col("", AnchoBorrar);
        return fila;
    }

    private void AgregarFilaNueva()
    {
        // Pre-siembra el UID con el primer nodo online visto (evita el
        // "tengo un solo nodo arriba y aun así me pide elegirlo").
        var s = new VxSensorCfg { Uid = C.PrimerNodoUid(), Tipo = "semilla", IsActive = true };
        AgregarFila(s);
        RefrescarOpcionesUid(forzar: true);
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private void AgregarFila(VxSensorCfg s)
    {
        var host = _filasHost;
        if (host == null) return;
        var f = new Fila { Origen = s };

        f.Uid = VxUi.Combo(AnchoUid);
        f.Uid.Width = AnchoUid;

        f.Cable  = VxUi.Entrada(C.Client, s.Cable.ToString(),  true, "Cable",  AnchoNum);
        f.Bajada = VxUi.Entrada(C.Client, s.Bajada.ToString(), true, "Bajada", AnchoNum);

        f.Tren = VxUi.Combo(AnchoTren);
        f.Tren.Width = AnchoTren;
        var trenes = new List<OpcionTren>();
        int trenSel = 0;
        foreach (var t in C.TrenesCentral())
        {
            trenes.Add(new OpcionTren
            {
                Id = t.Id,
                Texto = string.IsNullOrEmpty(t.Nombre) ? "Tren " + t.Id : t.Nombre!,
            });
            if (t.Id == s.Tren) trenSel = trenes.Count - 1;
        }
        f.Tren.ItemsSource = trenes;
        f.Tren.SelectedIndex = trenSel;

        f.Tipo = VxUi.Combo(AnchoTipo);
        f.Tipo.Width = AnchoTipo;
        var tipos = new List<OpcionTipo>();
        int tipoSel = 0;
        foreach (var t in C.TiposConFallback())
        {
            tipos.Add(new OpcionTipo
            {
                Id = t.Id,
                Texto = string.IsNullOrEmpty(t.Etiqueta) ? t.Id : t.Etiqueta!,
            });
            if (string.Equals(t.Id, s.Tipo, StringComparison.Ordinal)) tipoSel = tipos.Count - 1;
        }
        f.Tipo.ItemsSource = tipos;
        f.Tipo.SelectedIndex = tipoSel;

        f.Activo = new CheckBox
        {
            IsChecked = s.IsActive, Width = AnchoActivo, MinHeight = 42,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var borrar = VxUi.Boton("×", null, peligro: true);
        borrar.Width = AnchoBorrar;
        borrar.MinWidth = AnchoBorrar;
        borrar.FontSize = 16;
        borrar.Click += (_, __) =>
        {
            // Solo saca la fila; persiste recién al Guardar (igual que el HTML).
            _filas.Remove(f);
            host.Children.Remove(f.Raiz);
        };

        var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        fila.Children.Add(f.Uid);
        fila.Children.Add(f.Cable);
        fila.Children.Add(f.Bajada);
        fila.Children.Add(f.Tren);
        fila.Children.Add(f.Tipo);
        fila.Children.Add(f.Activo);
        fila.Children.Add(borrar);
        f.Raiz = fila;

        _filas.Add(f);
        host.Children.Add(fila);
    }

    private Control Pie()
    {
        _estado = VxUi.Estado();

        _chipErrorTexto = new TextBlock
        {
            Text = "", Foreground = VxUi.Err, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
        };
        _chipError = new Border
        {
            Background = VxUi.BgError, BorderBrush = VxUi.Err, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
            IsVisible = false, Child = _chipErrorTexto,
        };

        var botones = VxUi.Fila();
        botones.Children.Add(VxUi.Boton("Guardar implemento", () => _ = GuardarAsync(), primario: true));
        botones.Children.Add(VxUi.Boton("Recargar", () => _ = CargarAsync()));
        botones.Children.Add(_estado);

        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(botones);
        sp.Children.Add(_chipError);
        return sp;
    }

    // =======================================================================
    //  Tick: opciones de UID
    // =======================================================================

    public override void Live() => RefrescarOpcionesUid(forzar: false);

    /// <summary>Repuebla los desplegables de UID con los nodos vistos, SIN
    /// perder la selección. Dos cuidados: no tocar un desplegable abierto
    /// (Avalonia colapsa la selección) y solo repoblar cuando cambió el set de
    /// UIDs.</summary>
    private void RefrescarOpcionesUid(bool forzar)
    {
        if (_filas.Count == 0) return;

        var firma = new System.Text.StringBuilder();
        foreach (var n in C.Nodos)
            firma.Append(n.Uid).Append(n.Online ? "+" : "-").Append('|');
        string f = firma.ToString();
        if (!forzar && f == _firmaNodos) return;
        _firmaNodos = f;

        foreach (var fila in _filas)
        {
            if (fila.Uid.IsDropDownOpen) continue;
            string actual = (fila.Uid.SelectedItem as OpcionUid)?.Uid ?? fila.Origen.Uid ?? "";

            var ops = new List<OpcionUid>
            {
                new OpcionUid { Uid = "", Texto = PilotX.Cockpit.Bars.Traductor.T("— elegir nodo —") },
            };
            int sel = 0;
            bool visto = string.IsNullOrEmpty(actual);
            foreach (var n in C.Nodos)
            {
                if (string.IsNullOrEmpty(n.Uid)) continue;
                ops.Add(new OpcionUid
                {
                    Uid = n.Uid!,
                    Texto = n.Uid + (n.Online
                        ? " (" + PilotX.Cockpit.Bars.Traductor.T("online") + ")"
                        : " (" + PilotX.Cockpit.Bars.Traductor.T("offline") + ")"),
                });
                if (string.Equals(n.Uid, actual, StringComparison.Ordinal))
                {
                    sel = ops.Count - 1;
                    visto = true;
                }
            }
            // Un UID guardado que todavía no publicó no se pierde: entra como
            // "(no visto)" y sigue seleccionado.
            if (!visto)
            {
                ops.Add(new OpcionUid
                {
                    Uid = actual,
                    Texto = actual + " (" + PilotX.Cockpit.Bars.Traductor.T("no visto") + ")",
                });
                sel = ops.Count - 1;
            }
            fila.Uid.ItemsSource = ops;
            fila.Uid.SelectedIndex = sel;
        }
    }

    // =======================================================================
    //  Guardado
    // =======================================================================

    private async Task GuardarAsync()
    {
        if (C.Imp == null)
        {
            VxUi.SetEstado(_estado, "No se pudo cargar el implemento", "err");
            return;
        }
        if (_chipError != null) _chipError.IsVisible = false;
        VxUi.SetEstado(_estado, "Guardando…", "");

        var imp = C.Imp.Implemento;
        imp.Setup ??= new VxSetup();

        // Solo los campos que VistaX posee. La geometría y los trenes los
        // ignora el backend (los re-deriva del central) pero viajan tal cual
        // vinieron, sin inventar valores.
        imp.Setup.DensidadObjetivo = VxUi.LeerDouble(_txtObjetivo, imp.Setup.DensidadObjetivo);
        imp.Setup.ObjetivoFuente   = _cboFuente?.SelectedIndex == 1 ? "manual" : "quantix";
        imp.Setup.ToleranciaDesvio = VxUi.LeerDouble(_txtTolerancia, imp.Setup.ToleranciaDesvio);
        imp.Setup.SurcosPorTorre   = VxUi.LeerInt(_txtSurcosTorre, imp.Setup.SurcosPorTorre);
        imp.Setup.VistaModoDefault = _cboVista?.SelectedIndex == 1 ? "torres" : "surcos";

        var sensores = new List<VxSensorCfg>();
        foreach (var f in _filas)
        {
            var s = f.Origen;                       // se edita la fila cargada:
            s.Uid      = (f.Uid.SelectedItem as OpcionUid)?.Uid ?? "";   // así no se
            s.Cable    = VxUi.LeerInt(f.Cable, 0);                        // pierden
            s.Bajada   = VxUi.LeerInt(f.Bajada, 0);                       // muted /
            s.Tren     = (f.Tren.SelectedItem as OpcionTren)?.Id ?? 0;    // objetivo
            s.Tipo     = (f.Tipo.SelectedItem as OpcionTipo)?.Id ?? "semilla";
            s.IsActive = f.Activo.IsChecked == true;
            sensores.Add(s);
        }
        imp.MapeoSensores = sensores;

        var r = await C.Client.PutImplementoAsync(imp).ConfigureAwait(true);
        if (!r.Ok)
        {
            if (!string.IsNullOrEmpty(r.Error) && _chipError != null && _chipErrorTexto != null)
            {
                _chipErrorTexto.Text = r.Texto();
                _chipError.IsVisible = true;
                VxUi.SetEstado(_estado, "", "");
            }
            else
            {
                VxUi.SetEstado(_estado, "Error al guardar: " + r.Texto(), "err");
            }
            return;
        }
        VxUi.SetEstado(_estado, "Guardado ✓", "ok");
    }
}
