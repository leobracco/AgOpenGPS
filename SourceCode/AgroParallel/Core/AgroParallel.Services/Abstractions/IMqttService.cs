// IMqttService — fachada del cliente MQTT compartido para todos los
// servicios AgroParallel (QuantiX/SectionX/VistaX/etc). Pool de clientes
// vs un cliente por bridge — ahorra conexiones y unifica logs.

using System;
using System.Threading.Tasks;

namespace AgroParallel.Services.Abstractions
{
    public interface IMqttService
    {
        bool IsConnected { get; }

        Task<bool> ConnectAsync(string brokerAddress, int brokerPort, string clientId);
        void Disconnect();

        Task PublishAsync(string topic, string payload, bool atLeastOnce = false);

        /// <summary>Suscribirse a un topic. El handler se invoca por cada mensaje recibido.</summary>
        IDisposable Subscribe(string topicFilter, Action<string, string> onMessage);
    }
}
