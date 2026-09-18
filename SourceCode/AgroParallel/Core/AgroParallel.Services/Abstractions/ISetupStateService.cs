// ============================================================================
// ISetupStateService.cs
// Estado del asistente de primera vez del Hub PilotX (setup.json).
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ISetupStateService
    {
        SetupStateDto Load();
        void Save(SetupStateDto dto);

        /// <summary>Marca un paso concreto como completado y persiste.</summary>
        void MarkPaso(string paso, bool valor);

        /// <summary>Marca el wizard como completado/dismissed.</summary>
        void MarkCompleted(bool completed);
        void MarkDismissed(bool dismissed);
    }
}
