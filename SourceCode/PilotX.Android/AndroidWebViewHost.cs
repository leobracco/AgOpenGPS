// ============================================================================
// AndroidWebViewHost.cs — implementación Android de IWebViewHost.
//
// Es el head Android del contrato UI↔plataforma (ver IWebViewHost.cs): embebe
// el WebView NATIVO de Android (Android.Webkit.WebView) dentro del árbol visual
// de Avalonia usando un NativeControlHost, para que las pantallas HTML del Hub
// (config, Dirección/FormSteer, CoreX, lote, etc.) abran igual que en Desktop.
//
// En Desktop la implementación es WebView.Avalonia (Chromium, solo-Desktop). En
// Android no hay control Avalonia de WebView, así que se embebe el nativo.
//
// [Se toma este carril desde la sesión taller — avisado en COORDINACION-SESIONES.md]
// ============================================================================

#nullable enable
using System;
using Android.Webkit;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using AWebView = Android.Webkit.WebView;

namespace PilotX.Droid
{
    /// <summary>
    /// Control Avalonia que embebe un <see cref="AWebView"/> nativo. El WebView
    /// recién existe cuando el control se adjunta al árbol visual
    /// (CreateNativeControlCore); las navegaciones previas quedan pendientes y se
    /// aplican al crearse.
    /// </summary>
    internal sealed class AndroidWebViewControl : NativeControlHost
    {
        private readonly global::Android.Content.Context _context;
        private readonly Action<string>? _onNavigated;
        private string? _pendingUrl;

        public AWebView? Web { get; private set; }

        public AndroidWebViewControl(global::Android.Content.Context ctx, Action<string>? onNavigated)
        {
            _context = ctx;
            _onNavigated = onNavigated;
        }

        public void Load(string url)
        {
            if (Web != null) Web.LoadUrl(url);
            else _pendingUrl = url;   // aún no adjunto: queda pendiente
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var web = new AWebView(_context);
            var s = web.Settings;
            s.JavaScriptEnabled = true;
            s.DomStorageEnabled = true;
            s.MediaPlaybackRequiresUserGesture = false;
            // Depuración remota (chrome://inspect) solo en debug.
#if DEBUG
            AWebView.SetWebContentsDebuggingEnabled(true);
#endif
            web.SetWebViewClient(new NavClient(_onNavigated));
            Web = web;

            if (_pendingUrl != null)
            {
                web.LoadUrl(_pendingUrl);
                _pendingUrl = null;
            }

            return new AndroidViewControlHandle(web);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            try { Web?.StopLoading(); Web?.Destroy(); } catch { /* best-effort */ }
            Web = null;
            base.DestroyNativeControlCore(control);
        }

        private sealed class NavClient : WebViewClient
        {
            private readonly Action<string>? _cb;
            public NavClient(Action<string>? cb) { _cb = cb; }

            public override void OnPageFinished(AWebView? view, string? url)
            {
                base.OnPageFinished(view, url);
                if (!string.IsNullOrEmpty(url)) _cb?.Invoke(url);
            }
        }
    }

    /// <summary>Fábrica Android de web views (IWebViewHost).</summary>
    public sealed class AndroidWebViewHost : PilotX.Desktop.Services.IWebViewHost
    {
        private readonly global::Android.Content.Context _context;

        public AndroidWebViewHost(global::Android.Content.Context ctx) => _context = ctx;

        public PilotX.Desktop.Services.IWebViewHandle Create(Action<string> onNavigated)
            => new Handle(_context, onNavigated);

        private sealed class Handle : PilotX.Desktop.Services.IWebViewHandle
        {
            private readonly AndroidWebViewControl _ctrl;

            public Handle(global::Android.Content.Context ctx, Action<string> onNavigated)
                => _ctrl = new AndroidWebViewControl(ctx, onNavigated);

            public Control Control => _ctrl;
            public void Navigate(string url) => _ctrl.Load(url);
            public void Release() { try { _ctrl.Load("about:blank"); } catch { /* best-effort */ } }
            public void OpenDevTools() { /* no-op en Android (usar chrome://inspect en debug) */ }
        }
    }
}
