// ISectionXService — bridge MQTT a los ESP32 SectionX (PCA9685 / relays).

namespace AgroParallel.Services.Abstractions
{
    public interface ISectionXService
    {
        bool IsRunning { get; }
        int MessagesSent { get; }

        void Start();
        void Stop();

        void ReloadConfig();
    }
}
