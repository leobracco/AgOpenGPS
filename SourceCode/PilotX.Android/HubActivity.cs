// ============================================================================
// HubActivity.cs
// Shell del Hub (Fase 1): WebView pantalla completa contra 127.0.0.1:5180.
// Era el MainActivity original (WebView del Hub HTML); desde S1 del port Android
// el launcher es el mapa NATIVO Avalonia (MainActivity : AvaloniaMainActivity).
// Se conserva como activity secundaria (MainLauncher=false) para poder abrir el
// Hub HTML mientras se portan pantallas a nativo. El engine + broker los arranca
// HubForegroundService igual que antes.
// ============================================================================

using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Webkit;

namespace PilotX.Droid
{
    [Activity(
        Label = "PilotX Hub",
        MainLauncher = false,
        Exported = false,
        Theme = "@android:style/Theme.Material.Light.NoActionBar",
        ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation
                             | Android.Content.PM.ConfigChanges.ScreenSize
                             | Android.Content.PM.ConfigChanges.KeyboardHidden,
        ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape)]
    public class HubActivity : Activity
    {
        private WebView _webView;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // El broker + WebHost ya corren en HubForegroundService (arrancado por
            // MainActivity). Por si se abre esta activity directo, asegurarlo.
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

            Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            LoadWhenReady();
        }

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
                var url = request?.Url?.ToString() ?? "";
                if (url.StartsWith("http://127.0.0.1") || url.StartsWith("http://localhost"))
                    return false;
                return true;
            }
        }
    }
}
