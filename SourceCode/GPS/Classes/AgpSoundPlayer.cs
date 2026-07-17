using System.IO;

namespace AgOpenGPS
{
    /// <summary>
    /// Wrapper de audio: único punto de Classes/ que toca System.Media
    /// (Windows-only). En un port a Linux/Android se reimplementa esta clase
    /// (NAudio / OpenAL / MediaPlayer) sin tocar a los consumidores, que solo
    /// conocen Play(). (traspaso portabilidad 2026-07-16)
    /// </summary>
    public class AgpSoundPlayer
    {
        private readonly System.Media.SoundPlayer player;

        public AgpSoundPlayer(Stream wav)
        {
            try { player = new System.Media.SoundPlayer(wav); }
            catch { player = null; }
        }

        public void Play()
        {
            try { player?.Play(); }
            catch { /* audio best-effort: sin sonido no se frena el guiado */ }
        }
    }
}
