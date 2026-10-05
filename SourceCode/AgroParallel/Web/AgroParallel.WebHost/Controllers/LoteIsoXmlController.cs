// ============================================================================
// LoteIsoXmlController.cs — export del lote abierto a ISO-XML.
//   POST /api/lotes/exportar-isoxml  {destino, version} → IsoXmlExportResultadoDto
// destino: carpeta o archivo .XML que eligió el operario en el explorador
// (pendrive); el TASKDATA.XML queda en <destino>\TASKDATA\. version: "4"
// (defecto, 4.3) o "3" (3.3, pantallas viejas). Wire snake_case por AgpJson.
// Si el host no inyectó el delegado (Android hoy) el controller no se registra.
// ============================================================================

using System;
using System.Threading.Tasks;
using AgroParallel.Models;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class LoteIsoXmlController : AgpControllerBase
    {
        private readonly Func<string, string, IsoXmlExportResultadoDto> _exportar;

        public LoteIsoXmlController(Func<string, string, IsoXmlExportResultadoDto> exportar)
        {
            _exportar = exportar;
        }

        [Route(HttpVerbs.Post, "/lotes/exportar-isoxml")]
        public async Task PostExportar()
        {
            Body body = null;
            try { body = await ReadJsonBodyAsync<Body>(); } catch { }
            if (body == null || string.IsNullOrWhiteSpace(body.Destino))
            {
                await WriteJsonAsync(new IsoXmlExportResultadoDto { Ok = false, Error = "Elegí dónde guardar (el pendrive)." });
                return;
            }
            IsoXmlExportResultadoDto r;
            try { r = _exportar(body.Destino, body.Version ?? "4"); }
            catch (Exception ex) { r = new IsoXmlExportResultadoDto { Ok = false, Error = "No se pudo exportar: " + ex.Message }; }
            await WriteJsonAsync(r ?? new IsoXmlExportResultadoDto { Ok = false, Error = "No se pudo exportar." });
        }

        private sealed class Body
        {
            [System.Text.Json.Serialization.JsonPropertyName("destino")]
            public string Destino { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("version")]
            public string Version { get; set; }
        }
    }
}
