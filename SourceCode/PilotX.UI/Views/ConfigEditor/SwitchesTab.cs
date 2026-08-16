// ============================================================================
// SwitchesTab.cs — pestaña "Secciones › Switches" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=tswitches` (la sección data-tab="tswitches" +
// tabs.tswitches / swPintar() de config.js).
//
// QUÉ QUEDÓ NATIVO: las dos cartas (Switch de trabajo / Switch de dirección)
// con sus 7 filas táctiles, la cascada de habilitación, el ícono dinámico de
// "Activo con contacto cerrado" y el guardado (POST /api/aog/config/switches).
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas todavía sin portar.
//
// Qué son estos 5 booleanos: el switch FÍSICO de trabajo y el de dirección
// cableados al módulo de máquina. Llegan por PGN y los interpreta
// AgOpenGPS.Core/Classes/CModuleComm.cs (CheckWorkAndSteerSwitch):
//   · work_enabled / steer_enabled → el motor escucha ese switch remoto;
//   · work_active_low  → contacto CERRADO significa "trabajando";
//   · *_manual_sections → al activarse el switch, las secciones pasan a master
//     Manual (true) o Auto (false).
// El derivado `is_remote_work_system_on = work_enabled || steer_enabled` NO
// viaja en el body: lo recalcula el backend en cada POST.
//
// Qué configuración toca (la trampa de las DOS configuraciones de implemento):
//   · SOLO /api/aog/config (la de PilotX / el guiado). NO toca /api/implemento
//     (surcos y semillas/ha de la pantalla de siembra): acá no hay nada que
//     espejar, pero conviene saberlo antes de "arreglar" una discrepancia.
//
// Trampas cubiertas:
//   · SEMÁNTICA DE work_active_low: "Activo con contacto cerrado" = true. La
//     comparación del motor es `workSwitchHigh != isWorkSwitchActiveLow`, así
//     que invertir el bool hace que la máquina aplique AL REVÉS del switch
//     físico (secciones prendidas con el implemento levantado ⇒ siembra doble o
//     vacíos). Se manda tal cual, sin "corregir" nada.
//   · CASCADA: apagar "Activar" NO resetea ni deja de mandar a sus hijos. Los
//     hijos solo se atenúan y se vuelven intocables; su valor viaja igual en el
//     body, exactamente como en el HTML. Resetearlos pisaría configuración que
//     el operario ya tenía.
//   · Manual/Auto es UN SOLO bool por par: siempre hay exactamente uno marcado.
//     No es tri-estado y no se "mejora" agregándole un "ninguno".
//   · work_enabled / steer_enabled salen del RUNTIME del motor (_engine.Mc.*),
//     no de Settings: se pinta SIEMPRE lo que devuelve el GET, nunca lo que
//     "se guardó ayer".
//   · Los PNG usan siempre la variante *Off como pictograma fijo (la selección
//     la marca el borde verde). El ÚNICO ícono dinámico es el de contacto
//     cerrado: SwitchActiveClosed/SwitchActiveOpen.
//
// Esta pestaña NO manda PGN al módulo (a diferencia de Pines relay y Máquina,
// que tienen "Enviar + Guardar"). El original tampoco: no inventar un envío.
//
// PERSISTENCIA — LOS 5 CAMPOS PERSISTEN. Verificado con REINICIO REAL del motor
// (2026-08-16), no con un round-trip (el GET lee Settings EN MEMORIA: guardar y
// releer no prueba nada). Método: POST → inspección del XML en disco → taskkill
// → arranque limpio → GET, campo por campo y con una combinación asimétrica
// (work_enabled=true / steer_enabled=false) para descartar un "todo true".
//
// HISTORIA — hasta el 2026-08-16 acá faltaban DOS. `work_enabled` y
// `steer_enabled` se escribían en el XML pero volvían apagados tras reiniciar,
// porque el snapshot los lee del RUNTIME (`_engine.Mc.isWorkSwitchEnabled` /
// `isSteerWorkSwitchEnabled`) mientras que los otros tres salen de Settings, y
// NADA en el arranque headless copiaba esos `setF_*` a `Mc` (eso lo hacía
// LoadSettings de FormGPS). Peor que el síntoma visible: `Mc.isRemoteWorkSystemOn`
// también volvía al false del constructor de CModuleComm, y
// `CheckWorkAndSteerSwitch` entra al bloque de trabajo/dirección SOLO con ese
// flag en true — o sea que tras cada arranque el switch físico quedaba INERTE
// (ni bien ni al revés) hasta el próximo guardado del panel.
//
// ARREGLADO EN EL MOTOR, no acá: `GuidanceEngineHost.CargarSwitchesRemotos()`
// aplica los seis `setF_*` a `Mc` en `Start()` y al activar un perfil. El
// snapshot SIGUE leyendo `Mc` a propósito: así el panel muestra lo que el motor
// realmente tiene aplicado, no lo que dice el archivo. Si algún día vuelven a
// divergir, el bug está en el motor y el panel lo va a delatar en vez de taparlo.
// El panel pinta SIEMPRE lo que devuelve el GET; nunca un "Guardado ✔" que mienta.
//
// Todo esto depende de que haya perfil de vehículo: `Settings.Save()` escribe
// <Documentos>\AgOpenGPS\Vehicles\<perfil>.XML SOLO si
// RegistrySettings.vehicleFileName no está vacío (el motor headless se crea el
// perfil "PilotX" al arrancar). Ninguno de estos 5 campos vive en tool.json.
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

