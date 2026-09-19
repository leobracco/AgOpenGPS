// ============================================================================
// QuantiXPidController.cs
//   GET  /api/quantix/graph-pid?uid=<uid>&m=<idx>  -> ultimas muestras del motor
//   POST /api/quantix/pid-marca                    -> clava una marca en todos
//
// Lee del buffer en memoria del recorder, no del disco: es el mismo dato que se
// esta escribiendo, sin volver a leerlo.
// ============================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.QuantiX;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuantiXPidController : AgpControllerBase
    {
        public sealed class MarcaDto
        {
            public string Texto { get; set; }
        }

        [Route(HttpVerbs.Get, "/quantix/graph-pid")]
        public Task GetGraphPid([QueryField] string uid, [QueryField] int m)
        {
            var rec = QxPidRecorder.Instance;
            if (rec == null) return WriteJsonAsync(new { ok = false, motivo = "sin_registro" });

            var motores = new List<object>();
            foreach (var s in rec.Motores())
                motores.Add(new { uid = s.Uid, m = s.MotorIdx, nombre = s.Nombre });

            var muestras = new List<object>();
            if (!string.IsNullOrEmpty(uid))
            {
                var buf = rec.BufferDe(uid, m);
                for (int i = 0; i < buf.Count; i++)
                    muestras.Add(new
                    {
                        rpm_real = buf[i].RpmReal,
                        rpm_target = buf[i].RpmTarget,
                        vel_motor = buf[i].VelMotorKmh,
                        vel_gps = buf[i].VelGpsKmh,
                        load_pct = buf[i].LoadPct,
                    });
            }

            return WriteJsonAsync(new { ok = true, motores = motores, muestras = muestras });
        }

        /// <summary>Diagnostico de sintonia de TODOS los motores de la corrida en
        /// curso. El numero de cada recomendacion sale de la medicion, no de un
        /// modelo: ver QxPidAnalisis.</summary>
        [Route(HttpVerbs.Get, "/quantix/pid-diagnostico")]
        public Task GetDiagnostico()
        {
            var rec = QxPidRecorder.Instance;
            if (rec == null) return WriteJsonAsync(new { ok = false, motivo = "sin_registro" });

            var cfg = rec.ConfigDeLaCorrida;
            var lista = new List<object>();

            foreach (var vistoEnLaCorrida in rec.Motores())
            {
                var ms = rec.MuestrasDe(vistoEnLaCorrida.Uid, vistoEnLaCorrida.MotorIdx);
                double kp = 0, ki = 0, kd = 0;
                var mc = BuscarMotor(cfg, vistoEnLaCorrida.Uid, vistoEnLaCorrida.MotorIdx);
                if (mc != null) { kp = mc.Kp; ki = mc.Ki; kd = mc.Kd; }

                var d = QxPidAnalisis.Analizar(ms, kp, ki, kd);
                lista.Add(new
                {
                    uid = d.Uid,
                    m = d.MotorIdx,
                    nombre = d.Nombre,
                    veredicto = d.Veredicto.ToString().ToLowerInvariant(),
                    explicacion = d.Explicacion,
                    rpm_desvio = d.RpmDesvio,
                    error_medio_pct = d.ErrorMedioPct,
                    tiempo_saturado_pct = d.TiempoSaturadoPct,
                    vel_desvio_pct = d.VelDesvioPct,
                    oscilacion_hz = d.OscilacionHz,
                    muestras = d.Muestras,
                    hay_recomendacion = d.HayRecomendacion,
                    parametro = d.Parametro,
                    valor_actual = d.ValorActual,
                    valor_sugerido = d.ValorSugerido,
                });
            }

            return WriteJsonAsync(new { ok = true, motores = lista });
        }

        public sealed class AplicarDto
        {
            public string Uid { get; set; }
            public int M { get; set; }
            /// <summary>"kp", "ki" o "kd".</summary>
            public string Parametro { get; set; }
            public double Valor { get; set; }
        }

        /// <summary>Escribe la ganancia nueva en ese motor. El bridge recarga la
        /// config cada 2 s, asi que se propaga solo.</summary>
        [Route(HttpVerbs.Post, "/quantix/pid-aplicar")]
        public async Task PostAplicar()
        {
            var dto = await ReadJsonBodyAsync<AplicarDto>().ConfigureAwait(false);
            if (dto == null || string.IsNullOrEmpty(dto.Uid) || string.IsNullOrEmpty(dto.Parametro))
            {
                await WriteJsonAsync(new { ok = false, motivo = "faltan_datos" }).ConfigureAwait(false);
                return;
            }

            // Guard: con el motor girando no se cambian ganancias. Un cambio de PID
            // en caliente le pega un tiron al dosificador en pleno lote.
            var rec = QxPidRecorder.Instance;
            if (rec != null)
            {
                var ultimas = rec.BufferDe(dto.Uid, dto.M);
                if (ultimas.Count > 0)
                {
                    var u = ultimas[ultimas.Count - 1];
                    if (u.PpsTarget > 0.01 || (u.RpmReal.HasValue && u.RpmReal.Value > 0.5))
                    {
                        await WriteJsonAsync(new { ok = false, motivo = "motor_girando" }).ConfigureAwait(false);
                        return;
                    }
                }
            }

            var cfg = MotoresConfig.Load();
            var mc = BuscarMotor(cfg, dto.Uid, dto.M);
            if (mc == null)
            {
                await WriteJsonAsync(new { ok = false, motivo = "motor_no_encontrado" }).ConfigureAwait(false);
                return;
            }

            double antes;
            switch ((dto.Parametro ?? "").ToLowerInvariant())
            {
                case "kp": antes = mc.Kp; mc.Kp = dto.Valor; break;
                case "ki": antes = mc.Ki; mc.Ki = dto.Valor; break;
                case "kd": antes = mc.Kd; mc.Kd = dto.Valor; break;
                default:
                    await WriteJsonAsync(new { ok = false, motivo = "parametro_invalido" }).ConfigureAwait(false);
                    return;
            }

            cfg.Save();
            await WriteJsonAsync(new { ok = true, antes = antes, ahora = dto.Valor }).ConfigureAwait(false);
        }

        private static QxMotorConfig BuscarMotor(MotoresConfig cfg, string uid, int idx)
        {
            if (cfg == null || cfg.Nodos == null) return null;
            foreach (var nodo in cfg.Nodos)
            {
                if (nodo == null || nodo.Motores == null) continue;
                if (!string.Equals(nodo.Uid, uid, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (idx < 0 || idx >= nodo.Motores.Length) return null;
                return nodo.Motores[idx];
            }
            return null;
        }

        [Route(HttpVerbs.Post, "/quantix/pid-marca")]
        public async Task PostMarca()
        {
            var dto = await ReadJsonBodyAsync<MarcaDto>().ConfigureAwait(false);
            var rec = QxPidRecorder.Instance;
            if (rec == null)
            {
                await WriteJsonAsync(new { ok = false, motivo = "sin_registro" }).ConfigureAwait(false);
                return;
            }
            rec.Marcar(dto != null ? dto.Texto : null);
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }
    }
}
