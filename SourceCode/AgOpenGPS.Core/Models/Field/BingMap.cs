namespace AgOpenGPS.Core.Models
{
    // Modelo portable: el fondo satelital se guarda como PNG crudo (byte[]),
    // sin System.Drawing. La decodificación a textura ocurre en la capa GL
    // (Texture2D/GeoTexture2D).
    public class BingMap
    {
        public BingMap(
            GeoBoundingBox geoBoundingBox,
            byte[] pngBytes)
        {
            GeoBoundingBox = geoBoundingBox;
            PngBytes = pngBytes;
        }

        public GeoBoundingBox GeoBoundingBox { get; }
        public byte[] PngBytes { get; }

    }
}
