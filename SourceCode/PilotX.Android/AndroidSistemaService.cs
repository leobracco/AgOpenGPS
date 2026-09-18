// ============================================================================
// AndroidSistemaService.cs — ISistemaService para la tablet (brillo + energía).
//
// Gemelo de EngineSistemaService (Windows: DDC/CI + WMI) y
// EngineSistemaServiceLinux (sysfs). Reemplaza al StubSistemaService de la
// Fase 1, que devolvía brillo -1 y no hacía nada con la energía: la pantalla
// de Sistema del Hub y los botones Brillo +/− del menú quedaban muertos.
//
// Brillo, dos niveles:
//   · Ventana de PilotX (WindowManager.LayoutParams.ScreenBrightness): no
//     necesita permiso y alcanza para la cabina (PilotX es la única app).
//   · Sistema (Settings.System.SCREEN_BRIGHTNESS): sólo si el usuario dio
//     WRITE_SETTINGS (una vez, desde ACTION_MANAGE_WRITE_SETTINGS). Con eso el
//     brillo persiste aunque PilotX se cierre.
// Energía: Android no deja apagar ni reiniciar sin ser device-owner. Si la
// app es device-owner (provisionada con MDM/adb) se usa DevicePolicyManager.
// Reboot; si no, se loguea y no pasa nada. ExitApp cierra la app de verdad.
// ============================================================================

#nullable enable
using System;
using Android.App;
using Android.App.Admin;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using AgroParallel.Services.Abstractions;

namespace PilotX.Droid
{
    internal sealed class AndroidSistemaService : ISistemaService
    {
        private readonly Context _ctx;

        /// <summary>Activity viva para el brillo de ventana. La setea
        /// MainActivity en OnCreate (y la limpia al destruirse).</summary>
        public static Activity? Activity;

        /// <summary>Último porcentaje aplicado a la ventana (-1 = nunca).</summary>
        private static int s_brilloVentana = -1;

        public AndroidSistemaService(Context ctx) { _ctx = ctx; }

        public int GetBrightness()
        {
            // Primero el sistema (si se puede leer), después lo que aplicamos
            // a la ventana. Settings.System se puede leer sin permiso.
            try
            {
                int raw = Settings.System.GetInt(_ctx.ContentResolver, Settings.System.ScreenBrightness);
                int pct = (int)Math.Round(raw * 100.0 / 255.0);
                if (s_brilloVentana >= 0) return s_brilloVentana;
                return Math.Max(0, Math.Min(100, pct));
            }
            catch
            {
                return s_brilloVentana;
            }
        }

        public bool SetBrightness(int percent)
        {
            percent = Math.Max(5, Math.Min(100, percent)); // 0 = pantalla negra en cabina: no
            bool ok = false;

            // 1) Ventana de PilotX — siempre, en el hilo de UI.
            var act = Activity;
            if (act != null)
            {
                try
                {
                    act.RunOnUiThread(() =>
                    {
                        try
                        {
                            var w = act.Window;
                            if (w == null) return;
                            var lp = w.Attributes;
                            if (lp == null) return;
                            lp.ScreenBrightness = percent / 100f;
                            w.Attributes = lp;
                        }
                        catch { /* best-effort */ }
                    });
                    s_brilloVentana = percent;
                    ok = true;
                }
                catch { /* best-effort */ }
            }

            // 2) Sistema — sólo con WRITE_SETTINGS concedido.
            try
            {
                bool puede = Build.VERSION.SdkInt < BuildVersionCodes.M || Settings.System.CanWrite(_ctx);
                if (puede)
                {
                    Settings.System.PutInt(_ctx.ContentResolver, Settings.System.ScreenBrightnessMode,
                        (int)ScreenBrightness.ModeManual);
                    Settings.System.PutInt(_ctx.ContentResolver, Settings.System.ScreenBrightness,
                        (int)Math.Round(percent * 255.0 / 100.0));
                    ok = true;
                }
                else
                {
                    Android.Util.Log.Info("PilotX", "Brillo: sin WRITE_SETTINGS — sólo ventana. Autorizar en Ajustes › Apps › PilotX › Modificar ajustes del sistema.");
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("PilotX", "Brillo sistema: " + ex.Message);
            }
            return ok;
        }

        public void ExecutePowerAction(PowerAction action)
        {
            switch (action)
            {
                case PowerAction.ExitApp:
                    try { HubBootstrap.Stop(); } catch { }
                    try { Activity?.FinishAffinity(); } catch { }
                    try { Java.Lang.JavaSystem.Exit(0); } catch { }
                    break;

                case PowerAction.Restart:
                case PowerAction.Shutdown:
                    // Sólo device-owner puede reiniciar (API 24+). Apagar no existe
                    // ni para device-owner: se trata como reinicio.
                    try
                    {
                        var dpm = (DevicePolicyManager?)_ctx.GetSystemService(Context.DevicePolicyService);
                        if (dpm != null && dpm.IsDeviceOwnerApp(_ctx.PackageName))
                        {
                            var admin = new ComponentName(_ctx, Java.Lang.Class.FromType(typeof(PilotXDeviceAdmin)));
                            dpm.Reboot(admin);
                            return;
                        }
                        Android.Util.Log.Warn("PilotX", "Energía: " + action + " requiere que PilotX sea device-owner (provisionar con adb dpm set-device-owner).");
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn("PilotX", "Energía " + action + ": " + ex.Message);
                    }
                    break;

                case PowerAction.Suspend:
                case PowerAction.LogOff:
                default:
                    Android.Util.Log.Info("PilotX", "Energía: " + action + " no aplica en Android.");
                    break;
            }
        }
    }

    /// <summary>Receiver de administración de dispositivo: necesario como
    /// ComponentName para DevicePolicyManager.Reboot cuando PilotX es
    /// device-owner. Sin provisionar no hace nada.</summary>
    [BroadcastReceiver(Permission = "android.permission.BIND_DEVICE_ADMIN", Exported = true)]
    [MetaData("android.app.device_admin", Resource = "@xml/device_admin")]
    [IntentFilter(new[] { "android.app.action.DEVICE_ADMIN_ENABLED" })]
    public sealed class PilotXDeviceAdmin : DeviceAdminReceiver
    {
    }
}
