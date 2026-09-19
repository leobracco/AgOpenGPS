// ============================================================================
// ChatController.cs — puente UI ↔ chat de soporte.
//
// La UI (PilotX.Desktop) no habla con el cloud: habla con ESTE host local, y el
// ChatSoporteService (que vive en el Engine junto al SoporteRemotoService) hace
// de puente hacia OrbitX. Acá sólo se expone su estado en memoria.
//
//   GET  /api/chat/estado    → { ok, rev, no_leidos }   (liviano, para el badge)
//   GET  /api/chat/mensajes  → { ok, rev, mensajes[] }  (marca leído + acelera poll)
//   POST /api/chat/enviar    ← { texto }  → { ok }
//   POST /api/chat/propuesta ← { ts, decision }  → { ok, estado, detalle }
//
// Si el service no está montado (pantalla sin vincular, o el Engine no lo
// arrancó) se responde { ok:false, error:"service-unavailable" }, igual que
// OrbitXController — la UI lo interpreta como "chat no disponible".
//
// PROPUESTAS DE CONFIG (capa 2): el bot puede mandar mensajes
// tipo:"propuesta_config" con una lista de cambios. La regla dura (memoria
// chat-soporte-ia-aceptacion) es que NADA se aplica solo: el operario Acepta o
// Rechaza en la pantalla. Al Aceptar, ESTE controller aplica únicamente los
// cambios de una LISTA BLANCA conservadora (tuning del lazo de dirección) vía
// ISteerConfigService.Save() — el mismo camino que usa la pantalla cuando el
// operario cambia el setting a mano. Todo lo que no está en la lista blanca
// (geometría, calibración de sensores, inversiones, hardware, red, pairing) NO
// se toca: queda como "ajuste manual" en el detalle.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ChatController : AgpControllerBase
    {
        private readonly AgroParallel.Soporte.ChatSoporteService _chat;
        private readonly ISteerConfigService _steer;

        public ChatController(AgroParallel.Soporte.ChatSoporteService chat, ISteerConfigService steer = null)
        {
            _chat = chat;
            _steer = steer;
        }

        /// <summary>Estado liviano para el badge del menú: NO marca leído ni
        /// acelera el poll del cloud. Lo consulta el shell cada tanto.</summary>
        [Route(HttpVerbs.Get, "/chat/estado")]
        public Task Estado()
        {
            if (_chat == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var (rev, noLeidos) = _chat.Estado();
            return WriteJsonAsync(new { ok = true, rev, noLeidos });
        }

        /// <summary>La conversación entera. La pide el panel abierto: el service
        /// marca todo como leído y pone el canal en modo activo (poll rápido).</summary>
        [Route(HttpVerbs.Get, "/chat/mensajes")]
        public Task Mensajes()
        {
            if (_chat == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var snap = _chat.Mensajes();
            return WriteJsonAsync(new
            {
                ok = true,
                rev = snap.Rev,
                noLeidos = snap.NoLeidos,
                mensajes = ProyectarMensajes(snap)
            });
        }

        public sealed class EnviarReq { public string texto { get; set; } }

        /// <summary>El operario manda un mensaje. Se relaya al cloud; el eco
        /// local lo agrega el service para que aparezca en el acto.</summary>
        [Route(HttpVerbs.Post, "/chat/enviar")]
        public async Task Enviar()
        {
            if (_chat == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }

            EnviarReq req;
            try { req = await ReadJsonBodyAsync<EnviarReq>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            string texto = req?.texto ?? "";
            if (string.IsNullOrWhiteSpace(texto))
            {
                await WriteJsonAsync(new { ok = false, error = "empty" });
                return;
            }

            bool ok = await _chat.EnviarAsync(texto).ConfigureAwait(false);
            await WriteJsonAsync(new { ok });
        }

        public sealed class PropuestaReq
        {
            public long ts { get; set; }
            public string decision { get; set; } // "aceptar" | "rechazar"
        }

        /// <summary>El operario resuelve una propuesta del bot. Rechazar sólo la
        /// marca. Aceptar aplica los cambios de la lista blanca (tuning de
        /// dirección) y deja el resto como ajuste manual; después avisa al cloud.</summary>
        [Route(HttpVerbs.Post, "/chat/propuesta")]
        public async Task Propuesta()
        {
            if (_chat == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }

            PropuestaReq req;
            try { req = await ReadJsonBodyAsync<PropuestaReq>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            if (req == null || req.ts <= 0)
            {
                await WriteJsonAsync(new { ok = false, error = "bad-ts" });
                return;
            }

            bool aceptar = string.Equals(req.decision, "aceptar", StringComparison.OrdinalIgnoreCase);

            if (!aceptar)
            {
                // Rechazo: no se toca ninguna config.
                bool avisado = await _chat.ResolverPropuestaAsync(req.ts, "rechazada", null).ConfigureAwait(false);
                await WriteJsonAsync(new { ok = true, estado = "rechazada", avisado });
                return;
            }

            var prop = _chat.BuscarPropuesta(req.ts);
            if (prop == null)
            {
                await WriteJsonAsync(new { ok = false, error = "propuesta-no-encontrada" });
                return;
            }

            var (estado, detalle) = AplicarSeguro(prop);
            bool ok2 = await _chat.ResolverPropuestaAsync(req.ts, estado, detalle).ConfigureAwait(false);
            await WriteJsonAsync(new { ok = true, estado, detalle, avisado = ok2 });
        }

        // =====================================================================
        //  Aplicación SEGURA de una propuesta
        // =====================================================================

        /// <summary>Aplica los cambios que caen en la lista blanca y pasan el
        /// rango; el resto queda como "ajuste manual". Devuelve el estado final
        /// ("aplicada" si tocó algo, "aceptada" si no) y un detalle legible.</summary>
        private (string estado, string detalle) AplicarSeguro(AgroParallel.Soporte.ChatPropuesta prop)
        {
            var aplicados = new List<string>();
            var manuales = new List<string>();

            if (_steer == null)
            {
                // Sin módulo de dirección detrás no aplicamos nada: todo manual.
                foreach (var c in prop.Cambios)
                    manuales.Add(DescribirManual(c));
                return ("aceptada", ArmarDetalle(aplicados, manuales));
            }

            SteerConfigDto dto;
            try { dto = _steer.Get(); }
            catch { dto = null; }

            if (dto == null)
            {
                foreach (var c in prop.Cambios) manuales.Add(DescribirManual(c));
                return ("aceptada", ArmarDetalle(aplicados, manuales));
            }

            bool algoAplicado = false;
            foreach (var c in prop.Cambios)
            {
                string clave = (c.Clave ?? "").Trim().ToLowerInvariant();
                if (ListaBlanca.TryGetValue(clave, out var aplicador) &&
                    aplicador(dto, c.ValorNuevo, out string comoQuedo))
                {
                    aplicados.Add(clave + " → " + comoQuedo);
                    algoAplicado = true;
                }
                else
                {
                    manuales.Add(DescribirManual(c));
                }
            }

            if (algoAplicado)
            {
                try { _steer.Save(dto); }
                catch (Exception ex)
                {
                    // Si el guardado falla, no mentimos: pasa todo a manual.
                    manuales.Clear();
                    foreach (var c in prop.Cambios) manuales.Add(DescribirManual(c));
                    return ("aceptada", "No se pudo aplicar (" + ex.Message + "). Aplicá a mano: "
                            + ArmarDetalle(new List<string>(), manuales));
                }
            }

            return (algoAplicado ? "aplicada" : "aceptada", ArmarDetalle(aplicados, manuales));
        }

        private static string DescribirManual(AgroParallel.Soporte.ChatCambio c)
            => (c.Clave ?? "?") + " → " + (c.ValorNuevo ?? "?") + " (ajuste manual)";

        private static string ArmarDetalle(List<string> aplicados, List<string> manuales)
        {
            var sb = new StringBuilder();
            if (aplicados.Count > 0)
                sb.Append("Aplicado: ").Append(string.Join("; ", aplicados)).Append('.');
            if (manuales.Count > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("Requiere ajuste manual: ").Append(string.Join("; ", manuales)).Append('.');
            }
            if (sb.Length == 0) sb.Append("Sin cambios.");
            return sb.ToString();
        }

        // =====================================================================
        //  LISTA BLANCA — sólo tuning del lazo de dirección/guiado.
        //
        //  Criterio: settings que cambian CÓMO de suave/firme guía, nunca la
        //  geometría física, la calibración de sensores, las inversiones, el
        //  hardware del motor, la red ni el pairing. Cada entrada valida rango
        //  contra los mismos límites que muestran los sliders de direccion.html.
        //  Si una clave no está acá, o el valor no parsea/queda fuera de rango,
        //  NO se aplica: cae a "ajuste manual".
        //
        //  EXPRESAMENTE FUERA (siempre manual): was_offset, counts_per_degree,
        //  ackerman, max_steer_angle, min_pwm, high_steer_pwm, invert_*,
        //  motor_drive, conv_type, steer_enable, imu_axis, danfoss, encoder,
        //  pressure_sensor, current_sensor, max_counts, sensor_limit,
        //  steer_in_reverse, stanley_pure (modo de guiado — lo cambia el
        //  operario con su selector), line_width/cm_per_pixel/guidance_bar/
        //  display_lightbar (pantalla).
        // =====================================================================

        private delegate bool Aplicador(SteerConfigDto dto, string valor, out string comoQuedo);

        // El bot y la pantalla hablan SIEMPRE en DISPLAY (el número que ve el
        // operario en la pantalla de Dirección). PilotX es el ÚNICO que convierte
        // a lo que guarda el DTO. Hay dos familias, según cómo lo muestra
        // DireccionPanel.cs:
        //   · FilaAjusteD  → el DTO guarda el DISPLAY directo (km/h, s, grados).
        //                    Se aplica tal cual con SetDisplay.
        //   · FilaAjuste   → el DTO guarda el CRUDO entero del slider y el display
        //                    es crudo×escala. Guardamos crudo = round(display/escala)
        //                    con SetEscaladoDbl/Int. Escalas tomadas de las llamadas
        //                    a FilaAjuste en DireccionPanel.cs (fuente de verdad).
        // Los rangos de validación son en DISPLAY (los mismos que ve el operario).
        private static readonly Dictionary<string, Aplicador> ListaBlanca = new Dictionary<string, Aplicador>()
        {
            // FilaAjuste: crudo = round(display / escala)
            ["proportional_gain"] = (SteerConfigDto d, string v, out string q) => SetEscaladoInt(v, 1, 200, 1, x => d.ProportionalGain = x, 0, "", out q),
            ["hold_look_ahead"] = (SteerConfigDto d, string v, out string q) => SetEscaladoDbl(v, 1.0, 7.0, 0.1, x => d.HoldLookAhead = x, 1, "s", out q),
            ["look_ahead_mult"] = (SteerConfigDto d, string v, out string q) => SetEscaladoDbl(v, 0.5, 6.0, 0.1, x => d.LookAheadMult = x, 1, "", out q),
            ["acquire_factor"] = (SteerConfigDto d, string v, out string q) => SetEscaladoDbl(v, 0.20, 3.00, 0.01, x => d.AcquireFactor = x, 2, "", out q),
            ["integral_pp"] = (SteerConfigDto d, string v, out string q) => SetEscaladoInt(v, 0, 100, 1, x => d.IntegralPp = x, 0, "", out q),
            ["stanley_gain"] = (SteerConfigDto d, string v, out string q) => SetEscaladoDbl(v, 0.1, 4.0, 0.1, x => d.StanleyGain = x, 1, "", out q),
            ["heading_error_gain"] = (SteerConfigDto d, string v, out string q) => SetEscaladoDbl(v, 0.1, 1.5, 0.1, x => d.HeadingErrorGain = x, 1, "", out q),
            ["integral_stanley"] = (SteerConfigDto d, string v, out string q) => SetEscaladoInt(v, 0, 100, 1, x => d.IntegralStanley = x, 0, "", out q),
            ["dead_zone_delay"] = (SteerConfigDto d, string v, out string q) => SetEscaladoInt(v, 1, 50, 1, x => d.DeadZoneDelay = x, 0, "", out q),
            ["u_turn_comp"] = (SteerConfigDto d, string v, out string q) => SetEscaladoDbl(v, 2, 20, 1, x => d.UTurnComp = x, 0, "", out q),
            ["side_hill_comp"] = (SteerConfigDto d, string v, out string q) => SetEscaladoInt(v, 0.00, 0.30, 0.01, x => d.SideHillComp = x, 2, "°", out q),
            // FilaAjusteD: el DTO ya está en display
            ["dead_zone_heading"] = (SteerConfigDto d, string v, out string q) => SetDisplay(v, 0, 5, x => d.DeadZoneHeading = x, 1, "°", out q),
            ["min_steer_speed"] = (SteerConfigDto d, string v, out string q) => SetDisplay(v, 0, 10, x => d.MinSteerSpeed = x, 1, "km/h", out q),
            ["max_steer_speed"] = (SteerConfigDto d, string v, out string q) => SetDisplay(v, 1, 40, x => d.MaxSteerSpeed = x, 0, "km/h", out q),
            ["guidance_speed_limit"] = (SteerConfigDto d, string v, out string q) => SetDisplay(v, 1, 40, x => d.GuidanceSpeedLimit = x, 0, "km/h", out q),
            ["snap_distance"] = (SteerConfigDto d, string v, out string q) => SetDisplay(v, 1, 100, x => d.SnapDistance = x, 0, "", out q),
            ["guidance_look_ahead"] = (SteerConfigDto d, string v, out string q) => SetDisplay(v, 0.1, 5, x => d.GuidanceLookAhead = x, 1, "s", out q),
        };

        // Parsea el número de PANTALLA (display). Acepta coma o punto decimal.
        private static bool ParseDisplay(string valor, out double v)
        {
            var s = (valor ?? "").Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static string FormatDisplay(double v, int dec, string unidad)
            => v.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
               + (string.IsNullOrEmpty(unidad) ? "" : " " + unidad);

        // FilaAjusteD: el DTO guarda el display directo (double).
        private static bool SetDisplay(string valor, double dmin, double dmax, Action<double> set, int dec, string unidad, out string comoQuedo)
        {
            comoQuedo = null;
            if (!ParseDisplay(valor, out double v)) return false;
            if (v < dmin || v > dmax) return false;
            set(v);
            comoQuedo = FormatDisplay(v, dec, unidad);
            return true;
        }

        // FilaAjuste con campo double en el DTO: crudo = round(display / escala).
        private static bool SetEscaladoDbl(string valor, double dmin, double dmax, double escala, Action<double> setCrudo, int dec, string unidad, out string comoQuedo)
        {
            comoQuedo = null;
            if (!ParseDisplay(valor, out double v)) return false;
            if (v < dmin || v > dmax) return false;
            setCrudo(Math.Round(v / escala));
            comoQuedo = FormatDisplay(v, dec, unidad);
            return true;
        }

        // FilaAjuste con campo int en el DTO: crudo = round(display / escala).
        private static bool SetEscaladoInt(string valor, double dmin, double dmax, double escala, Action<int> setCrudo, int dec, string unidad, out string comoQuedo)
        {
            comoQuedo = null;
            if (!ParseDisplay(valor, out double v)) return false;
            if (v < dmin || v > dmax) return false;
            setCrudo((int)Math.Round(v / escala));
            comoQuedo = FormatDisplay(v, dec, unidad);
            return true;
        }

        // =====================================================================
        //  proyección de mensajes al wire
        // =====================================================================

        private static object[] ProyectarMensajes(AgroParallel.Soporte.ChatSnapshot snap)
        {
            var arr = new object[snap.Mensajes.Count];
            for (int i = 0; i < arr.Length; i++)
            {
                var m = snap.Mensajes[i];
                if (m.Tipo == "propuesta_config" && m.Payload != null)
                {
                    var cambios = new object[m.Payload.Cambios.Count];
                    for (int j = 0; j < cambios.Length; j++)
                    {
                        var c = m.Payload.Cambios[j];
                        cambios[j] = new { clave = c.Clave, valor_actual = c.ValorActual, valor_nuevo = c.ValorNuevo };
                    }
                    arr[i] = new
                    {
                        rol = m.Rol,
                        texto = m.Texto,
                        ts = m.Ts,
                        tipo = m.Tipo,
                        payload = new { cambios, estado = m.Payload.Estado }
                    };
                }
                else
                {
                    arr[i] = new { rol = m.Rol, texto = m.Texto, ts = m.Ts, tipo = m.Tipo ?? "texto" };
                }
            }
            return arr;
        }
    }
}
