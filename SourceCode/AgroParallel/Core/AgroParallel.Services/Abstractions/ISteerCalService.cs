// ISteerCalService — asistente de calibración guiada de la dirección.
//
// La implementación vive en el motor (PilotX.GuidanceEngine/EngineSteerCalService)
// porque necesita la telemetría viva (PGN 253/250, GPS) y el PGN 254. Si el host
// no lo inyecta (Android, WinForms viejo) el controller contesta
// service-unavailable y la pantalla no ofrece el asistente.
//
// Archivo nuevo, aditivo.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ISteerCalService
    {
        /// <summary>Estado actual. Consultarlo ES el latido de la pantalla: si deja
        /// de consultar con un asistente en curso, el motor lo cancela solo.</summary>
        SteerCalEstadoDto Estado();

        /// <summary>Arranca con la config actual. ok=false "en-curso" si ya hay uno.</summary>
        SteerCalEstadoDto Iniciar();

        /// <summary>empezar | siguiente | saltar | repetir | aceptar | rechazar |
        /// cancelar | aplicar | deshacer.</summary>
        SteerCalEstadoDto Accion(string accion);

        /// <summary>Hombre muerto: apretado=true cada ≤ 300 ms mientras el operario
        /// mantiene el botón; false al soltar (corta en el acto).</summary>
        SteerCalEstadoDto Latido(bool apretado);
    }
}
