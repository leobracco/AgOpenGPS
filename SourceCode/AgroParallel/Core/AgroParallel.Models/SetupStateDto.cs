// ============================================================================
// SetupStateDto.cs
// Estado del asistente de primera vez del Hub PilotX. Persistido en setup.json.
// Cada paso del wizard marca su flag; si todos están true el wizard no se
// vuelve a abrir automáticamente (sigue accesible desde el sidebar).
// ============================================================================

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class SetupStateDto
    {
        /// <summary>El usuario marcó el wizard como "no mostrar de nuevo".</summary>
        [JsonPropertyName("wizard_dismissed")] public bool WizardDismissed { get; set; }

        /// <summary>Todos los pasos críticos completados.</summary>
        [JsonPropertyName("wizard_completed")] public bool WizardCompleted { get; set; }

        // Pasos individuales (boolean granular para retomar el wizard donde quedó).
        [JsonPropertyName("paso_pc_ok")]     public bool PasoPcOk { get; set; }
        [JsonPropertyName("paso_orbitx")]    public bool PasoOrbitx { get; set; }
        [JsonPropertyName("paso_encender")]  public bool PasoEncender { get; set; }
        [JsonPropertyName("paso_aceptar")]   public bool PasoAceptar { get; set; }
        [JsonPropertyName("paso_configurar")] public bool PasoConfigurar { get; set; }

        [JsonPropertyName("ultimo_paso")]    public string UltimoPaso { get; set; }

        public SetupStateDto()
        {
            UltimoPaso = "";
        }
    }
}
