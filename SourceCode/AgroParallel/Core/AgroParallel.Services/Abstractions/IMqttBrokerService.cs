// ============================================================================
// IMqttBrokerService.cs — Broker MQTT embebido portable (netstandard2.0).
// Extraído de CoreX/AgIO MQTT.Designer.cs para permitir que el broker corra
// en Android (Foreground Service) sin WinForms.
// ============================================================================

using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgroParallel.Services.Abstractions
{
    public interface IMqttBrokerService
    {
        bool IsRunning { get; }
        int Port { get; }
        int ClientsConnected { get; }
        long MessagesTotal { get; }

        Task StartAsync(int port = 1883);
        Task StopAsync();

        // Últimos N tópicos recibidos (para monitor/debug).
        List<string> GetRecentTopics(int max = 200);
    }
}
