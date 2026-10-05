// ============================================================================
// LinuxWavPlayer.cs — sink de audio Linux para las alarmas sonoras.
//
// Contracara de WinmmWavPlayer: mismo contrato (Play(rutaWav)), pero toca el
// WAV con el reproductor de línea de comandos que haya en el sistema. Se
// prueba una vez en orden paplay (PulseAudio/PipeWire, lo normal en un
// escritorio) → aplay (ALSA pelado) y se cachea el que anduvo; si no hay
// ninguno, las alarmas quedan mudas y se avisa UNA sola vez por stderr —
// mismo criterio que el resto del head: degradar sin romper la cabina.
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;

namespace PilotX.Desktop
{
    public static class LinuxWavPlayer
    {
        private static string _player;          // exe elegido; "" = no hay
        private static readonly object _lock = new object();

        /// <summary>Mismo contrato que WinmmWavPlayer.Play: recibe el WAV en
        /// bytes (lo entrega el poller), lo baja a un temp y lo toca.</summary>
        public static void Play(byte[] wav)
        {
            if (wav == null || wav.Length == 0) return;

            string player = ResolverPlayer();
            if (player.Length == 0) return;

            string rutaWav;
            try
            {
                rutaWav = Path.Combine(Path.GetTempPath(), "pilotx-alarma.wav");
                File.WriteAllBytes(rutaWav, wav);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[LinuxWavPlayer] temp: " + ex.Message);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(player, "\"" + rutaWav + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // Fire-and-forget: la alarma no puede bloquear el hilo del poller.
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[LinuxWavPlayer] " + player + ": " + ex.Message);
            }
        }

        private static string ResolverPlayer()
        {
            if (_player != null) return _player;
            lock (_lock)
            {
                if (_player != null) return _player;
                foreach (var cand in new[] { "paplay", "aplay" })
                {
                    try
                    {
                        var psi = new ProcessStartInfo("which", cand)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        using var p = Process.Start(psi);
                        p.WaitForExit(2000);
                        if (p.ExitCode == 0) { _player = cand; return _player; }
                    }
                    catch { }
                }
                Console.Error.WriteLine(
                    "[LinuxWavPlayer] sin paplay ni aplay — alarmas sonoras mudas");
                _player = "";
                return _player;
            }
        }
    }
}
