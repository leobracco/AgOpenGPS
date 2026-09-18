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

        /// <summary>Sprite de vehículo custom activo (nombre de archivo en
        /// wwwroot/img/vehiculos/), o "" si se usa la marca embebida.</summary>
        string GetVehiculoCustom();

        /// <summary>Activa un sprite custom del mapa ("" = volver a la marca
        /// embebida). Persiste el setting y recarga la textura en caliente.</summary>
        bool SetVehiculoCustom(string archivo);

        // --- IMU / fuente de rumbo (tabDHeading + tabDRoll) --------------------

        ImuConfigDto GetImu();

        /// <summary>Persiste setGPS_*/setIMU_* + aplica en vivo a mf/ahrs (hilo UI).</summary>
        bool SaveImu(ImuConfigDto cfg);

        /// <summary>Roll actual + rollZero + presencia de IMU (centinelas 99999/88888).</summary>
        ImuLiveDto GetImuLive();

        /// <summary>Poner el roll actual como cero. false si no hay roll de IMU.</summary>
        bool ZeroRoll();

        /// <summary>Ajustar el cero de roll en ±delta grados. false si no hay roll de IMU.</summary>
        bool AdjustRollZero(double delta);

        /// <summary>Quitar el offset de cero (rollZero = 0).</summary>
        bool RemoveRollZero();

        /// <summary>Reset IMU: vuelve a los centinelas (sin heading/roll de IMU).</summary>
        bool ResetImu();
    }
}
