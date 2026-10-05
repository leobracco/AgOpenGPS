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

        // ---- Manejo libre (free drive) --------------------------------------
        // Port del bloque "Free Drive" del FormSteer nativo. Mueve la dirección
        // sin guía: sirve para probar sentido de giro, tope de ángulo y WAS con
        // el tractor PARADO. Nunca tira: la pantalla necesita mostrar el motivo.

        /// <summary>Estado actual (prendido, ángulo pedido, velocidad y límite).</summary>
        FreeDriveStateDto GetFreeDrive();

        /// <summary>Prende o apaga el manejo libre. Prender se RECHAZA si el
        /// tractor va más rápido que el límite de funciones de guiado, o si el
        /// host no sabe informar velocidad. Apagar siempre vale y deja el
        /// ángulo en cero.</summary>
        FreeDriveStateDto SetFreeDrive(bool on);

        /// <summary>Corre el ángulo pedido un grado a la izquierda (dir&lt;0) o a
        /// la derecha (dir&gt;0). Solo con el manejo libre prendido.</summary>
        FreeDriveStateDto NudgeFreeDrive(int dir);

        /// <summary>Alterna entre 0° y 5°, igual que el botón del punto del
        /// FormSteer nativo: es la forma rápida de ver si el volante responde.</summary>
        FreeDriveStateDto ToggleFreeDriveZero();
    }
}
