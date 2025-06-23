using AgLibrary.Logging;
using MQTTnet;
using MQTTnet.Server;
using System;
using System.Text;
using System.Threading.Tasks;

namespace AgIO.Classes
{
    public class MqttServerManager
    {
        private MqttServer mqttServer;

        public async Task StartAsync()
        {
            var optionsBuilder = new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(1883);

            var options = optionsBuilder.Build();

            mqttServer = new MqttFactory().CreateMqttServer(options);

            mqttServer.ClientConnectedAsync += e =>
            {
                Console.WriteLine($"✅ Cliente conectado: {e.ClientId}");
                Log.EventWriter($"✅ Cliente conectado: {e.ClientId}");
                return Task.CompletedTask;
            };

            mqttServer.ClientDisconnectedAsync += e =>
            {
                Console.WriteLine($"❌ Cliente desconectado: {e.ClientId}");
                Log.EventWriter($"✅ Cliente desconectado: {e.ClientId}");
                return Task.CompletedTask;
            };

            mqttServer.InterceptingPublishAsync += e =>
            {
                try
                {
                    string topic = e.ApplicationMessage.Topic;
                    string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                    Log.EventWriter($"📥 MQTT [{topic}] => {payload}");
                }
                catch (Exception ex)
                {
                    Log.EventWriter($"❌ Error al loguear mensaje MQTT: {ex.Message}");
                }

                return Task.CompletedTask;
            };



            await mqttServer.StartAsync();
            Console.WriteLine("🚀 Broker MQTT embebido iniciado en puerto 1883");
            Log.EventWriter("🚀 Broker MQTT embebido iniciado en puerto 1883");
        }

        public async Task StopAsync()
        {
            if (mqttServer != null)
            {
                await mqttServer.StopAsync();
                Console.WriteLine("🛑 Broker MQTT detenido");
            }
        }
    }
}
