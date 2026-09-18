// ============================================================================
// VehiculoTab.cs — pestaña "Vehículo › Tipo" del ConfigPanel nativo.
// Porteo 1:1 de `config.html?tab=vconfig` (la sección data-tab="vconfig" +
// tabs.vconfig de config.js).
//
// QUÉ QUEDÓ NATIVO: las cuatro opciones de tipo (Rígido / Articulado /
// Cosechadora / Pulverizadora) con sus notas, el guardado
// (POST /api/aog/config/vehiculo) y la relectura del snapshot después de
// guardar.
// QUÉ SIGUE EN HTML: la página config.html entera — la usa la PWA del celular
// (strangler fig, NO se borra) — y las pestañas que todavía no se portaron.
//
// Decisiones que se heredan del rediseño 2026-08-10 y NO se resucitan:
//   · No hay elección de MARCA ni de opacidad/preview: el vehículo del mapa es
//     SIEMPRE el triángulo verde. El backend conserva esos campos por merge
//     porque el body que se manda es parcial a propósito.
//   · `is_vehicle_image:false` NO es una opción, es un false forzado en CADA
//     guardado: así un perfil viejo con imagen de marca queda saneado al
//     primer guardado. Va hardcodeado, sin control de UI.
//
// Trampas cubiertas:
//   · Rígido y Pulverizadora comparten `vehicle_type = 0`. La selección se
//     compara por SUB ("rigido" / "pulverizadora"), nunca por tipo. Y jamás se
//     manda un tipo nuevo (3) por la pulverizadora: el backend clampea 0..2 y
//     quedaría "articulado".
//   · La pulverizadora NO existe en el wire: es memoria puramente de UI. El
//     HTML la recuerda en localStorage del WebView y este panel en
//     Documentos\AgOpenGPS\pilotx_ui_prefs.json (clave "vehiculo_sub"). Son
//     DOS memorias distintas: la cabina puede resaltar "Pulverizadora" y el
//     celular "Rígido" (o al revés). Es solo estético — el motor solo conoce
//     0/1/2 — y se acepta a propósito: inventar un endpoint nuevo para esto
//     cambiaría el contrato del wire sin acordarlo.
//   · Guardar tipo 1 (cosechadora) tiene EFECTO COLATERAL en el motor: fuerza
//     el implemento a frontal y puede dar vuelta el signo del enganche. Por
//     eso, tras guardar, se re-lee el snapshot (RefrescarSnapshot) y la nota
//     que lo avisa está visible ANTES de tocar Guardar.
//
// Quirks del HTML que NO se portan (a propósito):
//   · config.js pone `v.dirty = false` ANTES del POST: si el guardado falla,
//     el segundo intento no manda nada y el shell canta "Guardado ✔" sin
//     haber guardado. Acá el dirty se limpia SOLO si el POST salió bien.
//   · El sub se persiste también solo si el guardado salió bien (en el HTML se
//     escribe en localStorage aunque el POST falle).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class VehiculoTab : ConfigTab
{
    /// <summary>Una opción de la grilla. `Tipo` es lo que viaja al wire y
    /// `Sub` lo que decide el resaltado (rígido y pulverizadora comparten
    /// tipo 0).</summary>
    private sealed class Opcion
    {
        public string Sub = "";
        public int Tipo;
        public string Titulo = "";
        public string Icono = "";
    }

    // Mismo orden que el #vTipos del HTML.
    private static readonly Opcion[] OPCIONES =
    {
        new Opcion { Sub = "rigido",        Tipo = 0, Titulo = "Rígido",        Icono = "VehicleTractorRigid.png" },
        new Opcion { Sub = "articulado",    Tipo = 2, Titulo = "Articulado",    Icono = "VehicleTractorArticulated.png" },
        new Opcion { Sub = "cosechadora",   Tipo = 1, Titulo = "Cosechadora",   Icono = "VehicleHarvester.png" },
        new Opcion { Sub = "pulverizadora", Tipo = 0, Titulo = "Pulverizadora", Icono = "VehicleSprayer.png" },
    };

    /// <summary>Clave del sub dentro de las prefs de UI (el `pilotx_vehiculo_sub`
    /// del localStorage en la versión HTML).</summary>
    private const string ClaveSub = "vehiculo_sub";

    // ---- modelo local (el `v` de config.js) --------------------------------
    private int _tipo;
    private string _sub = "rigido";
    private bool _dirty;

    /// <summary>Hay un POST en vuelo: las cards quedan muertas (anti doble-tap
    /// y anti "cambio el tipo mientras se está guardando otro").</summary>
    private bool _guardando;

    private readonly Dictionary<string, Border> _cards = new Dictionary<string, Border>(StringComparer.Ordinal);
    private TextBlock? _notaCosechadora, _notaPulverizadora;

    /// <summary>Estado con el que se armó el árbol (sin datos / servicio caído /
    /// ok), para saber si el refresco de fondo tiene que reconstruir.</summary>
    private int _estadoPintado = -1;

    public VehiculoTab(CfgCtx c) : base(c) { }

    /// <summary>Tiene un campo (el tipo) y lo manda al motor ⇒ botón Guardar.</summary>
    public override bool TieneGuardar => true;

    /// <summary>El shell pregunta esto antes de decir "Guardado ✔": si el
    /// operario toca Guardar sin haber elegido otra cosa, no se manda nada al
    /// motor y el footer tiene que decir "Sin cambios", no "Guardado".</summary>
    public override bool HayCambios => _dirty;

    // =======================================================================
    //  Ciclo de vida (enter/leave de config.js)
    // =======================================================================

    /// <summary>`enter()`: lee el tipo del snapshot, deriva el sub y repinta.
    /// Descarta cualquier cambio sin guardar, igual que el HTML.</summary>
    public override Task AlEntrarAsync()
    {
        _tipo = C.Snap?.Vehiculo?.VehicleType ?? 0;
        _sub = SubDesdeTipo(_tipo);
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

            // Body PARCIAL a propósito: marcas y opacidad quedan como estén.
            // `is_vehicle_image:false` va SIEMPRE (ver cabecera).
            var r = await C.Client.GuardarAsync("vehiculo",
                        new { vehicle_type = _tipo, is_vehicle_image = false })
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
            GuardarSubLocal(_sub);
            C.Estado?.Invoke("Guardado ✔", "ok");

            // Relectura OBLIGATORIA: guardar cosechadora fuerza el implemento a
            // frontal y puede invertir el signo del enganche en el motor. Sin
            // esto, las pestañas de Enganche/Distancias mostrarían datos que ya
            // cambiaron solos.
            if (C.RefrescarSnapshot != null)
            {
                try { await C.RefrescarSnapshot(CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                catch { }
            }
            return true;
        }
        finally
        {
            _guardando = false;
            PintarSeleccion();
        }
    }

    // =======================================================================
    //  Árbol
    // =======================================================================

    public override void Rebuild()
    {
        Children.Clear();
        _cards.Clear();
        _estadoPintado = EstadoActual();

        var carta = new StackPanel { Spacing = 10 };
        carta.Children.Add(CfgUi.Titulo("Tipo de vehículo"));

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

        var grilla = CfgUi.Grilla();
        foreach (var o in OPCIONES) grilla.Children.Add(Card(o));
        carta.Children.Add(grilla);

        // Notas contextuales (los <p class="nota"> del HTML, palabra por palabra).
        _notaCosechadora = CfgUi.Nota("La cosechadora fuerza el implemento a frontal.");
        _notaPulverizadora = CfgUi.Nota("La pulverizadora se comporta como un vehículo rígido.");
        carta.Children.Add(_notaCosechadora);
        carta.Children.Add(_notaPulverizadora);
        carta.Children.Add(CfgUi.Nota("El vehículo se dibuja en el mapa como el triángulo verde."));

        Children.Add(CfgUi.Carta(carta));

        PintarSeleccion();
    }

    private Border Card(Opcion o)
    {
        var pila = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // El PNG viene con fondo blanco (Avalonia no dibuja SVG sin paquete
        // extra): se lo mete en un recuadro blanco para que la card elegida,
        // que va con fondo verde claro, no muestre un rectángulo suelto.
        pila.Children.Add(new Border
        {
            Background = CfgUi.BgFila,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = new Image
            {
                Source = Icono(o.Icono),
                Width = 110, Height = 72,
                // MaxWidth/MaxHeight EXPLÍCITOS: BarStyles.axaml (que la ventana
                // incluye para las barras del cockpit) trae un
                // `Style Selector="Image"` con MaxWidth/MaxHeight 34 que aplica a
                // TODA imagen de la ventana. Un máximo le gana al Width local, así
                // que sin estas dos líneas el ícono sale de 34 px y la card queda
                // con un dibujito perdido en el medio.
                MaxWidth = 110, MaxHeight = 72,
                Stretch = Stretch.Uniform,
            },
        });

        pila.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(o.Titulo),
            Foreground = CfgUi.TextoMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var card = new Border
        {
            Width = 152, Height = 132,
            Margin = new Thickness(0, 0, 10, 10),
            Background = CfgUi.BgFila,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pila,
        };

        string sub = o.Sub;
        int tipo = o.Tipo;
        card.Tapped += (_, __) => Elegir(tipo, sub);

        _cards[o.Sub] = card;
        return card;
    }

    /// <summary>Click en una card: solo cambia el modelo local y marca sucio.
    /// NO guarda — el guardado va por el botón Guardar del shell o al salir de
    /// la pestaña, igual que en el HTML.</summary>
    private void Elegir(int tipo, string sub)
    {
        if (_guardando) return;
        if (!Editable()) return;
        if (_sub == sub) return;
        _tipo = tipo;
        _sub = sub;
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
        if (_estadoPintado != EstadoActual()) { Rebuild(); return; }
        if (_dirty || _guardando) return;
        int tipo = C.Snap?.Vehiculo?.VehicleType ?? 0;
        if (tipo == _tipo) return;
        _tipo = tipo;
        _sub = SubDesdeTipo(tipo);
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
            bool sel = kv.Key == _sub;
            kv.Value.BorderBrush = sel ? CfgUi.Verde : CfgUi.Borde;
            kv.Value.Background = sel ? CfgUi.BgFilaSel : CfgUi.BgFila;
            kv.Value.Opacity = editable ? 1.0 : 0.55;
            kv.Value.IsHitTestVisible = editable;
        }
        if (_notaCosechadora != null) _notaCosechadora.IsVisible = _sub == "cosechadora";
        if (_notaPulverizadora != null) _notaPulverizadora.IsVisible = _sub == "pulverizadora";
    }

    /// <summary>Sin snapshot no se sabe qué tiene el motor y el POST iría al
    /// mismo Hub que no está contestando: elegir a ciegas es peor que no poder
    /// elegir. Las cards quedan apagadas hasta que vuelvan los datos.</summary>
    private bool Editable() => !C.SinDatos && !C.ServicioCaido;

    private int EstadoActual() => C.SinDatos ? 0 : C.ServicioCaido ? 1 : 2;

    // =======================================================================
    //  Sub (la memoria de "pulverizadora", que no existe en el wire)
    // =======================================================================

    /// <summary>`vSubDesdeTipo` del HTML: 1 → cosechadora, 2 → articulado y
    /// 0 → lo último que se eligió entre rígido y pulverizadora.</summary>
    private static string SubDesdeTipo(int tipo)
    {
        if (tipo == 1) return "cosechadora";
        if (tipo == 2) return "articulado";
        return LeerSubLocal() == "pulverizadora" ? "pulverizadora" : "rigido";
    }

    /// <summary>Documentos\AgOpenGPS\pilotx_ui_prefs.json — junto a los datos
    /// del operario, así sobrevive a una actualización de PilotX.</summary>
    private static string RutaPrefs()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "AgOpenGPS", "pilotx_ui_prefs.json");

    private static string LeerSubLocal()
    {
        try
        {
            string ruta = RutaPrefs();
            if (!File.Exists(ruta)) return "rigido";
            var raiz = JsonNode.Parse(File.ReadAllText(ruta)) as JsonObject;
            var v = raiz?[ClaveSub];
            return v == null ? "rigido" : (v.GetValue<string>() ?? "rigido");
        }
        catch { return "rigido"; }   // prefs rotas ⇒ rígido, nunca una excepción
    }

    /// <summary>Escribe la clave sin pisar el resto del archivo: estas prefs
    /// las van a compartir otras pantallas nativas.</summary>
    private static void GuardarSubLocal(string sub)
    {
        try
        {
            string ruta = RutaPrefs();
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            JsonObject raiz;
            try { raiz = (File.Exists(ruta) ? JsonNode.Parse(File.ReadAllText(ruta)) as JsonObject : null) ?? new JsonObject(); }
            catch { raiz = new JsonObject(); }
            raiz[ClaveSub] = sub;
            File.WriteAllText(ruta, raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* sin prefs la elección se pierde al reabrir; no es del motor */ }
    }

    // =======================================================================
    //  Íconos
    // =======================================================================

    /// <summary>Los 4 PNG se decodifican UNA vez y se comparten entre cards y
    /// entre rebuilds. Cada uno es de 480×360 (≈700 kB en GPU/RAM) y Rebuild()
    /// corre en cada entrada a la pestaña y cada vez que cambia el estado de
    /// conexión: sin cache, un rato de red inestable deja decenas de bitmaps
    /// sin liberar en una PC de cabina. Solo se toca desde el hilo de UI.</summary>
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
