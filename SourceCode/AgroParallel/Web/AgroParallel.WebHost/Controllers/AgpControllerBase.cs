// ============================================================================
// AgpControllerBase.cs — Base común para todos los controllers del WebHost.
//
// Unifica la serialización JSON (snake_case vía AgpJson) y la lectura del body
// para evitar que cada controller reimplemente sus propias JsonSerializerOptions
// y su helper de lectura de stream.
//
// API EmbedIO usada:
//   · HttpContext.SendStringAsync(json, mime, encoding) — escribe la respuesta
//     sin tener que manipular OutputStream directamente.
//   · HttpContext.Request.InputStream + StreamReader — lectura del body (sin
//     GetRequestBodyAsStringAsync, que no existe en EmbedIO 3.5.2).
//   · HttpContext.Response.StatusCode — para errores HTTP.
// ============================================================================

using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using EmbedIO;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    /// <summary>
    /// Base de todos los controllers: JSON out snake_case vía AgpJson, body-in unificado.
    /// </summary>
    public abstract class AgpControllerBase : WebApiController
    {
        private const string JsonMime = "application/json";

        /// <summary>Serializa <paramref name="payload"/> con AgpJson (snake_case) y escribe la respuesta.</summary>
        protected Task WriteJsonAsync<T>(T payload)
        {
            string json = AgpJson.Serialize(payload);
            return HttpContext.SendStringAsync(json, JsonMime, Encoding.UTF8);
        }

        /// <summary>Lee el body crudo de la request como string UTF-8.</summary>
        protected async Task<string> ReadBodyAsync()
        {
            using (var sr = new StreamReader(HttpContext.Request.InputStream, Encoding.UTF8))
                return await sr.ReadToEndAsync().ConfigureAwait(false);
        }

        /// <summary>Deserializa el body de la request a <typeparamref name="T"/> con AgpJson (case-insensitive).</summary>
        protected async Task<T> ReadJsonBodyAsync<T>()
        {
            string body = await ReadBodyAsync().ConfigureAwait(false);
            return AgpJson.Deserialize<T>(body);
        }

        /// <summary>
        /// Escribe una respuesta de error con status HTTP, código de error y mensaje amigable.
        /// </summary>
        protected Task WriteErrorAsync(int status, string code, string friendly, string technical = null)
        {
            HttpContext.Response.StatusCode = status;
            return WriteJsonAsync(new { error = code, mensaje = friendly, detalle = technical });
        }
    }
}
