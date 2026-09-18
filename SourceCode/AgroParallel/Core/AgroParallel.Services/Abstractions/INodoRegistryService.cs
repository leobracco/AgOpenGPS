// INodoRegistryService — descubre y mantiene una tabla de nodos AgroParallel
// vivos en la LAN MQTT. Reemplaza la lógica embebida en FormNodos.cs.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Common;

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
        /// Tanda 2 — publica un comando con envelope estándar (cmd_id+ttl+ack).
        /// El Task resuelve cuando llega ack del firmware o cuando vence el TTL.
        /// Backward-compatible: el envelope es additive (_meta), firmwares viejos
        /// procesan el comando normalmente pero el bridge recibirá timeout.
        /// </summary>
        Task<CmdAckResult> PublishCmdAsync(
            string topic, string uid,
            IDictionary<string, object> payload,
            int ttlMs = 5000, string source = "pilotx");

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

        /// <summary>
        /// Snapshot del estado de la conexión MQTT y últimos mensajes recibidos.
        /// Útil para diagnosticar por qué un ESP32 conectado al broker no aparece
        /// en la lista de nodos (topic incorrecto, broker distinto, etc.).
        /// </summary>
        NodoDiagInfo GetDiagnostic();

        /// <summary>
        /// Activa o desactiva la captura wildcard "#" para ver TODOS los mensajes
        /// MQTT que pasan por el broker. Útil sólo para debugging.
        /// </summary>
        Task<bool> SetWildcardCaptureAsync(bool on);

        /// <summary>
        /// Fuerza un intento de reconexión inmediato al broker configurado en
        /// <see cref="Start"/>. Útil cuando el broker recién se levantó o cambió
        /// de IP. Retorna true si la conexión quedó establecida.
        /// </summary>
        Task<bool> ReconnectAsync();

        /// <summary>
        /// Tanda 2 #8 — tracker de sync desired/reported de la config de cada nodo.
        /// La PC publica retained en `agp/{prod}/{uid}/config/desired` y el firmware
        /// echa back en `.../config/reported`; este tracker compara timestamps y
        /// expone el estado (in_sync/drift/no_report/pending) para la UI.
        /// </summary>
        AgpConfigSyncTracker ConfigSync { get; }
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
