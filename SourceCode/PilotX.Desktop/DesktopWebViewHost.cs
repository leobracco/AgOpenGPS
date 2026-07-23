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
