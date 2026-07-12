// CoreXBridgeService.cs — proxy HTTP delgado del Hub hacia CoreX (AgIO),
// GET http://127.0.0.1:5181/api/corex/status. CoreX es un sidecar local de
// puerto fijo (no hay IP/puerto que configurar como con CoreX-ECU), así que
// el timeout es corto: si no contesta en ese lapso por loopback, no está
// levantado. Parseamos el JSON con JsonDocument en vez de tipar el DTO
// completo de CoreXState (vive en el proyecto AgIO, no referenciado acá) —
// solo extraemos los campos que necesitan las pills de estado del Hub.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class CoreXBridgeService : ICoreXBridgeService
    {
        private const string BaseUrl = "http://127.0.0.1:5181";
        private const int TimeoutMs = 1500;

        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        public async Task<CoreXBridgeStatusDto> GetStatusAsync()
        {
            string url = BaseUrl + "/api/corex/status";
            try
            {
                using (var cts = new CancellationTokenSource(TimeoutMs))
                {
                    var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return Stub("AGP-NET-101", "CoreX respondió HTTP " + (int)resp.StatusCode + ".");

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement;
                        return new CoreXBridgeStatusDto
                        {
                            Ok = true,
                            GpsAlive = GetBool(root, "gps", "alive"),
                            SteerConfigured = GetBool(root, "modules", "steer_configured"),
                            SteerHello = GetBool(root, "modules", "steer_hello"),
                            MachineConfigured = GetBool(root, "modules", "machine_configured"),
                            MachineHello = GetBool(root, "modules", "machine_hello"),
                            ImuConfigured = GetBool(root, "modules", "imu_configured"),
                            ImuHello = GetBool(root, "modules", "imu_hello")
                        };
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return Stub("AGP-NET-002", "CoreX no respondió a tiempo. ¿Está corriendo el servicio?");
            }
            catch (HttpRequestException)
            {
                return Stub("AGP-NET-000", "No se pudo conectar con CoreX (127.0.0.1:5181).");
            }
            catch (Exception)
            {
                return Stub("AGP-NET-001", "Error consultando el estado de CoreX.");
            }
        }

        private static bool GetBool(JsonElement root, string objProp, string boolProp)
        {
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty(objProp, out var o) || o.ValueKind != JsonValueKind.Object) return false;
            return o.TryGetProperty(boolProp, out var v) && v.ValueKind == JsonValueKind.True;
        }

        private static CoreXBridgeStatusDto Stub(string code, string friendly)
        {
            return new CoreXBridgeStatusDto { Ok = false, ErrorCode = code, Error = friendly };
        }
    }
}
