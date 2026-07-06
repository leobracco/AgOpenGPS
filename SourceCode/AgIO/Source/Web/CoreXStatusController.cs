using System.Threading.Tasks;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    /// <summary>GET /api/corex/status — snapshot JSON (snake_case) @1Hz de polling.</summary>
    public sealed class CoreXStatusController : AgpControllerBase
    {
        [Route(HttpVerbs.Get, "/corex/status")]
        public Task Status() => WriteJsonAsync(CoreXState.Instance.Snapshot());
    }
}
