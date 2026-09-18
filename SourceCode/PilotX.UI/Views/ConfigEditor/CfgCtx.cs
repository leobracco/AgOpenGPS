// ============================================================================
// CfgCtx.cs — estado compartido del ConfigPanel nativo: el snapshot en memoria
// (el `snap` de config.js), las conversiones de unidades y los enganches que
// las pestañas usan para hablarle al shell (estado, aviso, refresco, abrir el
// HTML de una pestaña que todavía no está portada).
//
// Unidades — RÉPLICA EXACTA de config.js / Units.cs, quirks incluidos:
//   · el wire viene SIEMPRE en metros con signo; la UI muestra cm|in enteros
//     (fmtSmall) o m|pies-pulgadas (fmtMedium) según is_metric;
//   · el factor de fmtSmall es 39.37, NO 39.3701 (39.3701 es el de m2disp,
//     que se usa para EDITAR). Si se "corrige", los números del panel dejan de
//     coincidir con los del HTML y con el FormConfig viejo, y entra un reporte
//     de "muestran distinto";
//   · fmtMedium imperial acarrea 12" a 1 pie;
//   · los signos NO se tocan: offset a la izquierda es negativo y un gap es
//     negativo. El signo es información, no un defecto visual;
//   · RedondeoJs replica Math.round de JavaScript (mitades hacia +infinito),
//     que NO es el Math.Round de .NET (banqueros) ni AwayFromZero: con
//     −10.5 cm el JS muestra −10 y AwayFromZero mostraría −11.
// ============================================================================

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.ConfigEditor;

public sealed class CfgCtx
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Cliente HTTP (uno solo para toda la config).</summary>
    public ConfigVehiculoClient? Client;

    /// <summary>Cliente del IMPLEMENTO CENTRAL (/api/implemento). Es OTRA
    /// configuración, no la del guiado: surcos, trenes y metadata de catálogo.
    /// Hoy lo usa nada más que la carta "Trenes de siembra" de la pestaña
    /// Secciones — el shell lo crea con la misma base que Client.</summary>
    public ImplementoClient? Implemento;

    /// <summary>Snapshot en memoria. null = el Hub no respondió.</summary>
    public ConfigSnapshot? Snap;

    /// <summary>true = respondió pero el servicio de configuración no está
    /// (ok:false). Es un caso DISTINTO de "sin conexión" y se muestra
    /// distinto, igual que en config.js.</summary>
    public bool ServicioCaido => Snap != null && !Snap.Ok;

    public bool SinDatos => Snap == null;

    /// <summary>true = el último refresco no respondió pero seguimos mostrando
    /// el snapshot anterior. NO es lo mismo que SinDatos: acá hay números en
    /// pantalla, y el operario tiene que saber que son los últimos leídos y no
    /// lo que el motor tiene AHORA (si alguien tocó el ancho en el celular
    /// mientras el Hub estaba caído, lo que se ve quedó viejo).</summary>
    public bool RefrescoCaido;

    public bool IsMetric => Snap?.IsMetric ?? true;

    // ---- enganches con el shell -------------------------------------------

    /// <summary>Mensaje del footer: (texto, "" | "ok" | "err").</summary>
    public Action<string, string>? Estado;

    /// <summary>Aviso corto → toast del host. NUNCA modal.</summary>
    public Action<string>? Aviso;

    /// <summary>Marca "hay cambios sin guardar" en el botón del shell.</summary>
    public Action? MarcarSucio;

    /// <summary>Vuelve a leer GET /api/aog/config y repinta header/footer.
    /// Las pestañas lo llaman después de guardar: hay guardados con efecto
    /// colateral (la cosechadora fuerza el implemento a frontal y puede dar
    /// vuelta el signo del enganche).</summary>
    public Func<CancellationToken, Task>? RefrescarSnapshot;

    /// <summary>Abre una página del Hub en el WebView (pestañas todavía no
    /// portadas y los módulos embebidos). Recibe la ruta relativa, p. ej.
    /// "pages/config.html?tab=roll".</summary>
    public Action<string>? AbrirHtml;

    /// <summary>Abre una página del Hub DENTRO de la tarjeta de Configuración
    /// (ruta relativa, título del encabezado): a pantalla completa el operario
    /// se queda sin ✕ y sin menú. Desde que los módulos son entradas directas
    /// del menú (2026-08-17) ninguna pestaña lo usa, pero queda cableado para
    /// la próxima pestaña que necesite mostrar una satélite HTML.</summary>
    public Action<string, string>? AbrirHtmlEmbebido;

    /// <summary>Pide abrir un PANEL NATIVO del cockpit por su clave
    /// ("hub", "quantix", "nodos", …). Lo resuelve MainWindow. Sin uso desde
    /// que los módulos se montan embebidos en la propia Configuración
    /// (2026-08-17); queda como puerta para caminos externos.</summary>
    public Action<string>? AbrirPanelNativo;

    /// <summary>
    /// El panel se está cerrando (Detach del shell). Lo tienen que mirar las
    /// pestañas que dejan algo corriendo de fondo ANTES de rearmarlo: el
    /// AlSalirAsync del cierre es fire-and-forget, así que si su guardado falla
    /// la pestaña reacciona con el panel ya oculto. Sin esta marca, el Rolido
    /// volvía a levantar su poll de 500 ms contra el motor y quedaba un timer
    /// huérfano hasta que alguien reabriera esa misma pestaña.
    /// </summary>
    public bool Cerrando;

    // ---- unidades ----------------------------------------------------------

    /// <summary>m → cm|in para EDITAR (factor 39.3701, como m2disp del JS).</summary>
    public double M2Disp(double m) => IsMetric ? m * 100.0 : m * 39.3701;

    /// <summary>cm|in → m (disp2m del JS).</summary>
    public double Disp2M(double v) => IsMetric ? v * 0.01 : v * 0.0254;

    public string Unidad() => IsMetric ? "cm" : "in";

    /// <summary>Math.round de JavaScript: las mitades van hacia +infinito.</summary>
    public static double RedondeoJs(double x) => Math.Floor(x + 0.5);

    /// <summary>cm enteros | pulgadas enteras (fmtSmall). null → "—".</summary>
    public string FmtSmall(double? m)
    {
        if (m == null || double.IsNaN(m.Value)) return "—";
        double v = IsMetric ? RedondeoJs(m.Value * 100.0) : RedondeoJs(m.Value * 39.37);
        return v.ToString("0", Inv) + (IsMetric ? " cm" : " in");
    }

    /// <summary>metros con 2 decimales | pies-pulgadas (fmtMedium). null → "—".</summary>
    public string FmtMedium(double? m)
    {
        if (m == null || double.IsNaN(m.Value)) return "—";
        if (IsMetric)
            return (RedondeoJs(m.Value * 100.0) / 100.0).ToString("0.00", Inv) + " m";
        double ft = m.Value * 3.28084;
        double f = Math.Floor(ft);
        double inch = RedondeoJs((ft - f) * 12.0);
        if (inch == 12) { f++; inch = 0; }
        return f.ToString("0", Inv) + "' " + inch.ToString("0", Inv) + "\"";
    }

    /// <summary>Número tal cual lo imprimiría JavaScript (1 → "1", 1.5 → "1.5").
    /// Lo usa el Lookahead del Resumen, que en el HTML es el valor crudo.</summary>
    public static string NumJs(double? v)
        => v == null || double.IsNaN(v.Value) ? "—" : v.Value.ToString("0.####", Inv);
}
