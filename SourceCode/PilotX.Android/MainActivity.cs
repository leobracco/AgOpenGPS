// ============================================================================
// MainActivity.cs — head Avalonia del mapa NATIVO en Android (S1 del port).
//
// Reemplaza el WebView del Hub (ahora HubActivity, secundaria) por la UI nativa
// Avalonia compartida: hostea PilotX.Desktop.App, que en el single-view lifetime
// de Android monta PilotX.Desktop.Views.MainView (mapa GL + barras del cockpit +
// pollers live contra 127.0.0.1:5180).
//
// El engine + WebHost + broker corren in-process en HubForegroundService (mismo
// que la Fase 1), arrancado acá antes de la UI. MainView poolea :5180; sus
// pollers reintentan mientras el WebHost termina de levantar (~1-2 s).
//
// GPS real: por WiFi/UDP (NMEA crudo o PGN) vía el bridge LAN de HubBootstrap —
// mismo camino que un CoreX-ECU o un receptor de red. USB-OTG queda para después.
// ============================================================================

using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;

namespace PilotX.Droid
{
    [Activity(
        Label = "PilotX",
        MainLauncher = true,
        Theme = "@style/PilotXTheme",
        ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation
                             | Android.Content.PM.ConfigChanges.ScreenSize
                             | Android.Content.PM.ConfigChanges.KeyboardHidden
                             | Android.Content.PM.ConfigChanges.UiMode,
        ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape)]
    public class MainActivity : AvaloniaMainActivity<PilotX.Desktop.App>
    {
        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
                .WithInterFont();
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            // Engine + WebHost + broker (:5180) en foreground ANTES de la UI. Los
            // pollers de MainView reintentan hasta que responda, así no importa
            // que la Activity arranque unos ms antes que el WebHost.
            var svc = new Intent(this, typeof(HubForegroundService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                StartForegroundService(svc);
            else
                StartService(svc);

            // WebView nativo para las pantallas HTML del Hub (config/Dirección/
            // CoreX/lote/…). Debe quedar seteado ANTES de que Avalonia monte la UI.
            PilotX.Desktop.App.WebViewHost = new AndroidWebViewHost(this);

            // Plataforma (gemelos de lo que Program.cs del Desktop cablea):
            // alarmas de cabina por MediaPlayer y brillo de ventana para
            // AndroidSistemaService. ANTES de la UI: MainWindow arranca el
            // SoundAlarmPoller al montarse.
            AndroidWavPlayer.Init(this);
            PilotX.Desktop.Services.SoundAlarmPoller.WavSink = AndroidWavPlayer.Play;
            AndroidSistemaService.Activity = this;

            base.OnCreate(savedInstanceState);

            // Monitor de cabina: pantalla siempre encendida.
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            HideSystemBars();
            IniciarKiosko();
        }

        /// <summary>
        /// Modo kiosko (lock task): el operario no puede salir de PilotX con
        /// Home/Recientes. Sólo funciona si PilotX es device-owner o el
        /// administrador lo permitió (DevicePolicyManager.SetLockTaskPackages);
        /// si no, Android muestra el diálogo de "fijar pantalla" o lo ignora.
        /// Se intenta siempre y se loguea el resultado — nunca rompe el arranque.
        /// </summary>
        private void IniciarKiosko()
        {
            try
            {
                var dpm = (Android.App.Admin.DevicePolicyManager)GetSystemService(DevicePolicyService);
                if (dpm != null && dpm.IsLockTaskPermitted(PackageName))
                {
                    StartLockTask();
                    Android.Util.Log.Info("PilotX", "Kiosko: lock task activo");
                }
                else
                {
                    Android.Util.Log.Info("PilotX", "Kiosko: sin permiso de lock task (no es device-owner) — modo inmersivo solamente");
                }
            }
            catch (System.Exception ex) { Android.Util.Log.Warn("PilotX", "Kiosko: " + ex.Message); }
        }

        protected override void OnDestroy()
        {
            if (ReferenceEquals(AndroidSistemaService.Activity, this)) AndroidSistemaService.Activity = null;
            base.OnDestroy();
        }

        // El theme ya pide windowFullscreen, pero eso solo no basta: la barra de
        // estado seguía dibujándose ENCIMA del contenido (tapaba el marco
        // superior de BarraSuperior) en vez de ocultarse. En API 30+ (Android 11+)
        // las banderas viejas de SystemUiVisibility están deprecadas y muchos
        // fabricantes ya las ignoran silenciosamente — hace falta el
        // WindowInsetsController nativo. Se mantiene el fallback viejo para
        // API 28/29 (el mínimo del proyecto).
        private void HideSystemBars()
        {
            if (Window == null) return;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                Window.SetDecorFitsSystemWindows(false);
                var controller = Window.InsetsController;
                if (controller != null)
                {
                    controller.Hide(WindowInsets.Type.SystemBars());
                    controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                }
            }
            else
            {
                if (Window.DecorView == null) return;
#pragma warning disable CA1422 // SystemUiVisibility obsoleto desde API 30 — rama explícita para API < 30
                Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                    SystemUiFlags.LayoutStable
                    | SystemUiFlags.LayoutHideNavigation
                    | SystemUiFlags.LayoutFullscreen
                    | SystemUiFlags.HideNavigation
                    | SystemUiFlags.Fullscreen
                    | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CA1422
            }
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            // El sistema puede sacar el modo inmersivo (ej. al volver de otra
            // app); reaplicar cuando la ventana vuelve a tener foco.
            if (hasFocus) HideSystemBars();
        }
    }
}
