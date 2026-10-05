// NodoDiagDto — diagnóstico del cliente MQTT del registro de nodos. Útil cuando
// un ESP32 dice estar conectado al broker pero no aparece en la lista de Nodos
// del Hub: muestra a qué broker se enganchó el Hub, si la conexión está viva,
// qué tópicos tiene suscritos y los últimos mensajes que recibió.

using System;
using System.Collections.Generic;

namespace AgroParallel.Models
{
    public sealed class NodoMqttMessage
    {
        public DateTime TimestampUtc { get; set; }
        public string Topic { get; set; }
        public string Payload { get; set; }
    }

    /// <summary>Detalle de un gap en la secuencia `seq` de un firmware
    /// (Tanda 2 punto 7). Informativo, no alarma — sirve para diagnosticar
    /// LAN ruidosa o nodos que rebootean sin ser detectados.</summary>
    public sealed class NodoSeqGap
    {
        public DateTime TimestampUtc { get; set; }
        public string Uid { get; set; }
        public string Schema { get; set; }
        public int LastSeq { get; set; }
        public int NewSeq { get; set; }
        public int Missed { get; set; }
    }

    public sealed class NodoDiagInfo
    {
        public string BrokerAddress { get; set; }
        public int BrokerPort { get; set; }
        public bool Connected { get; set; }
        public bool WildcardCaptureOn { get; set; }
        public List<string> Subscriptions { get; set; }
        public List<NodoMqttMessage> RecentMessages { get; set; }
        public int KnownNodesCount { get; set; }
        // Mensaje en español, listo para mostrar al operario.
        public string LastError { get; set; }
        // Código corto tipo "AGP-MQTT-001" — el operario puede dictarlo al
        // soporte / al bot de WhatsApp y se atiende sin pedir capturas.
        public string LastErrorCode { get; set; }
        // Detalle técnico (tipo de excepción + mensaje crudo). NO se pinta en
        // la UI principal — sirve para soporte / log / diagnóstico avanzado.
        public string LastErrorTechnical { get; set; }
        public DateTime? LastErrorUtc { get; set; }
        public DateTime? LastConnectedUtc { get; set; }
        public int ConnectAttempts { get; set; }

        // Tanda 2 punto 7: gap detection.
        // SeqGapCount = total acumulado de mensajes perdidos detectados.
        // SeqResetCount = cuántas veces se observó un reboot implícito del nodo.
        // RecentSeqGaps = últimos N gaps (más nuevos primero).
        public long SeqGapCount { get; set; }
        public long SeqResetCount { get; set; }
        public List<NodoSeqGap> RecentSeqGaps { get; set; }
    }
}
