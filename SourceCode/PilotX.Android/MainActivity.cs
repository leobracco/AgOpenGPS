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
// GPS real: todavía sin fuente (necesita CoreX por USB-OTG, bloque 8). El mapa
// renderiza contra el engine igual; con un fix real (o el sim) se mueve el tractor.
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

            base.OnCreate(savedInstanceState);

            // Monitor de cabina: pantalla siempre encendida.
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            HideSystemBars();
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
