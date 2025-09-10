using Newtonsoft.Json;

namespace AgOpenGPS.Shape
{
    /// <summary>
    /// Publishes attribute information of the current shape feature over MQTT.
    /// </summary>
    public class ShapeAttributePublisher
    {
        private readonly MqttPublisher _publisher;
        private readonly string _topic;

        public ShapeAttributePublisher(string broker, int port, string topic)
        {
            _publisher = new MqttPublisher(broker, port, topic);
            _topic = topic;
        }

        public void Publish(ShapeHit hit, double lat, double lon)
        {
            var payload = new
            {
                lat,
                lon,
                featureId = hit?.Feature?.Id,
                distance_m = hit?.DistanceMeters,
                attributes = hit?.Feature?.Attributes
            };
            string json = JsonConvert.SerializeObject(payload);
            _publisher.Publish(json);
        }
    }
}
