// ============================================================================
// EngancheTab.cs — pestaña "Implemento › Enganche" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=tconfig` (la sección data-tab="tconfig" +
// tabs.tconfig de config.js).
//
// QUÉ QUEDÓ NATIVO: las cuatro cards de estilo de enganche (Fijo trasero /
// Tanque intermedio (TBT) / Frontal / De arrastre), el modo cosechadora
// (informativo, sin nada que elegir), el guardado
// (POST /api/aog/config/enganche_estilo) y la relectura del snapshot después
// de guardar.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron
// (Distancias, Offset, Pivote, Timing…).
//
// Qué config toca (la trampa de las DOS configuraciones de implemento):
//   · esta pestaña habla con /api/aog/config — la de PilotX, la que usa el
//     GUIADO y la geometría del implemento, respaldada por tool.json
//     (`is_tool_front` / `is_tool_tbt` / `is_tool_trailing` /
//     `is_tool_rear_fixed` + `hitch_length`).
//   · NO toca /api/implemento (surcos y semillas/ha de la pantalla de
//     siembra). Son dos configuraciones distintas y NO están sincronizadas:
//     acá no hay nada que espejar, pero conviene saberlo antes de "arreglar"
//     una discrepancia entre pantallas.
//
// Trampas cubiertas:
//   · El estilo se lee SIEMPRE del string `enganche.estilo` del wire, NUNCA se
//     reconstruye de los flags booleanos: guardar "tbt" prende isToolTBT Y
//     isToolTrailing a la vez (quirk del original), así que quien derive el
//     estilo de los flags pinta un TBT como "de arrastre". El backend ya lo
//     resuelve con la prioridad front → tbt → trailing → rear.
//   · Guardar el estilo tiene EFECTO COLATERAL en el motor: `front` fuerza
//     hitchLength > 0 y cualquier otro estilo lo fuerza < 0, y ese valor
//     corregido se persiste. Es decir, tocar acá le cambia el número a las
//     pestañas Distancias y Dimensiones. Por eso, tras guardar, se re-lee el
//     snapshot (RefrescarSnapshot) y la nota que lo avisa está a la vista
//     ANTES de tocar Guardar.
//   · Cosechadora: el motor obliga implemento frontal. La pestaña esconde las
//     cards y muestra el diagrama, igual que el HTML — y NO postea "front" por
//     su cuenta: eso ya lo hizo el guardado del tipo de vehículo. Postear acá
//     sería un segundo dueño del mismo dato.
//
// Quirk del HTML que NO se porta (a propósito):
//   · config.js pone `tc.dirty = false` ANTES del POST: si el guardado falla,
//     el segundo intento no manda nada y el shell canta "Guardado ✔" sin haber
//     guardado. Acá el dirty se limpia SOLO si el POST salió bien.
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

public sealed class EngancheTab : ConfigTab
{
    /// <summary>Una card de la grilla. `Clave` es EXACTAMENTE lo que viaja al
    /// wire (`rear|tbt|front|trailing`, lowercase): el backend rechaza
    /// cualquier otra cosa con "estilo-invalido".</summary>
    private sealed class Estilo
    {
        public string Clave = "";
        public string Titulo = "";
        public string Icono = "";
    }

    // Mismo orden que el #tcRadios del HTML (rear · tbt · front · trailing).
    // NO es alfabético ni el del enum: es el que el operario ya conoce.
    private static readonly Estilo[] ESTILOS =
    {
        new Estilo { Clave = "rear",     Titulo = "Fijo trasero",             Icono = "ToolChkRear.png"     },
        new Estilo { Clave = "tbt",      Titulo = "Tanque intermedio (TBT)",  Icono = "ToolChkTBT.png"      },
        new Estilo { Clave = "front",    Titulo = "Frontal",                  Icono = "ToolChkFront.png"    },
        new Estilo { Clave = "trailing", Titulo = "De arrastre",              Icono = "ToolChkTrailing.png" },
    };

    /// <summary>Tipo de vehículo "cosechadora" en el wire (`vehicle_type`).</summary>
    private const int TipoCosechadora = 1;

    // ---- modelo local (el `tc` de config.js) -------------------------------
    private string _estilo = "trailing";   // default del JS; enter() siempre lo pisa
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: las cards quedan muertas (anti doble-tap).</summary>
    private bool _guardando;

    private readonly Dictionary<string, Border> _cards = new Dictionary<string, Border>(StringComparer.Ordinal);

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    /// <summary>Modo con el que se armó el árbol. Si el operario cambia el tipo
    /// de vehículo en otra pestaña (o desde el celular), el árbol de acá tiene
    /// que rearmarse: son dos layouts distintos, no un IsVisible.</summary>
    private bool _cosechadoraPintada;

    public EngancheTab(CfgCtx c) : base(c) { }

