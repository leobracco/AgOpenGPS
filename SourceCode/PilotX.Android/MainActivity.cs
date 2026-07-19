// ============================================================================
// MainActivity.cs
// Shell del Hub: un WebView pantalla completa contra 127.0.0.1:5180.
// El WebHost + broker corren en HubForegroundService para sobrevivir a la
// Activity (bloque 7 matriz Android — Fase 1, Hub sin guiado).
// Las páginas usan window.chrome.webview con guardas try/catch, así que en
// Android (donde no existe) degradan solas; el puente JS nativo llega después.
// ============================================================================

using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Webkit;

namespace PilotX.Droid
{
    [Activity(
        Label = "PilotX",
        MainLauncher = true,
        Theme = "@android:style/Theme.Material.Light.NoActionBar",
        ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation
                             | Android.Content.PM.ConfigChanges.ScreenSize
                             | Android.Content.PM.ConfigChanges.KeyboardHidden,
        ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape)]
    public class MainActivity : Activity
    {
        private WebView _webView;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Servicio en foreground: broker + WebHost viven ahí.
            var svc = new Intent(this, typeof(HubForegroundService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                StartForegroundService(svc);
            else
                StartService(svc);

            _webView = new WebView(this);
            _webView.Settings.JavaScriptEnabled = true;
            _webView.Settings.DomStorageEnabled = true;
            _webView.Settings.MediaPlaybackRequiresUserGesture = false;
            _webView.SetWebViewClient(new HubWebViewClient());
            SetContentView(_webView);

            // Pantalla siempre encendida: es un monitor de cabina.
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            LoadWhenReady();
        }

        // El WebHost puede tardar 1-2 s en levantar; reintenta hasta que
        // responda en vez de mostrar el error de conexión del WebView.
        private void LoadWhenReady(int attempt = 0)
        {
            if (HubBootstrap.IsRunning || attempt >= 40)
            {
                _webView.LoadUrl("http://127.0.0.1:5180/");
                return;
            }
            new Handler(Looper.MainLooper).PostDelayed(() => LoadWhenReady(attempt + 1), 250);
        }

        public override void OnBackPressed()
        {
            if (_webView != null && _webView.CanGoBack()) _webView.GoBack();
            else base.OnBackPressed();
        }

        private sealed class HubWebViewClient : WebViewClient
        {
            public override bool ShouldOverrideUrlLoading(WebView view, IWebResourceRequest request)
            {
                // Todo el Hub es loopback; no abrir nada afuera.
                var url = request?.Url?.ToString() ?? "";
                if (url.StartsWith("http://127.0.0.1") || url.StartsWith("http://localhost"))
                    return false;
                return true;
            }
        }
    }
}
