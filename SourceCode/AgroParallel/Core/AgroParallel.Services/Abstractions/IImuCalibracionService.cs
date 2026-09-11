// IImuCalibracionService — puente entre el Hub y el objeto CAHRS vivo de
// PilotX (FormGPS.ahrs). Mismo patrón que IGuidanceCalculator: la interfaz
// vive en netstandard2.0 (AgroParallel.Services), la implementación real
// ("using AgOpenGPS") vive del lado GPS en AgroParallel.Adapters.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IImuCalibracionService
    {
        /// <summary>Lectura del estado actual (roll vivo, offset, invertido, filtro).</summary>
        ImuCalibracionSnapshot GetSnapshot();

        /// <summary>Comandos: "zero_roll" (nivelar — captura el roll actual como
        /// cero), "remove_zero_offset" (volver el offset a 0), "roll_offset_up" /
        /// "roll_offset_down" (ajuste manual ±0.1°), "toggle_invert" (invertir
        /// el sentido del roll), "reset_imu" (fuerza sentinels para esperar un
        /// dato fresco del sensor, ej. tras desconectar/reconectar). Dispara en
        /// el hilo de UI de PilotX (fire-and-forget); devuelve false si el
        /// comando no se reconoce.</summary>
        bool ExecuteCommand(string command);

        /// <summary>Filtro de suavizado del roll, 0–100 (mismo rango que el
        /// hsbarRollFilter nativo; se persiste como fracción 0–1).</summary>
        bool SetRollFilter(double percent0To100);
    }
}
