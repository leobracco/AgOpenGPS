// ============================================================================
// IVehicleToolService — lectura/escritura de la config de Vehículo y
// Herramienta de PilotX desde la UI HTML. La implementación concreta vive en
// GPS/AgroParallel/Common/FormGpsVehicleToolService.cs (único proyecto que
// puede tocar Properties.Settings.Default.setVehicle_*/setTool_*).
//
// El save dispara también un "reload" de CVehicle/CTool dentro de FormGPS
// (en el hilo de UI) para que los cambios sean efectivos sin reiniciar.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IVehicleToolService
    {
        VehicleConfigDto GetVehicle();
        ToolConfigDto GetTool();
        VehicleToolBundleDto GetBundle();

        /// <summary>Persiste setVehicle_* + Settings.Save() + reload CVehicle.</summary>
        bool SaveVehicle(VehicleConfigDto cfg);

        /// <summary>Persiste setTool_*/setVehicle_tool* + Settings.Save() + reload CTool.</summary>
        bool SaveTool(ToolConfigDto cfg);
    }
}
