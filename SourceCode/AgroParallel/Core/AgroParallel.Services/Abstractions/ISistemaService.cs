// ============================================================================
// ISistemaService.cs
// Servicios OS-level del módulo Sistema: brillo de pantalla + acciones de
// energía (apagar, reiniciar, logoff, suspender, cerrar app).
// La implementación concreta vive en AgroParallel.Shell (net48 + WMI/dxva2).
// ============================================================================

namespace AgroParallel.Services.Abstractions
{
    public enum PowerAction
    {
        Shutdown = 0,
        Restart = 1,
        LogOff = 2,
        Suspend = 3,
        ExitApp = 4
    }

    public interface ISistemaService
    {
        /// <summary>Brillo actual 0..100, o -1 si no se pudo leer.</summary>
        int GetBrightness();

        /// <summary>Aplica brillo 0..100. Devuelve true si DDC/CI o WMI aceptaron.</summary>
        bool SetBrightness(int percent);

        /// <summary>Ejecuta la acción de energía solicitada (asincrónico).</summary>
        void ExecutePowerAction(PowerAction action);
    }
}
