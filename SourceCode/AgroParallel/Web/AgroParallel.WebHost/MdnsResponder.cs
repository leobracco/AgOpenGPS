// ============================================================================
// MdnsResponder.cs
// Mini-responder mDNS / Bonjour: publica el hostname "<name>.local" -> IPs LAN
// de la PC del tractor, asi el operario puede tipear (o el celular resolver)
// http://agroparallel.local:5180/m/ sin depender de IP fija.
//
// Usa Makaretu.Dns.Multicast (MIT). Si la libreria/los permisos fallan, el
// constructor lanza y el caller hace try/catch para seguir sin mDNS.
//
// NO publicamos servicios DNS-SD (_http._tcp) — solo responder de hostname,
// que es lo unico que necesitan los browsers para resolver agroparallel.local.
// ============================================================================

using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;

namespace AgroParallel.WebHost
{
    internal sealed class MdnsResponder : IDisposable
    {
        private readonly string _hostnameLocal;   // "agroparallel.local"
        private MulticastService _mdns;

        public MdnsResponder(string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "agroparallel";
            _hostnameLocal = baseName.ToLowerInvariant() + ".local";
        }

        public void Start()
        {
            _mdns = new MulticastService();
            _mdns.QueryReceived += OnQueryReceived;
            _mdns.Start();
        }

        public void Stop()
        {
            if (_mdns == null) return;
            try { _mdns.QueryReceived -= OnQueryReceived; } catch { }
            try { _mdns.Stop(); } catch { }
            _mdns = null;
        }

        public void Dispose() => Stop();

        private void OnQueryReceived(object sender, MessageEventArgs e)
        {
            try
            {
                var msg = e.Message;
                if (msg == null || msg.Questions == null) return;

                Message response = null;
                foreach (var q in msg.Questions)
                {
                    if (q == null || q.Name == null) continue;
                    if (!string.Equals(q.Name.ToString().TrimEnd('.'),
                                       _hostnameLocal,
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (q.Type == DnsType.A || q.Type == DnsType.ANY)
                    {
                        if (response == null) response = msg.CreateResponse();
                        foreach (var ip in GetLanIPv4())
                        {
                            response.Answers.Add(new ARecord
                            {
                                Name = new DomainName(_hostnameLocal),
                                Address = ip,
                                TTL = TimeSpan.FromSeconds(120)
                            });
                        }
                    }
                }

                if (response != null && response.Answers.Count > 0)
                {
                    _mdns.SendAnswer(response);
                }
            }
            catch
            {
                // Silenciamos: si una query suelta falla, no queremos tirar el server.
            }
        }

        // IPv4 de interfaces UP, no loopback, no tunnel, no link-local (169.254.x.x).
        // Esto es lo que respondemos a queries A de "agroparallel.local".
        public static IPAddress[] GetLanIPv4()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(a.Address)
                            && !a.Address.ToString().StartsWith("169.254."))
                .Select(a => a.Address)
                .Distinct()
                .ToArray();
        }
    }
}
