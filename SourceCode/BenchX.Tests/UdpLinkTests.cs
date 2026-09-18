using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BenchX.Red;
using NUnit.Framework;

namespace BenchX.Tests;

public class UdpLinkTests
{
    // Puertos altos poco probables; si algo los tiene tomados el test de bind lo delata.
    private const int Escucha = 48888;
    private const int Destino = 49999;

    [Test]
    public void Envia_broadcast_por_loopback_y_recibe()
    {
        // BenchX manda a 127.255.255.255:destino; un socket que escucha en
        // 0.0.0.0:destino lo recibe (mismo esquema que el banco loopback real).
        using var receptor = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receptor.Bind(new IPEndPoint(IPAddress.Any, Destino));
        receptor.ReceiveTimeout = 3000;

        using var link = new UdpLink(127, 255, 255, Escucha, Destino);
        Assert.That(link.Conectado, Is.True, link.ErrorBind);
        link.Enviar("$GPGGA,prueba*00\r\n");

        var buf = new byte[256];
        int n = receptor.Receive(buf);
        Assert.That(System.Text.Encoding.ASCII.GetString(buf, 0, n), Does.StartWith("$GPGGA"));
        Assert.That(link.Tx, Is.EqualTo(1));
    }

    [Test]
    public void Recibe_datagramas_en_el_puerto_de_escucha()
    {
        using var link = new UdpLink(127, 255, 255, Escucha, Destino);
        byte[]? recibido = null;
        using var señal = new ManualResetEventSlim();
        link.DatagramaRecibido += d => { recibido = d; señal.Set(); };

        using var emisor = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        emisor.SendTo(new byte[] { 0x80, 0x81, 0x7F, 200, 3, 56, 0, 0, 0x47 },
            new IPEndPoint(IPAddress.Loopback, Escucha));

        Assert.That(señal.Wait(3000), Is.True, "no llegó el datagrama");
        Assert.That(recibido, Is.Not.Null);
        Assert.That(recibido![3], Is.EqualTo(200));
        Assert.That(link.Rx, Is.EqualTo(1));
    }

    [Test]
    public void Puerto_tomado_deja_ErrorBind_sin_tirar_excepcion()
    {
        using var primero = new UdpLink(127, 255, 255, Escucha, Destino);
        using var segundo = new UdpLink(127, 255, 255, Escucha, Destino);
        Assert.That(primero.Conectado, Is.True);
        Assert.That(segundo.Conectado, Is.False);
        Assert.That(segundo.ErrorBind, Is.Not.Null.And.Not.Empty);
    }
}
