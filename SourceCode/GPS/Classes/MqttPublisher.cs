using MQTTnet;
using MQTTnet.Client;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenGPS
{
    public class MqttPublisher
    {
        private readonly IMqttClient mqttClient;
        private readonly MqttClientOptions mqttOptions;
        private readonly string topic;

        public MqttPublisher(string broker, int port, string topicName)
        {
            var factory = new MqttFactory();
            mqttClient = factory.CreateMqttClient();
            topic = topicName;

            mqttOptions = new MqttClientOptionsBuilder()
                .WithClientId("AgOpenGPS")
                .WithTcpServer(broker, port)
                .Build();
        }

        public async Task ConnectAsync()
        {
            try
            {
                if (!mqttClient.IsConnected)
                {
                    await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
                    Console.WriteLine("MQTT conectado correctamente");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al conectar MQTT: {ex.Message}");
            }
        }

        public async void Publish(string message)
        {
            if (!mqttClient.IsConnected)
            {
                await ConnectAsync();
            }

            var mqttMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(message))
                .WithRetainFlag(false)
                .Build();

            try
            {
                await mqttClient.PublishAsync(mqttMessage, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al publicar MQTT: {ex.Message}");
            }
        }

    }
}