public sealed class SwitchesTab : ConfigTab
{
    /// <summary>Una fila táctil (el `.chkimg.sw` del HTML): ícono 52×52 +
    /// rótulo. `Hijo` = la deshabilita el master de su carta.</summary>
    private sealed class Fila
    {
        public Border Borde = null!;
        public Image Icono = null!;
        public bool Hijo;
        public Func<bool> Sel = () => false;
    }

    // ---- modelo local (el `sw` de config.js) -------------------------------
    private bool _workOn, _workManual, _workLow, _steerOn, _steerManual;
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: las filas quedan muertas (anti doble-tap).</summary>
    private bool _guardando;

    private readonly List<Fila> _filasWork = new List<Fila>();
    private readonly List<Fila> _filasSteer = new List<Fila>();

    /// <summary>Fila del contacto cerrado: su ícono cambia con el valor.</summary>
    private Fila? _filaLow;

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para que el refresco de fondo sepa si tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public SwitchesTab(CfgCtx c) : base(c) { }

    public override bool TieneGuardar => true;

    /// <summary>El shell pregunta esto antes de cantar "Guardado ✔": tocar
    /// Guardar sin haber cambiado nada no manda ningún POST.</summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: lee los 5 booleanos del snapshot y repinta.
    /// Descarta cualquier cambio sin guardar, igual que el HTML.</summary>
    public override Task AlEntrarAsync()
    {
        LeerDelSnapshot();
        _dirty = false;
        Rebuild();
        return Task.CompletedTask;
    }

    /// <summary>`leave()`: guarda si hay cambios. false CANCELA la navegación
    /// (el operario se queda acá con el error a la vista).</summary>
    public override async Task<bool> AlSalirAsync()
    {
        if (!_dirty) return true;
        if (C.Client == null) { C.Estado?.Invoke("Sin conexión con PilotX", "err"); return false; }
        if (_guardando) return false;

        _guardando = true;
        Pintar();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            // Body EXACTO del HTML: los 5 campos SIEMPRE, aunque solo haya
            // cambiado uno. El backend hace merge campo a campo, pero mandar el
            // modelo completo evita que un POST tardío deje un estado mezclado.
            var r = await C.Client.GuardarAsync("switches", new
            {
                work_enabled = _workOn,
                work_active_low = _workLow,
                work_manual_sections = _workManual,
                steer_enabled = _steerOn,
                steer_manual_sections = _steerManual,
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

            // El dirty se limpia SOLO si el POST salió bien (el HTML lo limpia
            // antes de postear: un guardado fallido allá no se reintenta).
            _dirty = false;
            C.Estado?.Invoke("Guardado ✔", "ok");

            // Relectura: work_enabled/steer_enabled los devuelve el RUNTIME del
            // motor, así que lo que quedó realmente aplicado lo dice el GET.
            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }

            LeerDelSnapshot();
            Pintar();
            return true;
        }
        finally
        {
            _guardando = false;
            Pintar();
        }
    }

