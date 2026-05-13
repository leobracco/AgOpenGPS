// INodoRegistryService — descubre y mantiene una tabla de nodos AgroParallel
// vivos en la LAN MQTT. Reemplaza la lógica embebida en FormNodos.cs.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface INodoRegistryService
    {
        /// <summary>Conectar al broker y arrancar la suscripción a announcements + status_live.</summary>
        void Start(string brokerAddress, int brokerPort);

        /// <summary>Detener la suscripción y cerrar conexión.</summary>
        void Stop();

        /// <summary>Snapshot tipado del registro actual (ordenado: online primero).</summary>
        IReadOnlyList<NodoStatus> GetAll();

        /// <summary>Dispara cuando el registro cambia (alta, baja, transición online/offline).</summary>
        event EventHandler Changed;

        /// <summary>
        /// Publica un mensaje MQTT al broker reutilizando la conexión del registry.
        /// Retorna false si el cliente no está conectado.
        /// </summary>
        Task<bool> PublishAsync(string topic, string payload, bool retain);

        /// <summary>
        /// Suscribe un filtro adicional al cliente MQTT del registry (ej. wildcards
        /// "vistax/+/telemetria"). Idempotente para el mismo filtro.
        /// </summary>
        Task<bool> SubscribeAsync(string topicFilter);

        /// <summary>
        /// Evento crudo de mensaje MQTT recibido (topic + payload UTF-8). Se dispara
        /// para todo lo suscrito por el registry, incluyendo announcements/status_live
        /// y filtros agregados con <see cref="SubscribeAsync"/>.
        /// </summary>
        event EventHandler<MqttMessageReceivedEventArgs> MessageReceived;
    }

    public sealed class MqttMessageReceivedEventArgs : EventArgs
    {
        public string Topic { get; }
        public string Payload { get; }
        public MqttMessageReceivedEventArgs(string topic, string payload)
        {
            Topic = topic ?? "";
            Payload = payload ?? "";
        }
    }
}
