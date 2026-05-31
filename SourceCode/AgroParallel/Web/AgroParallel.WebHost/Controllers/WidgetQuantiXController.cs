// ============================================================================
// WidgetQuantiXController.cs
// Backend del widget HTML de QuantiX en la pantalla principal de PilotX
// (overlay WebView2 sobre el OpenGL). Reemplaza al WinForms
// ShapefileLegendControl.
//
// Endpoints:
//   GET  /api/widget-quantix/state               → snapshot UI-ready
//   POST /api/widget-quantix/manual              → setea manual_mode/manual_dosis
//
// La selección de nodo (paginador) la maneja el frontend (localStorage); el
// backend expone TODOS los nodos QuantiX configurados y deja que la UI elija
// cuál renderizar. Esto evita estado de sesión en el server.
//
// La dosis real se calcula con la fórmula inversa del bridge:
//   pps × meterCal × 10 / (ancho × velMs)  → kg/ha
// La dosis objetivo viene del IQuantiXRuntimeService (única fuente de verdad
// que ya respeta la prioridad Manual > DosisFija > CampoDosis > Shape global).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.QuantiX;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SysJson = System.Text.Json.JsonSerializer;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class WidgetQuantiXController : WebApiController
    {
        private readonly IQuantiXRuntimeService _runtime;
        private readonly INodoRegistryService _registry;
        private readonly IAogStateProvider _state;

        public WidgetQuantiXController(
            IQuantiXRuntimeService runtime,
            INodoRegistryService registry,
            IAogStateProvider state)
        {
            _runtime = runtime;
            _registry = registry;
            _state = state;
        }

        [Route(HttpVerbs.Get, "/widget-quantix/state")]
        public async Task GetState()
        {
            // Config en disco (single source of truth de motores + manual).
            MotoresConfig mc;
            try { mc = MotoresConfig.Load(); }
            catch { mc = new MotoresConfig(); }

            // Snapshot runtime (objetivo efectivo por motor, respeta ManualMode).
            QuantiXRuntimeSnapshot rt = _runtime != null ? _runtime.GetSnapshot() : null;

            // Live MQTT por nodo (online, pps_real por motor).
            var liveByUid = new Dictionary<string, NodoStatus>(StringComparer.OrdinalIgnoreCase);
            if (_registry != null)
            {
                foreach (var n in _registry.GetAll())
                {
                    if (n == null || string.IsNullOrEmpty(n.Uid)) continue;
                    if (n.Type == null || n.Type.IndexOf("quantix", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    liveByUid[n.Uid] = n;
                }
            }

            var aog = _state != null ? _state.GetSnapshot() : null;
            double speedKmh = aog != null ? aog.AvgSpeed : 0;
            double anchoM = aog != null && aog.ToolWidth > 0 ? aog.ToolWidth : 0;
            double velMs = speedKmh / 3.6;

            var nodosOut = new List<object>();
            if (mc != null && mc.Nodos != null)
            {
                foreach (var nodo in mc.Nodos)
                {
                    if (nodo == null || string.IsNullOrEmpty(nodo.Uid)) continue;
                    if (!nodo.Habilitado) continue;

                    NodoStatus live;
                    liveByUid.TryGetValue(nodo.Uid, out live);

                    var motoresOut = new List<object>();
                    int motCount = nodo.Motores != null ? nodo.Motores.Length : 0;
                    for (int mi = 0; mi < motCount && mi < 2; mi++)
                    {
                        var motor = nodo.Motores[mi];
                        if (motor == null) continue;

                        // Objetivo: del snapshot runtime (ya respeta ManualMode).
                        double objetivo = 0;
                        if (rt != null && rt.Motores != null)
                        {
                            foreach (var r in rt.Motores)
                            {
                                if (r.NodoUid == nodo.Uid && r.MotorIndex == mi)
                                {
                                    objetivo = r.DosisObjetivo;
                                    break;
                                }
                            }
                        }

                        // Real: inversa pps→kg/ha usando live del nodo.
                        double ppsReal = 0;
                        if (live != null && live.MotorsLive != null)
                        {
                            foreach (var ml in live.MotorsLive)
                            {
                                if (ml.Id == mi) { ppsReal = ml.PpsReal; break; }
                            }
                        }
                        double real = 0;
                        if (velMs > 0.1 && motor.MeterCal > 0 && anchoM > 0)
                            real = ppsReal * motor.MeterCal * 10.0 / (anchoM * velMs);

                        bool activo = (live != null && live.Online) && (ppsReal > 0 || objetivo > 0);

                        motoresOut.Add(new
                        {
                            idx = mi,
                            nombre = motor.Nombre ?? ("M" + mi),
                            manual_mode = motor.ManualMode,
                            manual_dosis = motor.ManualDosis,
                            dosis_fija_config = motor.DosisFija,
                            objetivo,
                            real,
                            activo
                        });
                    }

                    nodosOut.Add(new
                    {
                        uid = nodo.Uid,
                        nombre = nodo.Nombre ?? "Nodo QuantiX",
                        online = live != null && live.Online,
                        motores = motoresOut
                    });
                }
            }

            string json = SysJson.Serialize(new
            {
                ok = true,
                connected = _registry != null,
                speed_kmh = speedKmh,
                ancho_m = anchoM,
                nodos = nodosOut
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }

        // Body: { "uid": "...", "motor_idx": 0, "manual": true, "dosis": 35.5 }
        [Route(HttpVerbs.Post, "/widget-quantix/manual")]
        public async Task<object> PostManual()
        {
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);

            ManualReq req;
            try
            {
                req = SysJson.Deserialize<ManualReq>(body, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex) { return new { ok = false, error = "bad-json: " + ex.Message }; }
            if (req == null || string.IsNullOrEmpty(req.Uid))
                return new { ok = false, error = "uid-required" };
            if (req.MotorIdx < 0 || req.MotorIdx > 1)
                return new { ok = false, error = "motor-idx-oob" };

            MotoresConfig mc;
            try { mc = MotoresConfig.Load(); }
            catch (Exception ex) { return new { ok = false, error = "load: " + ex.Message }; }
            if (mc == null || mc.Nodos == null) return new { ok = false, error = "no-config" };

            QxNodoConfig target = null;
            foreach (var n in mc.Nodos)
            {
                if (string.Equals(n.Uid, req.Uid, StringComparison.OrdinalIgnoreCase)) { target = n; break; }
            }
            if (target == null) return new { ok = false, error = "nodo-not-found" };
            if (target.Motores == null || req.MotorIdx >= target.Motores.Length)
                return new { ok = false, error = "motor-not-found" };

            var motor = target.Motores[req.MotorIdx];
            motor.ManualMode = req.Manual;
            if (req.Manual && req.Dosis > 0)
                motor.ManualDosis = req.Dosis;
            // Si pasa a AUTO, NO tocamos ManualDosis: queda persistido para el
            // próximo MAN (no se pierde lo que el operario tipeó).

            try { mc.Save(); }
            catch (Exception ex) { return new { ok = false, error = "save: " + ex.Message }; }

            return new
            {
                ok = true,
                uid = req.Uid,
                motor_idx = req.MotorIdx,
                manual_mode = motor.ManualMode,
                manual_dosis = motor.ManualDosis
            };
        }

        private sealed class ManualReq
        {
            public string Uid { get; set; }
            public int MotorIdx { get; set; }
            public bool Manual { get; set; }
            public double Dosis { get; set; }
        }
    }
}