    /// <summary>Mapeo snapshot → modelo (el `enter()` del JS). Sin snapshot se
    /// deja lo que había: no se inventan defaults que después se posteen.</summary>
    private void LeerDelSnapshot()
    {
        var z = C.Snap?.Switches;
        if (z == null) return;
        _workOn = z.WorkEnabled;
        _workManual = z.WorkManualSections;
        _workLow = z.WorkActiveLow;
        _steerOn = z.SteerEnabled;
        _steerManual = z.SteerManualSections;
    }

    /// <summary>¿El snapshot dice algo distinto de lo que tenemos en pantalla?
    /// (para el refresco de fondo, cuando alguien tocó la config del celular).</summary>
    private bool DifiereDelSnapshot()
    {
        var z = C.Snap?.Switches;
        if (z == null) return false;
        return z.WorkEnabled != _workOn
            || z.WorkManualSections != _workManual
            || z.WorkActiveLow != _workLow
            || z.SteerEnabled != _steerOn
            || z.SteerManualSections != _steerManual;
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _filasWork.Clear();
        _filasSteer.Clear();
        _filaLow = null;
        _estadoPintado = EstadoActual();

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
        else if (C.Snap?.Switches == null)
        {
            // El motor contestó ok pero sin la sección: se muestra lo último
            // conocido y NO se deja tocar (ver Editable()).
            Children.Add(CfgUi.ChipError("PilotX no informó la configuración de los switches", "AGP-NET-201"));
        }

        // Las dos cartas al lado (el `.dosCol` del HTML). WrapPanel: si la
        // pantalla de 10" no da el ancho, la segunda baja sola.
        var dosCol = CfgUi.Grilla();
        dosCol.Children.Add(CartaTrabajo());
        dosCol.Children.Add(CartaDireccion());
        Children.Add(dosCol);

        Children.Add(CfgUi.Nota(
            "Son los switches cableados al módulo de máquina. \"Activo con contacto cerrado\" " +
            "significa que el contacto CERRADO es trabajando: si se lo pone al revés, la máquina " +
            "aplica al revés del switch."));

        Pintar();
    }

    private Border CartaTrabajo()
    {
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(CfgUi.Titulo("Switch de trabajo"));

        col.Children.Add(FilaDe(_filasWork, "HydraulicLiftOff.png", "Activar", false,
            () => _workOn, () => { _workOn = !_workOn; }));
        col.Children.Add(FilaDe(_filasWork, "ManualOff.png", "Secciones: Manual", true,
            () => _workManual, () => { _workManual = true; }));
        col.Children.Add(FilaDe(_filasWork, "SectionMasterOff.png", "Secciones: Auto", true,
            () => !_workManual, () => { _workManual = false; }));

        // Ícono DINÁMICO: cerrado/abierto según el valor (única fila del HTML
        // que cambia el dibujo — las demás usan siempre la variante *Off).
        var low = FilaDe(_filasWork, "SwitchActiveOpen.png", "Activo con contacto cerrado", true,
            () => _workLow, () => { _workLow = !_workLow; });
        _filaLow = _filasWork[_filasWork.Count - 1];
        col.Children.Add(low);

        return Carta(col);
    }

    private Border CartaDireccion()
    {
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(CfgUi.Titulo("Switch de dirección"));

        col.Children.Add(FilaDe(_filasSteer, "AutoSteerOff.png", "Activar", false,
            () => _steerOn, () => { _steerOn = !_steerOn; }));
        col.Children.Add(FilaDe(_filasSteer, "ManualOff.png", "Secciones: Manual", true,
            () => _steerManual, () => { _steerManual = true; }));
        col.Children.Add(FilaDe(_filasSteer, "SectionMasterOff.png", "Secciones: Auto", true,
            () => !_steerManual, () => { _steerManual = false; }));

        return Carta(col);
    }

    /// <summary>Ancho fijo para que las dos cartas queden lado a lado y, cuando
    /// no entran, la segunda baje entera en vez de encogerse.</summary>
    private static Border Carta(Control contenido)
    {
        var b = CfgUi.Carta(contenido);
        b.Width = 320;
        b.Margin = new Thickness(0, 0, 12, 12);
        return b;
    }

