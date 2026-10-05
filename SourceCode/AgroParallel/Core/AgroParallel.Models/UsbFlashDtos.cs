// UsbFlashDtos — DTOs del wire de flasheo de firmware ESP32 por USB.
//
// El Hub detecta un nodo QuantiX/VistaX/SectionX/etc. conectado por cable USB
// (sin depender de MQTT ni LAN) y permite grabarle firmware desde la propia
// pantalla de la cabina, sin PC externa ni internet. Todos los campos van en
// snake_case exacto (aunque AgpJson ya serializa snake por convención, se
// declaran explícitos para que el contrato sea legible sin adivinar).

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Un puerto COM disponible en la PC, para elegir a qué nodo flashear.</summary>
    public sealed class PuertoComDto
    {
        [JsonPropertyName("port")]        public string Port { get; set; }
        [JsonPropertyName("descripcion")] public string Descripcion { get; set; }
    }

    /// <summary>Pedido de flasheo: qué producto/versión grabar, por qué puerto y en qué modo.</summary>
    public sealed class UsbFlashRequest
    {
        [JsonPropertyName("producto")]     public string Producto { get; set; }
        [JsonPropertyName("version")]      public string Version { get; set; }
        [JsonPropertyName("puerto")]       public string Puerto { get; set; }
        [JsonPropertyName("modo")]         public string Modo { get; set; }        // "completo" | "app"
        [JsonPropertyName("borrar_antes")] public bool BorrarAntes { get; set; }
    }

    /// <summary>Estado en vivo del flasheo en curso, para el polling de la UI.</summary>
    public sealed class UsbFlashEstadoDto
    {
        [JsonPropertyName("en_curso")]  public bool EnCurso { get; set; }
        [JsonPropertyName("fase")]      public string Fase { get; set; }       // conectando|borrando|escribiendo|verificando|reset|idle
        [JsonPropertyName("pct")]       public int Pct { get; set; }
        [JsonPropertyName("resultado")] public string Resultado { get; set; }  // null | "ok" | "fail"
        [JsonPropertyName("codigo")]    public string Codigo { get; set; }     // null | "AGP-USB-00X"
        [JsonPropertyName("log")]       public string Log { get; set; }
    }

    /// <summary>Pedido de instalación de driver USB-serial (CP210x/CH340) para que Windows reconozca el nodo.</summary>
    public sealed class UsbDriverRequest
    {
        [JsonPropertyName("driver")] public string Driver { get; set; }  // "cp210x" | "ch340" | "ambos"
    }
}
