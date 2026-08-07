// DesktopWebViewHost.cs — backend Desktop de IWebViewHost (WebView.Avalonia).
//
// Es la ÚNICA parte del shell Desktop que conoce el paquete WebView: la UI
// compartida (PilotX.UI) solo ve la interfaz IWebViewHost. El head Android
// (PilotX.Android.App, carril Santiago) provee su equivalente AndroidWebViewHost
// con el WebView nativo de Android. Program.Main inyecta esta instancia en
// App.WebViewHost antes de arrancar Avalonia.

using System;
using Avalonia.Controls;
using AvaloniaWebView;
using PilotX.Desktop.Services;
using WebViewCore.Events;

namespace PilotX.Desktop
{
    public sealed class DesktopWebViewHost : IWebViewHost
    {
        public IWebViewHandle Create(Action<string> onNavigated) => new Handle(onNavigated);

        private sealed class Handle : IWebViewHandle
        {
            private readonly WebView _wv;
            private readonly Action<string> _onNavigated;

            public Handle(Action<string> onNavigated)
            {
                _onNavigated = onNavigated;
                _wv = new WebView();
                _wv.NavigationCompleted += OnNav;
            }

            public Control Control => _wv;

            public void Navigate(string url)
            {
                try { _wv.Url = new Uri(url); } catch { }
            }

            /// <summary>Vacía la página sin desenganchar nada: el handle sigue
            /// vivo y el proceso WebView2 queda caliente para la próxima.</summary>
            public void Blank()
            {
                try { _wv.Url = new Uri("about:blank"); } catch { }
            }

            public void Release()
            {
                try
                {
                    _wv.NavigationCompleted -= OnNav;
                    // about:blank libera el contenido y baja el working set del
                    // proceso WebView2 hijo antes de que el GC lo finalice.
                    _wv.Url = new Uri("about:blank");
                }
                catch { }
            }

            /// <summary>
            /// Baja el motor y sus procesos hijos. El orden importa: primero se
            /// desengancha el evento y se vacía la página (si se hace Dispose
            /// con una página cargada, el controller a veces se lleva puesto al
            /// hilo de UI), y recién después se libera el control.
            /// </summary>
            public void Destroy()
            {
                try { _wv.NavigationCompleted -= OnNav; } catch { }
                try { _wv.Url = new Uri("about:blank"); } catch { }

                // Quién baja realmente los procesos hijos es la REMOCIÓN del
                // control del árbol visual: WebView.Avalonia es un
                // NativeControlHost y Avalonia destruye el control nativo (y
                // con él el controller del motor) al desmontarlo. Eso lo hace
                // el llamador antes de esta línea.
                //
                // El control no implementa IDisposable, así que si alguna
                // versión del paquete agrega un Dispose propio lo usamos por
                // reflexión; hoy no existe y no pasa nada.
                try
                {
                    var d = _wv.GetType().GetMethod("Dispose", Type.EmptyTypes);
                    if (d != null) d.Invoke(_wv, null);
                }
                catch { }
            }

            public void OpenDevTools()
            {
                try { _wv.OpenDevToolsWindow(); } catch { }
            }

            private void OnNav(object? sender, WebViewUrlLoadedEventArg e)
            {
                var u = _wv.Url?.ToString() ?? string.Empty;
                _onNavigated?.Invoke(u);
            }
        }
    }
}
