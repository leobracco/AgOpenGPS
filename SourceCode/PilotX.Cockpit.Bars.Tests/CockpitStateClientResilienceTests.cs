// Regresión de un bug real encontrado en el emulador Android (2026-07-27):
// la barra superior mostraba "0,0 KM/H" y "SIN FIX" para siempre mientras la
// API del propio proceso devolvía velocidad y fix reales.
//
// Causa: HttpClient.Timeout NO lanza TimeoutException sino
// TaskCanceledException, que hereda de OperationCanceledException — y el loop
// de polling la trataba como "me pidieron parar" y hacía return. O sea: UN
// solo request lento (trivial en un emulador o mientras el web host todavía
// está levantando) mataba el polling de forma PERMANENTE y la UI se quedaba
// congelada en sus valores por defecto. En Desktop casi no se notaba porque la
// máquina es rápida y el host ya está arriba.
//
// El test levanta un servidor HTTP mínimo (TcpListener, sin HttpListener para
// no depender de ACLs de URL en Windows) que cuelga el primer request más allá
// del timeout del cliente y después contesta normal. Si el loop sobrevive al
// timeout, llega un snapshot.

using NUnit.Framework;
using PilotX.Cockpit.Bars.Services;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Cockpit.Bars.Tests;

public class CockpitStateClientResilienceTests
{
    /// <summary>Servidor HTTP mínimo: el primer request se cuelga
    /// <paramref name="stallMs"/> ms (para disparar el timeout del cliente) y
    /// los siguientes contestan el JSON al toque.</summary>
    private sealed class StallingServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _requests;

        public int Port { get; }
        public int Requests => Volatile.Read(ref _requests);

        public StallingServer(string json, int stallMs)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(json, stallMs, _cts.Token);
        }

        private async Task AcceptLoopAsync(string json, int stallMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        int n = Interlocked.Increment(ref _requests);
                        try
                        {
                            var stream = client.GetStream();
                            var buf = new byte[4096];
                            await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);

                            // Primer request: no contesto hasta pasado el timeout.
                            if (n == 1) await Task.Delay(stallMs, ct).ConfigureAwait(false);

                            var body = Encoding.UTF8.GetBytes(json);
                            var head = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                                "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);
                            await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
                            await stream.FlushAsync(ct).ConfigureAwait(false);
                        }
                        catch { /* cliente cortó por timeout: esperado en el 1ro */ }
                    }
                }, ct);
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }

    [Test]
    public void PollingSurvivesRequestTimeout()
    {
        // Cuelga el primer request bastante más que el timeout del cliente (2 s).
        using var server = new StallingServer("{\"avg_speed\":2.2224,\"fix_quality\":8}", stallMs: 6000);

        using var client = new CockpitStateClient($"http://127.0.0.1:{server.Port}/", intervalMs: 100);

        CockpitSnapshot? received = null;
        using var got = new ManualResetEventSlim(false);
        client.SnapshotReceived += s => { received = s; got.Set(); };

        client.Start();

        // Con el bug, el loop moría en el timeout del primer request y NO
        // llegaba ningún snapshot nunca más.
        bool ok = got.Wait(TimeSpan.FromSeconds(20));
        client.Stop();

        Assert.That(ok, Is.True,
            "el polling murió en el primer timeout: no llegó ningún snapshot posterior");
        Assert.That(received, Is.Not.Null);
        Assert.That(received!.AvgSpeed, Is.EqualTo(2.2224).Within(0.0001));
        Assert.That(received.FixQuality, Is.EqualTo(8));
        Assert.That(server.Requests, Is.GreaterThan(1),
            "tendría que haber reintentado despues del timeout");
    }
}
