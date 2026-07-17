using AgOpenGPS.Core;
using AgOpenGPS.Core.DrawLib;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CTool necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// CSim/CModuleComm/CSection/CTram/VehicleConfig ya viven en Core y se
    /// exponen directos; de CVehicle (todavía en GPS) solo escalares del
    /// hidráulico; las texturas cruzan como Texture2D (Core.DrawLib).
    /// </summary>
    public interface IToolHost
    {
        CModuleComm Mc { get; }
        CSim Sim { get; }
        CSection[] Section { get; }
        CTram Tram { get; }

        /// <summary>vehicle.VehicleConfig (tipo articulado, wheelbase).</summary>
        VehicleConfig VehicleConfig { get; }

        /// <summary>fixHeading — rumbo actual del vehículo (rad).</summary>
        double FixHeading { get; }

        /// <summary>timerSim.Enabled — simulador activo.</summary>
        bool IsSimEnabled { get; }

        vec3 PivotAxlePos { get; }
        vec3 ToolPivotPos { get; }
        vec3 TankPos { get; }

        /// <summary>camera.camSetDistance.</summary>
        double CamSetDistance { get; }

        bool IsJobStarted { get; }

        // --- vehicle (hidráulico) ---
        bool IsHydLiftOn { get; }
        double HydLiftLookAheadDistanceLeft { get; }
        double HydLiftLookAheadDistanceRight { get; }

        // --- texturas (VehicleTextures queda en GPS por los Resources) ---
        Texture2D ToolAxleTexture { get; }
        Texture2D TireTexture { get; }
    }
}
