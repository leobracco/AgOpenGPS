using AgOpenGPS.Core;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CVehicle necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// CTool/CSim/CModuleComm/CBoundary ya viven en Core y se exponen
    /// directos; las texturas cruzan como Texture2D (VehicleTextures y
    /// ScreenTextures quedan en GPS por los Resources).
    /// </summary>
    public interface IVehicleHost
    {
        CModuleComm Mc { get; }
        CSim Sim { get; }
        CTool Tool { get; }
        CBoundary Bnd { get; }

        double AvgSpeed { get; }

        /// <summary>fixHeading — rumbo actual del vehículo (rad).</summary>
        double FixHeading { get; }

        bool IsFirstHeadingSet { get; }

        /// <summary>headingFromSource ("Fix", "Dual", "IMU"…).</summary>
        string HeadingFromSource { get; }

        /// <summary>timerSim.Enabled — simulador activo.</summary>
        bool IsSimEnabled { get; }

        /// <summary>camera.camSetDistance.</summary>
        double CamSetDistance { get; }

        bool IsSvennArrowOn { get; }

        /// <summary>ABLine.lineWidth — grosor de línea de guiado (px GL).</summary>
        int ABLineWidth { get; }

        //las texturas se movieron a IVehicleTexturesHost
        //(DrawLib/IDrawAssetHosts.cs): eran el único tipo DrawLib acá.
    }
}
