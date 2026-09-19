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

        [Route(HttpVerbs.Post, "/quantix/pid-marca")]
        public async Task PostMarca()
        {
            var dto = await HttpContext.GetRequestDataAsync<MarcaDto>().ConfigureAwait(false);
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
