// ============================================================================
// AndroidWavPlayer.cs — sink de audio para las alarmas de cabina.
//
// Gemelo de WinmmWavPlayer (Windows) / LinuxWavPlayer: SoundAlarmPoller
// (PilotX.UI) baja el .wav de /api/sonidos/... y lo entrega como byte[] a
// SoundAlarmPoller.WavSink; acá se reproduce con MediaPlayer. El wav se
// escribe a un archivo de cache porque MediaPlayer no toma un stream en
// memoria. Un player por disparo, liberado al terminar.
// ============================================================================

#nullable enable
using System;
using System.IO;
using Android.Content;
using Android.Media;

namespace PilotX.Droid
{
    internal static class AndroidWavPlayer
    {
        private static Context? s_ctx;
        private static int s_seq;

        public static void Init(Context ctx) => s_ctx = ctx.ApplicationContext;

        public static void Play(byte[] wav)
        {
            var ctx = s_ctx;
            if (ctx == null || wav == null || wav.Length == 0) return;
            try
            {
                var dir = ctx.CacheDir?.AbsolutePath;
                if (dir == null) return;
                var path = Path.Combine(dir, "alarma-" + (System.Threading.Interlocked.Increment(ref s_seq) % 8) + ".wav");
                File.WriteAllBytes(path, wav);

                var mp = new MediaPlayer();
                mp.SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Alarm)
                    .SetContentType(AudioContentType.Sonification)
                    .Build());
                mp.SetDataSource(path);
                mp.Completion += (s, e) => { try { mp.Release(); } catch { } };
                mp.Error += (s, e) => { try { mp.Release(); } catch { } };
                mp.Prepare();
                mp.Start();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("PilotX", "Alarma audio: " + ex.Message);
            }
        }
    }
}
