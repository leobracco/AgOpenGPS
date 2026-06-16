// ============================================================================
// CutCommand.cs - Intención de corte que un ICutAdapter calcula por tick.
//
// Es un valor inmutable: el adapter dice "este nodo (uid) debería recibir este
// payload en este topic". El CutDispatcher decide si publicar (dedup/heartbeat)
// y se encarga del transporte MQTT. Bits[] se guarda para el panel debug de la
// UI (no para el wire — el wire usa Payload ya serializado).
// ============================================================================

namespace AgroParallel.Cut
{
    public sealed class CutCommand
    {
        public string Uid { get; }
        public string Topic { get; }
        public string Payload { get; }
        public int[] Bits { get; }

        public CutCommand(string uid, string topic, string payload, int[] bits)
        {
            Uid = uid;
            Topic = topic;
            Payload = payload;
            Bits = bits;
        }
    }
}
