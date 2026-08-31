// ============================================================================
// UsbFlashController.cs
//
// Endpoints REST para flashear firmware ESP32 (QuantiX/VistaX/SectionX/etc.)
// por cable USB directo desde la cabina, sin PC externa ni internet. Envuelve
// UsbFlashService (esptool.exe como proceso hijo) y UsbDriverInstaller
// (drivers CP210x/CH340 vía pnputil).
//
// Endpoints:
//   GET  /api/usb/puertos          → puertos COM visibles
//   POST /api/usb/flash            → inicia flasheo (async, background)
//   GET  /api/usb/flash/estado     → polling del progreso
//   POST /api/usb/driver/instalar  → instala driver USB-serial (UAC)
//
// El .bin a flashear sale del MISMO cache local que sirve /api/firmwares (ver
// FirmwaresController): "completo" usa factory.bin @ 0x0 (borra todo, incluye
// bootloader/partition table), "app" usa firmware.bin @ 0x10000 (solo la app,
// requiere que el nodo ya tenga bootloader).
// ============================================================================

using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.OrbitX;
using AgroParallel.Services;
using AgroParallel.Usb;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class UsbFlashController : AgpControllerBase
    {
        // Mismo regex que FirmwaresController — anti path-traversal en el
        // lookup del cache (producto/version viajan como partes de un path).
        private static readonly Regex RxProducto = new Regex("^[a-zA-Z][a-zA-Z0-9-]{1,31}$");
        private static readonly Regex RxVersion = new Regex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,31}$");

        private readonly UsbFlashService _usb;
        private readonly string _engineBaseDir;

        public UsbFlashController(UsbFlashService usb, string engineBaseDir)
        {
            _usb = usb;
            _engineBaseDir = engineBaseDir;
        }

        [Route(HttpVerbs.Get, "/usb/puertos")]
        public Task Puertos() => WriteJsonAsync(new { ok = true, puertos = _usb.ListarPuertos() });

        [Route(HttpVerbs.Get, "/usb/flash/estado")]
        public Task Estado() => WriteJsonAsync(_usb.Estado());

        [Route(HttpVerbs.Post, "/usb/flash")]
        public async Task Flash()
        {
            var req = await ReadJsonBodyAsync<UsbFlashRequest>().ConfigureAwait(false);
            if (req == null || !RxProducto.IsMatch(req.Producto ?? "") || !RxVersion.IsMatch(req.Version ?? ""))
            {
                await WriteErrorAsync(400, "AGP-USB-004", "Producto o versión inválidos.").ConfigureAwait(false);
                return;
            }
            // Cualquier valor que no sea "app" cae en "completo" (factory.bin,
            // borra todo). Es el modo seguro por defecto: si el nodo nunca tuvo
            // firmware AgroParallel, "app" solo (sin bootloader) no arranca.
            string modo = req.Modo == "app" ? "app" : "completo";

            var cfg = SafeLoadOrbitX();
            string cacheDir = FirmwareMirror.ResolveCacheDir(cfg);
            string prodLo = req.Producto.ToLowerInvariant();
            string bin = modo == "completo"
                ? FirmwareMirror.PathFactory(cacheDir, prodLo, req.Version)
                : FirmwareMirror.PathBin(cacheDir, prodLo, req.Version);

            if (!File.Exists(bin))
            {
                await WriteErrorAsync(404, "AGP-USB-004", modo == "completo"
                    ? "Esta versión no tiene factory.bin para flasheo completo."
                    : "No encontré el firmware en el cache.").ConfigureAwait(false);
                return;
            }

            if (!_usb.Iniciar(bin, modo, req.Puerto, req.BorrarAntes, out string cod))
            {
                string codigo = cod ?? "AGP-USB-003";
                // AGP-USB-001 trae el placeholder literal "{port}" en su mensaje
                // amigable (ver AgpErrorMapper) — lo interpolamos con el puerto
                // real pedido para que el operario no vea texto de plantilla.
                string friendly = (AgpErrorMapper.FriendlyForCode(codigo) ?? "No se pudo iniciar el flasheo.")
                    .Replace("{port}", req.Puerto ?? "");
                await WriteErrorAsync(409, codigo, friendly).ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/usb/driver/instalar")]
        public async Task InstalarDriver()
        {
            var req = await ReadJsonBodyAsync<UsbDriverRequest>().ConfigureAwait(false);
            string drv = (req?.Driver == "cp210x" || req?.Driver == "ch340") ? req.Driver : "ambos";
            if (!UsbDriverInstaller.Instalar(_engineBaseDir, drv, out string cod))
            {
                string codigo = cod ?? "AGP-USB-006";
                string friendly = AgpErrorMapper.FriendlyForCode(codigo) ?? "No se pudo instalar el driver.";
                await WriteErrorAsync(500, codigo, friendly).ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }

        private OrbitXConfig SafeLoadOrbitX() { try { return OrbitXConfig.Load(); } catch { return new OrbitXConfig(); } }
    }
}
