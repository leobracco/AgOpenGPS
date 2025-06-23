using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Text;
using System.Threading.Tasks;

namespace AgIO.Classes
{
    public class MqttBroker
    {
        private IMqttClient mqttClient;

        public Action<string, string> OnMessageReceived;

        public async Task ConnectAsync(string brokerAddress = "localhost", int port = 1883)
        {
            var factory = new MqttFactory();
            mqttClient = factory.CreateMqttClient();

            var mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(brokerAddress, port)
                .WithClientId("AgIO")
                .WithCleanSession()
                .Build();

            mqttClient.ApplicationMessageReceivedAsync += e =>
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                OnMessageReceived?.Invoke(topic, payload);
                return Task.CompletedTask;
            };

            mqttClient.ConnectedAsync += e =>
            {
                Console.WriteLine("✅ Conectado a MQTT");
                return Task.CompletedTask;
            };

            mqttClient.DisconnectedAsync += e =>
            {
                Console.WriteLine("🔌 MQTT desconectado");
                return Task.CompletedTask;
            };

            await mqttClient.ConnectAsync(mqttOptions);
        }

        public async Task SubscribeAsync(string topic)
        {
            if (mqttClient?.IsConnected == true)
            {
                var topicFilter = new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithAtMostOnceQoS()
                    .Build();

                await mqttClient.SubscribeAsync(topicFilter);
            }
        }

        public async Task PublishAsync(string topic, string payload)
        {
            if (mqttClient?.IsConnected == true)
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .WithRetainFlag()
                    .Build();

                await mqttClient.PublishAsync(message);
            }
        }
    }
}
