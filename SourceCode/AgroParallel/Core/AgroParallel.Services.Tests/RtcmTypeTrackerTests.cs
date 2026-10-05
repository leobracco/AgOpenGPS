// ============================================================================
// RtcmTypeTrackerTests — el contador de tipos RTCM del monitor NTRIP.
// Lo delicado es el framing sobre chunks TCP arbitrarios: frames cortados en
// cualquier byte, basura intercalada (caster contestando texto) y resync.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgroParallel.Services;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class RtcmTypeTrackerTests
    {
        // Frame RTCM3 sintético: 0xD3, largo de payload, payload con el tipo
        // en los primeros 12 bits, CRC de relleno (el tracker no lo verifica).
        private static byte[] Frame(int tipo, int largoPayload = 8)
        {
            if (largoPayload < 2) throw new ArgumentException("payload minimo 2");
            var f = new byte[3 + largoPayload + 3];
            f[0] = 0xD3;
            f[1] = (byte)((largoPayload >> 8) & 0x03);
            f[2] = (byte)(largoPayload & 0xFF);
            f[3] = (byte)(tipo >> 4);
            f[4] = (byte)((tipo & 0x0F) << 4);
            return f;
        }

        [Fact]
        public void CuentaFramesEnteros()
        {
            var t = new RtcmTypeTracker();
            t.Feed(Frame(1074));
            t.Feed(Frame(1074));
            t.Feed(Frame(1005));

            var snap = t.Snapshot();
            Assert.Equal(2, snap.Count);
            Assert.Equal("1074×2", snap[0]);   // más frecuente primero
            Assert.Equal("1005×1", snap[1]);
        }

        [Fact]
        public void FrameCortadoEnChunksArbitrarios_CuentaUnaVez()
        {
            var t = new RtcmTypeTracker();
            var f = Frame(1230, largoPayload: 20);
            // de a un byte: el peor caso de fragmentación TCP
            foreach (var b in f) t.Feed(new[] { b });

            Assert.Equal(new List<string> { "1230×1" }, t.Snapshot());
        }

        [Fact]
        public void BasuraEntreFrames_Resincroniza()
        {
            var t = new RtcmTypeTracker();
            var basura = System.Text.Encoding.ASCII.GetBytes("ICY 200 OK\r\n");
            t.Feed(basura);
            t.Feed(Frame(1084));
            t.Feed(basura);
            t.Feed(Frame(1084));

            Assert.Equal(new List<string> { "1084×2" }, t.Snapshot());
        }

        [Fact]
        public void Reset_ArrancaDeCero()
        {
            var t = new RtcmTypeTracker();
            t.Feed(Frame(1004));
            t.Reset();
            Assert.Empty(t.Snapshot());

            // y el buffer interno también se limpia: medio frame previo al
            // Reset no puede contaminar el conteo nuevo
            var f = Frame(1094);
            t.Feed(f.Take(4).ToArray());
            t.Reset();
            t.Feed(Frame(1094));
            Assert.Equal(new List<string> { "1094×1" }, t.Snapshot());
        }
    }
}
