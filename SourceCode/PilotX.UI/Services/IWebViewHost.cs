// IWebViewHost.cs — abstracción del WebView por plataforma.
//
// PilotX.UI (net9.0 puro) NO depende de ningún paquete WebView: el WebView es
// lo único no portable del shell (WebView.Avalonia es solo-Desktop; en Android
// hay que usar el WebView nativo de Android). Cada "head" inyecta su
// implementación vía App.WebViewHost antes de arrancar Avalonia:
//   · PilotX.Desktop  → DesktopWebViewHost (WebView.Avalonia)
//   · PilotX.Android.App → AndroidWebViewHost (Android WebView)  [carril Santiago]
//
// Si App.WebViewHost es null (build sin backend web), las pantallas HTML aún no
// portadas a nativo simplemente no abren — el mapa y las pantallas nativas
// siguen funcionando. Ese es el contrato congelado UI↔plataforma para el port
// Android (ver COORDINACION-SESIONES.md / COORDINACION-UI.md).

using System;
using Avalonia.Controls;

namespace PilotX.Desktop.Services
{
    /// <summary>
    /// Fábrica de web views específica de plataforma. La UI compartida solo
    /// conoce esta interfaz; la implementación concreta la provee el head.
    /// </summary>
    public interface IWebViewHost
    {
        /// <summary>
        /// Crea un web view. <paramref name="onNavigated"/> se dispara con la
        /// URL final cada vez que termina de cargar una página (lo usa el
        /// centinela <c>pilotx-close</c> para cerrar diálogos y el log de
        /// cold-start).
        /// </summary>
        IWebViewHandle Create(Action<string> onNavigated);
    }

    /// <summary>
    /// Handle de un web view concreto. Envuelve el control de plataforma para
    /// que la UI compartida lo monte (en un slot <see cref="Panel"/> o como
    /// <c>Content</c> de una <see cref="Window"/>) sin conocer su tipo real.
    /// </summary>
    public interface IWebViewHandle
    {
        /// <summary>Control Avalonia a montar en el árbol visual.</summary>
        Control Control { get; }

        /// <summary>Navega a la URL indicada.</summary>
        void Navigate(string url);

        /// <summary>
        /// Vacía la página (navega a about:blank) PERO deja el handle vivo y
        /// reutilizable: los eventos siguen enganchados y el proceso del web view
        /// queda listo para la próxima apertura.
        ///
        /// Es lo que se usa al cerrar una pantalla. Destruir el web view en cada
        /// cierre obligaba a levantar todo el motor de nuevo en la apertura
        /// siguiente, que es de lejos el costo más caro del ciclo.
        /// </summary>
        void Blank();

        /// <summary>
        /// Libera el contenido (navega a about:blank y desengancha eventos).
        /// Tras llamarlo, el handle no se reutiliza. Solo para teardown real.
        /// </summary>
        void Release();

        /// <summary>
        /// Abre las herramientas de desarrollo del web view (F12). Hook de
        /// diagnóstico opcional: en plataformas que no lo soporten es un no-op.
        /// </summary>
        void OpenDevTools();
    }
}