    /// <summary>Tiene un campo (el estilo) y lo manda al motor ⇒ botón Guardar.
    /// En modo cosechadora no hay nada tocable, pero el botón se deja visible y
    /// simplemente nunca hay cambios: esconderlo al entrar y mostrarlo al salir
    /// haría bailar el footer.</summary>
    public override bool TieneGuardar => true;

    /// <summary>El shell pregunta esto antes de decir "Guardado ✔": si el
    /// operario toca Guardar sin haber elegido otro estilo, no se manda nada al
    /// motor y el footer dice "Sin cambios", no "Guardado".</summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: lee el estilo del snapshot, decide el modo
    /// cosechadora y repinta. Descarta cualquier cambio sin guardar, igual que
    /// el HTML.</summary>
    public override Task AlEntrarAsync()
    {
        _estilo = EstiloDelSnapshot();
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
        PintarSeleccion();
        try
        {
            C.Estado?.Invoke("Guardando…", "");

            // Body EXACTO del HTML: un solo campo, lowercase. Nada más viaja.
            var r = await C.Client.GuardarAsync("enganche_estilo", new { estilo = _estilo })
                    .ConfigureAwait(true);

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

            // Relectura OBLIGATORIA: el motor corrige el SIGNO del enganche
            // según el estilo (front → positivo, el resto → negativo) y lo
            // persiste. Sin esto, Distancias y Dimensiones mostrarían un valor
            // que ya cambió solo. Además, en cosechadora el backend fuerza
            // "front" ignorando lo que se mandó: la relectura es la que trae de
            // vuelta lo que REALMENTE quedó guardado.
            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }

            // Resincronizar con lo persistido: si el backend guardó otra cosa
            // (cosechadora → front), la card elegida tiene que mostrarlo.
            _estilo = EstiloDelSnapshot();
            PintarSeleccion();
            return true;
        }
        finally
        {
            _guardando = false;
            PintarSeleccion();
        }
    }

    /// <summary>El string del wire, tal cual. Nunca derivado de los flags
    /// booleanos (ver cabecera: un TBT se pintaría como "de arrastre").</summary>
    private string EstiloDelSnapshot()
    {
        string e = (C.Snap?.Enganche?.Estilo ?? "").Trim().ToLowerInvariant();
        foreach (var x in ESTILOS) if (x.Clave == e) return e;
        // Estilo desconocido (motor viejo o snapshot incompleto): "trailing",
        // el default del JS. No se inventa un guardado para "arreglarlo".
        return "trailing";
    }

    private bool EsCosechadora() => (C.Snap?.Vehiculo?.VehicleType ?? 0) == TipoCosechadora;

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _cards.Clear();
        _estadoPintado = EstadoActual();
        _cosechadoraPintada = EsCosechadora();

        // MaxWidth para que el WrapPanel corte en DOS columnas: los dibujos son
        // apaisados (200×98) y sin tope la grilla se estira a lo ancho del panel
        // y deja las cuatro cards en una fila con barra horizontal. 2×2 entra
        // entero en la pantalla de 10" sin scrollear.
        var carta = new StackPanel { Spacing = 10, MaxWidth = 480 };
        carta.Children.Add(CfgUi.Titulo("Estilo de enganche"));

        if (C.SinDatos)
        {
            carta.Children.Add(new TextBlock
            {
                Text = PilotX.Cockpit.Bars.Traductor.T("PilotX no responde — todavía no llegaron los datos."),
                Foreground = CfgUi.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (C.ServicioCaido)
        {
            carta.Children.Add(CfgUi.ChipError("Servicio de configuración no disponible", "AGP-NET-201"));
        }

        if (_cosechadoraPintada)
        {
            // Modo cosechadora: el motor obliga implemento frontal. Puramente
            // informativo — ni una card, igual que el #tcHarvester del HTML.
            carta.Children.Add(new Image
            {
                Source = Icono("vehiclePageHarvester.png"),
                // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml (que la ventana
                // incluye para las barras del cockpit) trae un
                // `Style Selector="Image"` con máximos de 34 px que aplica a TODA
                // imagen de la ventana y le gana al Width local. Sin esto el
                // diagrama sale de estampilla.
                MaxWidth = 320, MaxHeight = 240,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            carta.Children.Add(CfgUi.Nota(
                "La cosechadora usa siempre implemento frontal — no hay nada que elegir."));
        }
        else
        {
            var grilla = CfgUi.Grilla();
            foreach (var e in ESTILOS) grilla.Children.Add(Card(e));
            carta.Children.Add(grilla);

            // Aviso del efecto colateral ANTES de tocar Guardar: el operario
            // tiene que saber que elegir el estilo puede darle vuelta el signo
            // del enganche que ve en Distancias y Dimensiones.
            carta.Children.Add(CfgUi.Nota(
                "El estilo define el signo del enganche: frontal lo pone adelante y los demás, atrás."));
        }

        Children.Add(CfgUi.Carta(carta));

        PintarSeleccion();
    }

    /// <summary>El `.radioimg.estilo` del HTML: dibujo 200×98 + rótulo, con el
    /// borde verde y el fondo claro cuando está elegido.</summary>
    private Border Card(Estilo e)
    {
        var pila = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // El PNG viene con fondo blanco: recuadro blanco para que la card
        // elegida, que va con fondo verde claro, no muestre un rectángulo suelto.
        pila.Children.Add(new Border
        {
            Background = CfgUi.BgFila,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = new Image
            {
                Source = Icono(e.Icono),
                Width = 200, Height = 98,
                // MaxWidth/MaxHeight EXPLÍCITOS: ver el comentario del diagrama.
                MaxWidth = 200, MaxHeight = 98,
                Stretch = Stretch.Uniform,
            },
        });

        pila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(e.Titulo),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        var card = new Border
        {
            Width = 216, Height = 156,
            Margin = new Thickness(0, 0, 10, 10),
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };

        string clave = e.Clave;
        card.Tapped += (_, __) => Elegir(clave);

        _cards[e.Clave] = card;
        return card;
    }

    /// <summary>Tap en una card: solo cambia el modelo local y marca sucio. NO
    /// guarda — el guardado va por el botón Guardar del shell o al salir de la
    /// pestaña, igual que en el HTML (guardado on-leave).</summary>
    private void Elegir(string estilo)
    {
        if (_guardando) return;
        if (!Editable()) return;
        if (_estilo == estilo) return;
        _estilo = estilo;
        _dirty = true;
        PintarSeleccion();
        C.MarcarSucio?.Invoke();
    }

    /// <summary>Refresco de fondo (3 s). Si hay cambios sin guardar NO se toca
    /// nada: pisar la elección del operario con lo que hay en el motor sería
    /// perderle el cambio abajo del dedo. Sin cambios pendientes sí se
    /// resincroniza (alguien pudo tocarlo desde el celular).</summary>
    public override void Live()
    {
        // El cambio de modo (o de estado de conexión) reconstruye: cards y
        // diagrama son dos layouts distintos, no un IsVisible.
        if (_estadoPintado != EstadoActual() || _cosechadoraPintada != EsCosechadora())
        {
            if (_dirty || _guardando) return;   // salvo que haya algo sin guardar
            // Resincronizar ANTES de rearmar: el caso típico de este salto es
            // "el Hub estaba caído y recién ahora contestó", y hasta ese momento
            // _estilo era el default de memoria ("trailing"), no el del motor.
            // Sin esta línea el árbol nuevo sale con la card vieja marcada Y
            // habilitada hasta el tick siguiente: tres segundos mostrándole al
            // operario un estilo de enganche que la máquina no tiene.
            _estilo = EstiloDelSnapshot();
            Rebuild();
            return;
        }
        if (_dirty || _guardando) return;
        string e = EstiloDelSnapshot();
        if (e == _estilo) return;
        _estilo = e;
        PintarSeleccion();
    }

    // =======================================================================
    //  Pintura
    // =======================================================================

    private void PintarSeleccion()
    {
        bool editable = Editable() && !_guardando;
        foreach (var kv in _cards)
        {
            bool sel = kv.Key == _estilo;
            kv.Value.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
            kv.Value.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
            kv.Value.Opacity = editable ? 1.0 : 0.55;
            kv.Value.IsHitTestVisible = editable;
        }
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor y el POST iría al
    /// mismo Hub que no está contestando: elegir a ciegas es peor que no poder
    /// elegir. Las cards quedan apagadas hasta que vuelvan los datos.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    // =======================================================================
    //  Íconos
    // =======================================================================

    /// <summary>Los PNG se decodifican UNA vez y se comparten entre cards y
    /// entre rebuilds: Rebuild() corre en cada entrada a la pestaña y cada vez
    /// que cambia el estado de conexión, y sin cache un rato de red inestable
    /// deja decenas de bitmaps sin liberar en una PC de cabina. Solo se toca
    /// desde el hilo de UI.</summary>
    private static readonly Dictionary<string, Bitmap?> _iconos =
        new Dictionary<string, Bitmap?>(StringComparer.Ordinal);

    private static Bitmap? Icono(string nombre)
    {
        if (_iconos.TryGetValue(nombre, out var cacheado)) return cacheado;
        Bitmap? bmp;
        try { bmp = new Bitmap(AssetLoader.Open(new Uri("avares://PilotX.UI/Assets/config/" + nombre))); }
        catch { bmp = null; }   // falta el asset ⇒ card sin dibujo, nunca una excepción
        _iconos[nombre] = bmp;
        return bmp;
    }
}
