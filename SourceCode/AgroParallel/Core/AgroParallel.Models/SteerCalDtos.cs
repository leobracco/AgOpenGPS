// SteerCalDtos.cs — estado del ASISTENTE DE CALIBRACIÓN DE LA DIRECCIÓN para
// la pantalla nativa (Menú izquierdo › Dirección › Asistente).
//
// Lo arma el motor (EngineSteerCalService) a partir de la máquina de estados
// AgOpenGPS.SteerCal.SteerCalWizard. Todo snake_case (AgpJson). Unidades al
// operario: grados y km/h; los textos ya vienen en criollo.
//
// Archivo nuevo, aditivo: no cambia ningún DTO existente.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class SteerCalEstadoDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;

        /// <summary>Motivo cuando ok=false: "service-unavailable" | "en-curso" |
        /// "no-corresponde" | "accion-desconocida" | otro texto.</summary>
        [JsonPropertyName("error")] public string Error { get; set; }

        /// <summary>Hay un asistente en curso (chequeo..resumen).</summary>
        [JsonPropertyName("activo")] public bool Activo { get; set; }

        /// <summary>"inactivo" | "chequeo" | "sentido_was" | "sentido_motor" | "cero_was" |
        /// "cuentas_ackermann" | "pwm_minimo" | "ganancia" | "corte_corriente" |
        /// "ajuste_fino" | "resumen" | "aplicado" | "cancelado".</summary>
        [JsonPropertyName("paso")] public string Paso { get; set; }

        /// <summary>1..9 dentro de los pasos; 0 fuera (resumen, aplicado…).</summary>
        [JsonPropertyName("numero")] public int Numero { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }

        /// <summary>"instrucciones" | "midiendo" | "propuesta" | "hecho" | "error".</summary>
        [JsonPropertyName("fase")] public string Fase { get; set; }

        [JsonPropertyName("titulo")] public string Titulo { get; set; }
        [JsonPropertyName("mensaje")] public string Mensaje { get; set; }
        [JsonPropertyName("mensaje_error")] public bool MensajeError { get; set; }
        [JsonPropertyName("medicion")] public string Medicion { get; set; }
        /// <summary>0..1</summary>
        [JsonPropertyName("progreso")] public double Progreso { get; set; }

        [JsonPropertyName("chequeos")] public List<SteerCalChequeoDto> Chequeos { get; set; } = new List<SteerCalChequeoDto>();
        /// <summary>Filas antes → después (propuesta del paso o resumen final).</summary>
        [JsonPropertyName("cambios")] public List<SteerCalCambioDto> Cambios { get; set; } = new List<SteerCalCambioDto>();

        // ---- qué botones van ----
        [JsonPropertyName("puede_empezar")] public bool PuedeEmpezar { get; set; }
        [JsonPropertyName("puede_siguiente")] public bool PuedeSiguiente { get; set; }
        [JsonPropertyName("puede_aceptar")] public bool PuedeAceptar { get; set; }
        [JsonPropertyName("puede_repetir")] public bool PuedeRepetir { get; set; }
        [JsonPropertyName("puede_saltar")] public bool PuedeSaltar { get; set; }
        [JsonPropertyName("puede_aplicar")] public bool PuedeAplicar { get; set; }
        [JsonPropertyName("puede_deshacer")] public bool PuedeDeshacer { get; set; }
        /// <summary>Mostrar el botón "Mantené apretado" (hombre muerto).</summary>
        [JsonPropertyName("hombre_muerto")] public bool HombreMuerto { get; set; }

        // ---- en vivo ----
        [JsonPropertyName("motor_activo")] public bool MotorActivo { get; set; }
        [JsonPropertyName("apretado")] public bool Apretado { get; set; }
        /// <summary>Objetivo que manda el asistente (°).</summary>
        [JsonPropertyName("setpoint")] public double Setpoint { get; set; }
        /// <summary>Ángulo real de la rueda (°, PGN 253).</summary>
        [JsonPropertyName("angulo")] public double Angulo { get; set; }
        /// <summary>Velocidad GPS (km/h).</summary>
        [JsonPropertyName("velocidad")] public double Velocidad { get; set; }
        /// <summary>Corriente del motor (0..255, −1 = no llega).</summary>
        [JsonPropertyName("corriente")] public int Corriente { get; set; }
        [JsonPropertyName("escrituras")] public int Escrituras { get; set; }
    }

    public sealed class SteerCalChequeoDto
    {
        [JsonPropertyName("texto")] public string Texto { get; set; }
        [JsonPropertyName("ok")] public bool Ok { get; set; }
    }

    public sealed class SteerCalCambioDto
    {
        [JsonPropertyName("campo")] public string Campo { get; set; }
        [JsonPropertyName("antes")] public string Antes { get; set; }
        [JsonPropertyName("despues")] public string Despues { get; set; }
    }
}
