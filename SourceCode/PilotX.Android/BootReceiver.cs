// ============================================================================
// BootReceiver.cs — arranque automático de PilotX al bootear la tablet.
//
// Equivalente al kiosko/autostart de Windows (PilotX-KioskSetup): en cabina
// nadie toca la tablet, tiene que prender y mostrar el mapa. Android sólo
// entrega BOOT_COMPLETED a apps que el usuario abrió al menos una vez, y en
// API 29+ lanzar una Activity desde background exige que la app sea
// device-owner o tenga la excepción de "pantalla completa"; si no, al menos
// se levanta el Foreground Service (engine + broker) y los nodos ya tienen
// broker aunque la UI aparezca recién al tocar el ícono.
// ============================================================================

using Android.App;
using Android.Content;
using Android.OS;

namespace PilotX.Droid
{
    [BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = false)]
    [IntentFilter(new[] { Intent.ActionBootCompleted, "android.intent.action.QUICKBOOT_POWERON" })]
    public sealed class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            if (intent?.Action != Intent.ActionBootCompleted &&
                intent?.Action != "android.intent.action.QUICKBOOT_POWERON") return;

            try
            {
                var svc = new Intent(context, typeof(HubForegroundService));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(svc);
                else
                    context.StartService(svc);
            }
            catch (System.Exception ex) { Android.Util.Log.Warn("PilotX", "Boot service: " + ex.Message); }

            try
            {
                var ui = new Intent(context, typeof(MainActivity));
                ui.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
                context.StartActivity(ui);
            }
            catch (System.Exception ex) { Android.Util.Log.Warn("PilotX", "Boot UI: " + ex.Message); }
        }
    }
}
