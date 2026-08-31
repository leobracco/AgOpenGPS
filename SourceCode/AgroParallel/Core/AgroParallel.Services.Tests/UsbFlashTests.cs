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
    }
}
