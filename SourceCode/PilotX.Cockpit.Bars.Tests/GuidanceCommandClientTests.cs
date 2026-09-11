using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.Tests;

public class GuidanceCommandClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastUrl;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            LastBody = request.Content == null ? null
                     : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task SendAsync_PostsCommandToGuidanceEndpoint()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var client = new GuidanceCommandClient(http, "http://127.0.0.1:5180/");

        var ok = await client.SendAsync("autosteer");

        Assert.That(ok, Is.True);
        Assert.That(handler.LastUrl, Is.EqualTo("http://127.0.0.1:5180/api/aog/guidance/command"));
        Assert.That(handler.LastBody, Does.Contain("\"cmd\":\"autosteer\""));
    }
}
