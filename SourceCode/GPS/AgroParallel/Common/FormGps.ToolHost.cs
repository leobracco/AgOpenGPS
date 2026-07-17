// ============================================================================
// FormGps.ToolHost.cs
// Implementación de IToolHost sobre FormGPS. CTool vive en AgOpenGPS.Core y
// consume el host a través de esta interfaz (inversión de dependencia —
// traspaso de portabilidad 2026-07-17).
// ============================================================================

using AgOpenGPS.Core;
using AgOpenGPS.Core.DrawLib;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public partial class FormGPS : IToolHost
    {
        CModuleComm IToolHost.Mc => mc;
        CSim IToolHost.Sim => sim;
        CSection[] IToolHost.Section => section;
        CTram IToolHost.Tram => tram;

        VehicleConfig IToolHost.VehicleConfig => vehicle.VehicleConfig;

        double IToolHost.FixHeading => fixHeading;
        bool IToolHost.IsSimEnabled => timerSim.Enabled;

        vec3 IToolHost.PivotAxlePos => pivotAxlePos;
        vec3 IToolHost.ToolPivotPos => toolPivotPos;
        vec3 IToolHost.TankPos => tankPos;

        double IToolHost.CamSetDistance => camera.camSetDistance;
        bool IToolHost.IsJobStarted => isJobStarted;

        bool IToolHost.IsHydLiftOn => vehicle.isHydLiftOn;
        double IToolHost.HydLiftLookAheadDistanceLeft => vehicle.hydLiftLookAheadDistanceLeft;
        double IToolHost.HydLiftLookAheadDistanceRight => vehicle.hydLiftLookAheadDistanceRight;

        Texture2D IToolHost.ToolAxleTexture => VehicleTextures.ToolAxle;
        Texture2D IToolHost.TireTexture => VehicleTextures.Tire;
    }
}
