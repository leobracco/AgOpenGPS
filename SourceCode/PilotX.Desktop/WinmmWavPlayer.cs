// ============================================================================
// WinmmWavPlayer.cs — sink de audio Windows para las alarmas sonoras.
//
// PlaySound de winmm con SND_MEMORY: toca un WAV desde memoria, async, sin
// paquetes ni FrameworkReference (PilotX.UI es net9.0 portable y no puede
// usar System.Media; el head Windows lo resuelve con un P/Invoke de 10 líneas).
//
// El buffer se retiene en un campo estático mientras suena: PlaySound ASYNC
// lee la memoria después de retornar, y si el GC la levanta se corta el
// sonido (o algo peor). Un sonido nuevo reemplaza al anterior (SND_ASYNC
// interrumpe), que para alarmas de cabina es lo correcto: manda la última.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace PilotX.Desktop;

public static class WinmmWavPlayer
{
    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] pszSound, IntPtr hmod, uint fdwSound);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_NODEFAULT = 0x0002;

    // Retener el buffer en uso (ver header).
    private static byte[]? _sonando;

    public static void Play(byte[] wav)
    {
        if (wav == null || wav.Length < 44) return;
        try
        {
            _sonando = wav;
            PlaySound(_sonando, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
        }
        catch { /* audio best-effort: sin sonido no se frena nada */ }
    }
}
