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

        // -----------------------------------------------------------------
        // UsbFlashService.ArmarArgs — armado de argv para esptool.exe, SIN
        // IO ni proceso (van directo a ProcessStartInfo.ArgumentList, nunca
        // shell-concat).
        // -----------------------------------------------------------------

        [Fact]
        public void ArmarArgs_completo_escribe_factory_a_0x0()
        {
            var a = AgroParallel.Usb.UsbFlashService.ArmarArgs("COM5", "completo", @"C:\f\factory.bin", false);
            Assert.Contains("--port", a); Assert.Contains("COM5", a);
            Assert.Contains("write_flash", a);
            int i = a.IndexOf("write_flash");
            Assert.Equal("0x0", a[i + 1]);
            Assert.Equal(@"C:\f\factory.bin", a[i + 2]);
        }

        [Fact]
        public void ArmarArgs_app_escribe_firmware_a_0x10000()
        {
            var a = AgroParallel.Usb.UsbFlashService.ArmarArgs("COM3", "app", @"C:\f\firmware.bin", false);
            int i = a.IndexOf("write_flash");
            Assert.Equal("0x10000", a[i + 1]);
            Assert.Equal(@"C:\f\firmware.bin", a[i + 2]);
        }

        [Fact]
        public void ArmarArgs_borrar_antes_agrega_erase_flag()
        {
            var a = AgroParallel.Usb.UsbFlashService.ArmarArgs("COM3", "app", @"C:\f\firmware.bin", true);
            Assert.Contains("--erase-all", a);   // write_flash -e / --erase-all
        }

        // -----------------------------------------------------------------
        // UsbFlashService.ArmarLineaDeComando (internal) — port a mano del
        // algoritmo de citado de Windows (PasteArguments/ArgumentList) que
        // arma psi.Arguments. netstandard2.0 no tiene ArgumentList, así que
        // esto reemplaza al shell-concat ingenuo; sin tests hasta ahora.
        // -----------------------------------------------------------------

        [Fact]
        public void Quoting_argumento_simple_sin_espacios_no_lleva_comillas()
        {
            string linea = AgroParallel.Usb.UsbFlashService.ArmarLineaDeComando(new[] { "COM5" });
            Assert.Equal("COM5", linea);
        }

        [Fact]
        public void Quoting_argumento_con_espacios_queda_citado_como_un_solo_arg()
        {
            string linea = AgroParallel.Usb.UsbFlashService.ArmarLineaDeComando(
                new[] { @"C:\Program Files\x\factory.bin" });
            Assert.Equal("\"C:\\Program Files\\x\\factory.bin\"", linea);
        }

        [Fact]
        public void Quoting_comilla_embebida_se_escapa()
        {
            string linea = AgroParallel.Usb.UsbFlashService.ArmarLineaDeComando(new[] { "foo\"bar" });
            Assert.Equal("\"foo\\\"bar\"", linea);
        }

        [Fact]
        public void Quoting_backslash_final_se_dobla_antes_de_la_comilla_de_cierre()
        {
            // "a b\" necesita comillas por el espacio; el backslash final, al
            // quedar pegado a la comilla de cierre, se duplica (si no, la
            // comilla de cierre quedaría escapada y el argumento no cerraría).
            string linea = AgroParallel.Usb.UsbFlashService.ArmarLineaDeComando(new[] { @"a b\" });
            Assert.Equal("\"a b\\\\\"", linea);
        }

        [Fact]
        public void Quoting_string_vacio_produce_comillas_vacias()
        {
            string linea = AgroParallel.Usb.UsbFlashService.ArmarLineaDeComando(new[] { "" });
            Assert.Equal("\"\"", linea);
        }

        [Fact]
        public void Quoting_varios_argumentos_se_separan_con_un_espacio()
        {
            string linea = AgroParallel.Usb.UsbFlashService.ArmarLineaDeComando(
                new[] { "--chip", "auto", "--port", "COM5" });
            Assert.Equal("--chip auto --port COM5", linea);
        }

        // -----------------------------------------------------------------
        // UsbDriverInstaller.ExitCodeEsExito — códigos de salida de pnputil
        // que Microsoft documenta como éxito para /add-driver /install: 0,
        // 259 (ERROR_NO_MORE_ITEMS) y 3010 (ERROR_SUCCESS_REBOOT_REQUIRED,
        // primera instalación del CP210x/CH340 que pide reinicio).
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(0, true)]
        [InlineData(259, true)]
        [InlineData(3010, true)]
        [InlineData(1, false)]
        [InlineData(2, false)]
        public void ExitCodeEsExito_clasifica_codigos_de_pnputil(int code, bool esExito)
        {
            Assert.Equal(esExito, AgroParallel.Usb.UsbDriverInstaller.ExitCodeEsExito(code));
        }
    }
}