    /// <summary>El `.chkimg.sw` del HTML: fila táctil de ~56 px con el ícono a
    /// la izquierda y el rótulo al lado. `hijo` = la apaga el master de la carta.
    /// `accion` solo toca el modelo local y marca sucio — el guardado va por el
    /// botón Guardar del shell o al salir de la pestaña, igual que en el HTML.</summary>
    private Border FilaDe(List<Fila> destino, string icono, string caption, bool hijo,
                          Func<bool> sel, Action accion)
    {
        var img = new Image
        {
            Source = Icono(icono),
            Width = 52, Height = 52,
            // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml (que la ventana
            // incluye para las barras del cockpit) trae un `Style
            // Selector="Image"` con máximos de 34 px que le gana al Width local.
            // Sin esto los pictogramas salen de estampilla.
            MaxWidth = 52, MaxHeight = 52,
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
        borde.Tapped += (_, __) => Tocar(accion);

        destino.Add(new Fila { Borde = borde, Icono = img, Hijo = hijo, Sel = sel });
        return borde;
    }

    private void Tocar(Action accion)
    {
        if (_guardando) return;
        if (!Editable()) return;
        accion();
        _dirty = true;
        Pintar();
        C.MarcarSucio?.Invoke();
    }

    /// <summary>Refresco de fondo (3 s). Con cambios sin guardar NO se toca
    /// nada: pisar la elección del operario abajo del dedo sería perderle el
    /// cambio. Sin cambios pendientes sí se resincroniza (alguien pudo tocar la
    /// config desde el celular, y config.html puede estar abierta en paralelo).</summary>
    public override void Live()
    {
        if (_estadoPintado != EstadoActual())
        {
            if (_dirty || _guardando) return;
            // Resincronizar ANTES de rearmar: el salto típico es "el Hub estaba
            // caído y recién ahora contestó", y hasta ese momento el modelo era
            // el de memoria, no el del motor.
            LeerDelSnapshot();
            Rebuild();
            return;
        }
        if (_dirty || _guardando) return;
        if (!DifiereDelSnapshot()) return;
        LeerDelSnapshot();
        Pintar();
    }

    // =======================================================================
    //  Pintura (el swPintar() del HTML, réplica exacta)
    // =======================================================================

    private void Pintar()
    {
        bool editable = Editable() && !_guardando;
        PintarColumna(_filasWork, _workOn, editable);
        PintarColumna(_filasSteer, _steerOn, editable);

        // Único ícono dinámico de la pantalla.
        if (_filaLow != null)
            _filaLow.Icono.Source = Icono(_workLow ? "SwitchActiveClosed.png" : "SwitchActiveOpen.png");
    }

    /// <summary>Cascada: si el master está apagado, los hijos quedan al 45 % y
    /// no se pueden tocar, PERO conservan su selección (no se resetean ni dejan
    /// de viajar en el body). El master nunca se deshabilita.</summary>
    private static void PintarColumna(List<Fila> filas, bool masterOn, bool editable)
    {
        foreach (var f in filas)
        {
            bool sel = f.Sel();
            f.Borde.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
            f.Borde.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;

            bool viva = editable && (!f.Hijo || masterOn);
            f.Borde.Opacity = !editable ? 0.55 : (f.Hijo && !masterOn ? 0.45 : 1.0);
            f.Borde.IsHitTestVisible = viva;
        }
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor y el POST iría al
    /// mismo Hub que no contesta: tocar a ciegas es peor que no poder tocar.
    /// Sin la sección `switches` tampoco se toca: el modelo local arrancaría en
    /// los defaults de C# (todo false) y el primer toque postearía
    /// work_active_low = false, que en el motor vale TRUE por defecto — o sea,
    /// daría vuelta la lectura del switch físico sin que nadie lo haya pedido.
    /// El HTML directamente se rompe en ese caso (lee `snap.switches.…`) y no
    /// llega a postear nada; acá se prefiere la fila muerta.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido && C.Snap?.Switches != null;

    /// <summary>0 sin datos · 1 servicio caído · 2 respondió pero sin la sección
    /// `switches` · 3 todo bien. El 2 es un estado propio para que el refresco de
    /// fondo rearme la pestaña (y saque el chip) apenas la sección aparezca.</summary>
    private int EstadoActual()
        => C.SinDatos ? 0 : C.ServicioCaido ? 1 : C.Snap?.Switches == null ? 2 : 3;

    // =======================================================================
    //  Íconos
    // =======================================================================

    /// <summary>Los PNG se decodifican UNA vez y se comparten entre filas y
    /// entre rebuilds (Rebuild corre en cada entrada a la pestaña y cada vez que
    /// cambia el estado de conexión). Solo se toca desde el hilo de UI.</summary>
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
