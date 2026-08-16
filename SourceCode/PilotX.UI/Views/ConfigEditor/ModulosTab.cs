// ============================================================================
// ModulosTab.cs — la lista de MODULOS de la Configuracion, nativa.
//
// POR QUE EXISTE: el boton "Modulos y mas..." abria pages/config.html adentro
// del WebView y el operario elegia el modulo del menu HTML. Pero la mitad de
// esos modulos YA SON PANTALLAS NATIVAS (QuantiX, VistaX, FlowX, SectionX,
// StormX, Nodos, Camaras, Sistema, Sonidos, Actualizar, CoreX-ECU, Hub...):
// entrando por ahi se abria la version HTML de una pantalla que ya existe en
// Avalonia. Dos puertas a la misma cosa, y la que se veia dependia de por
// donde entraste. Esta grilla es la unica puerta: cada modulo abre lo que hay
// de verdad.
//
// CADA MODULO SE ABRE DE UNA SOLA MANERA:
//   · Clave != null  -> PANEL NATIVO. Se pide por CfgCtx.AbrirPanelNativo y lo
//     resuelve MainWindow (mismo patron que los OnRequest* que ya existian).
//     Esos paneles son de pantalla completa / card flotante propia, asi que
//     ellos mismos cierran la Configuracion al abrirse.
//   · Ruta  != null  -> pagina del Hub EMBEBIDA en la misma tarjeta
//     (CfgCtx.AbrirHtmlEmbebido). NUNCA a pantalla completa: si se va a
//     pantalla completa el operario pierde el X y el menu (reporte 2026-08-16).
//
// LA LISTA NO MIENTE: si el equipo no tiene navegador embebido (App.WebViewHost
// nulo) los modulos que todavia son HTML se muestran APAGADOS y con el motivo
// escrito, en vez de ofrecer un boton que no hace nada.
//
// AL AGREGAR UN PORTEO NATIVO: cambiar la fila de MODS (sacarle Ruta, ponerle
// Clave) y agregar el case en el dispatch de MainWindow. Nada mas.
//
// Las paginas HTML NO se borran ni se tocan: las usa la PWA del celular.
// ============================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class ModulosTab : ConfigTab
{
    /// <summary>Una entrada de la grilla. Clave = panel nativo (la resuelve
    /// MainWindow); Ruta = pagina del Hub embebida. Uno de los dos, nunca los
    /// dos: si hay pantalla nativa, la HTML no se ofrece.</summary>
    private sealed class Mod
    {
        public string Titulo = "";
        public string Desc = "";
        public string Grupo = "";
        public string? Clave;      // panel nativo
        public string? Ruta;       // pagina del Hub (fallback embebido)
    }

    // Mismo orden y mismos grupos que el #menu de pages/config.html, para que
    // el operario que ya sabia donde estaba cada cosa la siga encontrando ahi.
    private static readonly Mod[] MODS =
    {
        // ---- Modulos ------------------------------------------------------
        new Mod { Grupo = "Módulos", Titulo = "Hub",      Clave = "hub",
                  Desc = "Widgets sobre el mapa y accesos" },
        new Mod { Grupo = "Módulos", Titulo = "QuantiX",  Clave = "quantix",
                  Desc = "Dosis de siembra, motores y PID" },
        new Mod { Grupo = "Módulos", Titulo = "VistaX",   Clave = "vistax",
                  Desc = "Monitoreo de semillas por línea" },
        new Mod { Grupo = "Módulos", Titulo = "FlowX",    Clave = "flowx",
                  Desc = "Caudal y dosis líquida" },
        // SectionX nativo es live-only (grilla de secciones + estado del
        // bridge). El mapeo surco->seccion y el debug MQTT siguen en HTML,
        // pero se llega desde el propio panel con "Configurar": la puerta de
        // aca sigue siendo la nativa, que es la que el operario mira en labor.
        new Mod { Grupo = "Módulos", Titulo = "SectionX", Clave = "sectionx",
                  Desc = "Corte de secciones en vivo" },
        new Mod { Grupo = "Módulos", Titulo = "StormX",   Clave = "stormx",
                  Desc = "Estación meteo del lote" },
        // LineX no tiene panel nativo: se dice asi, no se disfraza.
        new Mod { Grupo = "Módulos", Titulo = "LineX",    Ruta = "pages/linex.html",
                  Desc = "Corte surco por surco" },
        new Mod { Grupo = "Módulos", Titulo = "CoreX-ECU", Clave = "corex_ecu",
                  Desc = "ECU de dirección: WAS, motor y sensores" },
        new Mod { Grupo = "Módulos", Titulo = "Nodos",    Clave = "nodos",
                  Desc = "Los nodos que aparecieron por MQTT" },
        new Mod { Grupo = "Módulos", Titulo = "Cámaras",  Clave = "camaras",
                  Desc = "Cámaras de la máquina" },

        // ---- Campo --------------------------------------------------------
        new Mod { Grupo = "Campo", Titulo = "Insumos",  Ruta = "pages/insumos.html",
                  Desc = "Catálogo de semillas y fertilizantes" },
        new Mod { Grupo = "Campo", Titulo = "Mapas",    Ruta = "pages/mapas.html",
                  Desc = "Mapas y capas del lote" },
        // "Prescripciones" es la tab Shape del editor de QuantiX (el viejo
        // quantix.html?tab=shape), que ya es nativa.
        new Mod { Grupo = "Campo", Titulo = "Prescripciones", Clave = "prescripciones",
                  Desc = "Shape de dosis variable" },

        // ---- Herramientas -------------------------------------------------
        new Mod { Grupo = "Herramientas", Titulo = "Calculadora", Ruta = "pages/calculadora-siembra.html",
                  Desc = "Cuentas de siembra" },
        new Mod { Grupo = "Herramientas", Titulo = "Lab PID",     Ruta = "pages/pid-lab.html",
                  Desc = "Ajuste fino del PID" },
        new Mod { Grupo = "Herramientas", Titulo = "Diagnóstico PWM", Ruta = "pages/pwm-diag.html",
                  Desc = "Banco de pruebas del actuador" },

        // ---- Cloud --------------------------------------------------------
        new Mod { Grupo = "Cloud", Titulo = "OrbitX",     Ruta = "pages/orbitx.html",
                  Desc = "Cuenta y sincronización con la nube" },
        new Mod { Grupo = "Cloud", Titulo = "Firmwares",  Ruta = "pages/firmwares.html",
                  Desc = "Firmwares de los nodos (OTA)" },
        new Mod { Grupo = "Cloud", Titulo = "Actualizar", Clave = "actualizar",
                  Desc = "Actualizar PilotX" },
        new Mod { Grupo = "Cloud", Titulo = "Conectar celular", Ruta = "pages/pwa-qr.html",
                  Desc = "QR para abrir PilotX en el teléfono" },

        // ---- Mantenimiento -------------------------------------------------
        new Mod { Grupo = "Mantenimiento", Titulo = "Red WiFi", Ruta = "pages/wifi.html",
                  Desc = "Red de la pantalla" },
        new Mod { Grupo = "Mantenimiento", Titulo = "Sistema",  Clave = "sistema",
                  Desc = "Estado del equipo y servicios" },
        new Mod { Grupo = "Mantenimiento", Titulo = "Sonidos",  Clave = "sonidos",
                  Desc = "Alarmas de cabina" },
        new Mod { Grupo = "Mantenimiento", Titulo = "Eventos",  Ruta = "pages/eventos.html",
                  Desc = "Historial de lo que pasó" },
        new Mod { Grupo = "Mantenimiento", Titulo = "Debug",    Ruta = "pages/debug.html",
                  Desc = "Datos crudos para soporte" },
        new Mod { Grupo = "Mantenimiento", Titulo = "Ayuda",    Ruta = "pages/ayuda.html",
                  Desc = "Manual de cabina" },
    };

    public ModulosTab(CfgCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();

        bool hayWeb = PilotX.Desktop.App.WebViewHost != null;

        var cabecera = new StackPanel { Spacing = 6 };
        cabecera.Children.Add(CfgUi.Titulo("Módulos"));
        cabecera.Children.Add(CfgUi.Nota(hayWeb
            ? "Los productos X-*, el Hub y el mantenimiento del equipo. Los que ya son pantalla propia de PilotX se abren enteros; el resto se abre acá adentro, sin salir de la Configuración."
            : "Este equipo no tiene el navegador embebido: solo se pueden abrir los módulos que ya son pantalla propia de PilotX. Los demás quedan apagados."));
        Children.Add(CfgUi.Carta(cabecera));

        string grupoActual = "";
        WrapPanel? grilla = null;

        foreach (var m in MODS)
        {
            if (m.Grupo != grupoActual)
            {
                grupoActual = m.Grupo;
                var sp = new StackPanel { Spacing = 8 };
                sp.Children.Add(CfgUi.Etiqueta(grupoActual));
                // MaxWidth fijo a propósito: el ScrollViewer de la tarjeta
                // mide con ancho INFINITO (tiene scroll horizontal en Auto),
                // así que un WrapPanel sin tope no envuelve nunca y las 10
                // fichas de "Módulos" salían en una sola fila con scroll.
                // 3 columnas de 200 + 8 de margen = 624.
                grilla = CfgUi.Grilla();
                grilla.MaxWidth = 640;
                sp.Children.Add(grilla);
                Children.Add(CfgUi.Carta(sp));
            }
            grilla!.Children.Add(Tarjeta(m, hayWeb));
        }
    }

    /// <summary>Un boton tactil de modulo. Alto 74 y ancho 200: entra el dedo
    /// con guante y entran tres por fila en la tarjeta de 940 px.</summary>
    private Button Tarjeta(Mod m, bool hayWeb)
    {
        bool nativo = m.Clave != null;
        bool habilitado = nativo || hayWeb;

        var txt = new StackPanel { Spacing = 2 };
        txt.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(m.Titulo),
            Foreground = habilitado ? CfgUi.Texto : CfgUi.Dim,
            FontSize = 14, FontWeight = FontWeight.Bold,
        });
        txt.Children.Add(new TextBlock
        {
            Text = habilitado
                ? PilotX.Cockpit.Bars.Traductor.T(m.Desc)
                : PilotX.Cockpit.Bars.Traductor.T("Necesita el navegador embebido"),
            Foreground = habilitado ? CfgUi.TextoMuted : CfgUi.Dim,
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });

        var b = new Button
        {
            Content = txt,
            Width = 200, MinHeight = 74,
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 8, 8),
            Background = habilitado ? CfgUi.BgFila : CfgUi.BgSuave,
            BorderBrush = CfgUi.Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = habilitado,
            Cursor = new Cursor(habilitado ? StandardCursorType.Hand : StandardCursorType.Arrow),
        };

        if (nativo)
        {
            string clave = m.Clave!;
            b.Click += (_, __) => C.AbrirPanelNativo?.Invoke(clave);
        }
        else if (hayWeb)
        {
            string ruta = m.Ruta!;
            string titulo = m.Titulo;
            b.Click += (_, __) => C.AbrirHtmlEmbebido?.Invoke(ruta, titulo);
        }
        return b;
    }
}
