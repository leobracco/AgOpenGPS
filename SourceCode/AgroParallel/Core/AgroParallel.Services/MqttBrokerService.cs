// ============================================================================
// MqttBrokerService.cs — Broker MQTT embebido portable (netstandard2.0).
// MQTTnet Server sin dependencias WinForms. Reemplaza el partial
// FormLoop.MQTT de CoreX. Corre en cualquier host: WinForms, Android
// Foreground Service, console, etc.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using MQTTnet;
using MQTTnet.Server;

namespace AgroParallel.Services
{
    public sealed class MqttBrokerService : IMqttBrokerService, IDisposable
    {
        private MqttServer _server;
        private readonly LinkedList<string> _recentTopics = new LinkedList<string>();
        private readonly object _lock = new object();

        /// <summary>
        /// Adónde van, además del anillo de tópicos, los altas y bajas de
        /// clientes del broker. El anillo se pierde al reiniciar y sólo se ve
        /// abriendo el panel; estos eventos son la única huella de que un nodo
        /// se cayó y volvió, que es exactamente lo que hay que cruzar contra un
        /// hueco de semilla (Las Gringas, 2026-09-25). El host lo cablea a
        /// Log.EventWriter — acá no se conoce AgLibrary a propósito: este
        /// servicio también corre en Android.
        /// </summary>
        public static Action<string> EventoCliente { get; set; }

        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public int ClientsConnected { get; private set; }
        public long MessagesTotal { get; private set; }

        public async Task StartAsync(int port = 1883)
        {
            if (IsRunning) return;

            Port = port;
            var factory = new MqttFactory();
            var options = new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(port)
                .Build();

            _server = factory.CreateMqttServer(options);

            _server.ClientConnectedAsync += e =>
            {
                ClientsConnected++;
                Anotar("[+] " + e.ClientId);
                return Task.CompletedTask;
            };

            _server.ClientDisconnectedAsync += e =>
            {
                if (ClientsConnected > 0) ClientsConnected--;
                Anotar("[-] " + e.ClientId);
                return Task.CompletedTask;
            };

            _server.InterceptingPublishAsync += e =>
            {
                MessagesTotal++;
                AddTopic(e.ApplicationMessage.Topic);
                return Task.CompletedTask;
            };

            await _server.StartAsync();
            IsRunning = true;
        }

        public async Task StopAsync()
        {
            if (!IsRunning || _server == null) return;

            try
            {
                await _server.StopAsync();
                _server.Dispose();
            }
            catch { }
            finally
            {
                _server = null;
                IsRunning = false;
                ClientsConnected = 0;
                MessagesTotal = 0;
            }
        }

        public List<string> GetRecentTopics(int max = 200)
        {
            lock (_lock)
                return _recentTopics.Take(max).ToList();
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        /// <summary>Alta o baja de un cliente: al anillo del panel Y al log de
        /// eventos. Nunca tira: si el sink del host falla, el broker sigue.</summary>
        private void Anotar(string linea)
        {
            AddTopic(linea);
            try
            {
                var sink = EventoCliente;
                if (sink != null) sink("MQTT: " + linea + " (clientes=" + ClientsConnected + ")");
            }
            catch { } // silencioso a propósito: loguear no puede voltear el broker
        }

        private void AddTopic(string topic)
        {
            lock (_lock)
            {
                _recentTopics.AddFirst(DateTime.Now.ToString("HH:mm:ss") + "  " + topic);
                while (_recentTopics.Count > 200)
                    _recentTopics.RemoveLast();
            }
        }
    }
}
