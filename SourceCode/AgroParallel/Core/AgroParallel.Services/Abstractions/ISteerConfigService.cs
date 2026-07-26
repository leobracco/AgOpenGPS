// ISteerConfigService — puente entre la pantalla direccion.html del Hub y la
// configuración real del autoguiado (Properties.Settings.setAS_* / setArdSteer_*)
// + el envío de los PGN 252/251 al módulo de dirección.
//
// Mismo patrón que IImuCalibracionService/IGuidanceCalculator: la interfaz vive
// en netstandard2.0 (portable), la implementación ("using AgOpenGPS") vive en
// AgroParallel.Adapters y la comparten el host WinForms (FormGPS) y el motor
// headless (GuidanceEngineHost).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ISteerConfigService
    {
        /// <summary>Config actual leída de los settings reales de PilotX.</summary>
        SteerConfigDto Get();

        /// <summary>Persiste la config en los settings, la deja aplicada en el
        /// modelo del vehículo y manda los PGN 252/251 al módulo de dirección.
        /// Devuelve false si el objeto es nulo o falló el guardado.</summary>
        bool Save(SteerConfigDto config);

        /// <summary>Pone el sensor de ángulo (WAS) en cero tomando la lectura
        /// viva del módulo, igual que el botón "&gt;0&lt;" del FormSteer nativo.
        /// Falla (ok=false) si el offset resultante supera ±3900 cuentas.</summary>
        SteerZeroWasResult ZeroWas();
    }
}
