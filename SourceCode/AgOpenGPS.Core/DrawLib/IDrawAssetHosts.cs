// Interfaces GL-side para los assets de dibujo del host (font y texturas,
// que quedan en GPS por los Resources). Vivían dentro de IABLineHost/
// IToolHost/IVehicleHost: se separaron para que las interfaces portables
// no dependan de tipos DrawLib (traspaso portabilidad). FormGPS las
// implementa junto a las I*Host; las extensiones de dibujo castean el
// host a estas interfaces.

using AgOpenGPS.Core.DrawLib;

namespace AgOpenGPS
{
    public interface ITextFontHost
    {
        /// <summary>font (Core.DrawLib.Font) — texto 3D en el mapa.</summary>
        Font TextFont { get; }
    }

    public interface IToolTexturesHost
    {
        Texture2D ToolAxleTexture { get; }
        Texture2D TireTexture { get; }
    }

    public interface IVehicleTexturesHost
    {
        Texture2D TractorTexture { get; }
        Texture2D HarvesterTexture { get; }
        Texture2D ArticulatedFrontTexture { get; }
        Texture2D ArticulatedRearTexture { get; }
        Texture2D FrontWheelTexture { get; }
        Texture2D QuestionMarkTexture { get; }
    }
}
