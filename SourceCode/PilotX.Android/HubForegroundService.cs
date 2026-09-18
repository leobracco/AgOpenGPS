// ============================================================================
// HubForegroundService.cs
// Foreground Service que mantiene vivos el broker MQTT (:1883) y el WebHost
// (:5180) aunque la Activity se vaya a background — los nodos ESP32 de la
// LAN siguen conectados. En Android es proceso único (bloque 14 matriz:
// PilotX+CoreX juntos).
// ============================================================================

using Android.App;
using Android.Content;
using Android.OS;
using System.Threading.Tasks;

namespace PilotX.Droid
{
    [Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
    public class HubForegroundService : Service
    {
        private const int NotifId = 5180;
        private const string ChannelId = "pilotx_hub";

        public override void OnCreate()
        {
            base.OnCreate();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(ChannelId, "PilotX Hub", NotificationImportance.Low)
                {
                    Description = "Broker MQTT y Hub de PilotX"
                };
                ((NotificationManager)GetSystemService(NotificationService)).CreateNotificationChannel(channel);
            }
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            var notif = new Notification.Builder(this, ChannelId)
                .SetContentTitle("PilotX")
                .SetContentText("Hub y broker MQTT activos")
                .SetSmallIcon(Android.Resource.Drawable.StatSysDownloadDone)
                .SetOngoing(true)
                .Build();
            StartForeground(NotifId, notif);

            var ctx = ApplicationContext;
            Task.Run(() =>
            {
                try
                {
                    string wwwroot = HubBootstrap.ExtractWwwroot(ctx);
                    string dataDir = ctx.FilesDir.AbsolutePath;
                    string external = ctx.GetExternalFilesDir(null)?.AbsolutePath ?? dataDir;
                    HubBootstrap.Start(dataDir, wwwroot, external);
                    Android.Util.Log.Info("PilotX", "Hub arriba en " + (HubBootstrap.Url ?? "?"));
                }
                catch (System.Exception ex)
                {
                    Android.Util.Log.Error("PilotX", "Bootstrap: " + ex);
                }
            });

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            HubBootstrap.Stop();
            base.OnDestroy();
        }

        public override IBinder OnBind(Intent intent) => null;
    }
}
