// ============================================================================
// UsbFlashTests — pinnea el layout de rutas que usa el flasheo USB de nodos
// (QuantiX/VistaX/etc.) contra el cache local de FirmwareMirror.
// ============================================================================

using System.IO;
using AgroParallel.OrbitX;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class UsbFlashTests
    {
        [Fact]
        public void PathFactory_apunta_a_factory_bin_en_el_dir_de_version()
        {
            string cache = Path.Combine(Path.GetTempPath(), "agp_fw_test");
            string p = AgroParallel.OrbitX.FirmwareMirror.PathFactory(cache, "flowx", "1.0.0");
            Assert.EndsWith(Path.Combine("flowx", "1.0.0", "factory.bin"), p);
        }

        [Theory]
        [InlineData("Connecting........", "conectando", 0)]
        [InlineData("Erasing flash...", "borrando", 0)]
        [InlineData("Writing at 0x00010000... (37 %)", "escribiendo", 37)]
        [InlineData("Hash of data verified.", "verificando", 100)]
        [InlineData("Hard resetting via RTS pin...", "reset", 100)]
        public void Parse_actualiza_fase_y_pct(string linea, string faseEsp, int pctEsp)
        {
            string fase = "idle"; int pct = 0;
            AgroParallel.Usb.EsptoolOutputParser.Parse(linea, ref fase, ref pct);
            Assert.Equal(faseEsp, fase);
            Assert.Equal(pctEsp, pct);
        }

        [Theory]
        [InlineData("A fatal error occurred: Failed to connect to ESP32: No serial data received.", "AGP-USB-002")]
        [InlineData("A fatal error occurred: Could not open COM5, the port doesn't exist", "AGP-USB-001")]
        [InlineData("Serial port COM5: Access is denied.", "AGP-USB-001")]
        [InlineData("Hash of data verified.", null)]
        public void ClasificarError_mapea_fallas_conocidas(string log, string codigoEsp)
        {
            Assert.Equal(codigoEsp, AgroParallel.Usb.EsptoolOutputParser.ClasificarError(log));
        }
    }
}
