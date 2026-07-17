// ============================================================================
// FormGps.VehicleHost.cs
// Implementación de IVehicleHost sobre FormGPS. CVehicle vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (inversión de
// dependencia — traspaso de portabilidad 2026-07-17).
// ============================================================================

using AgOpenGPS.Core;
using AgOpenGPS.Core.DrawLib;

namespace AgOpenGPS
{
    public partial class FormGPS : IVehicleHost
    {
        CModuleComm IVehicleHost.Mc => mc;
        CSim IVehicleHost.Sim => sim;
        CTool IVehicleHost.Tool => tool;
        CBoundary IVehicleHost.Bnd => bnd;

        double IVehicleHost.AvgSpeed => avgSpeed;
        double IVehicleHost.FixHeading => fixHeading;
        bool IVehicleHost.IsFirstHeadingSet => isFirstHeadingSet;
        string IVehicleHost.HeadingFromSource => headingFromSource;
        bool IVehicleHost.IsSimEnabled => timerSim.Enabled;
        double IVehicleHost.CamSetDistance => camera.camSetDistance;
        bool IVehicleHost.IsSvennArrowOn => isSvennArrowOn;
        int IVehicleHost.ABLineWidth => ABLine.lineWidth;

        Texture2D IVehicleHost.TractorTexture => VehicleTextures.Tractor;
        Texture2D IVehicleHost.HarvesterTexture => VehicleTextures.Harvester;
        Texture2D IVehicleHost.ArticulatedFrontTexture => VehicleTextures.ArticulatedFront;
        Texture2D IVehicleHost.ArticulatedRearTexture => VehicleTextures.ArticulatedRear;
        Texture2D IVehicleHost.FrontWheelTexture => VehicleTextures.FrontWheel;
        Texture2D IVehicleHost.QuestionMarkTexture => ScreenTextures.QuestionMark;
    }
}
