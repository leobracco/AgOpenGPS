// ============================================================================
// NmeaParserRs232Tests — la entrada NMEA por RS232, testeada como la entrega
// un puerto serie de verdad: en CHUNKS arbitrarios (el DataReceived corta
// donde quiere, no en fin de sentencia), con basura entre sentencias, CRLF
// partido entre lecturas y checksums rotos. Es el mismo CNmeaParser que usa
// CoreXEngineHost tanto para SpGPS (serie) como para el bridge LAN.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using AgIO;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class NmeaParserRs232Tests
    {
        private sealed class HostFake : INmeaParserHost
        {
            public readonly List<byte[]> Pgns = new List<byte[]>();
            public bool IsGpsSentencesOn => true;
            public bool IsLogMonitorOn => false;
            public void AppendLogMonitor(string text) { }
            public void SendNmeaPgn(byte[] pgn) { Pgns.Add((byte[])pgn.Clone()); }
        }

        private static string ConChecksum(string cuerpo)
        {
            int c = 0;
            foreach (char ch in cuerpo) c ^= ch;
            return "$" + cuerpo + "*" + c.ToString("X2") + "\r\n";
        }

        // Mismo fix que el simulador de siembra: 53.436N 111.16W, RTK, 3,5 km/h.
        private static string Gga() => ConChecksum(
            "GPGGA,120000.00,5326.163384,N,11109.602820,W,4,12,0.7,700.0,M,0.0,M,,");

        private static string Vtg() => ConChecksum("GPVTG,0.0,T,,M,1.89,N,3.50,K,A");

        private static void Alimentar(CNmeaParser parser, string stream, int chunk)
        {
            var bytes = Encoding.ASCII.GetBytes(stream);
            for (int i = 0; i < bytes.Length; i += chunk)
            {
                int n = Math.Min(chunk, bytes.Length - i);
                parser.ParseIncoming(Encoding.ASCII.GetString(bytes, i, n));
            }
        }

        [Theory]
        [InlineData(1)]   // byte a byte: el peor caso serie (4800 baudios)
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(64)]  // chunk típico de un DataReceived a 115200
        public void Fragmentado_ParseaPosicionVelocidadYFix(int chunk)
        {
            var host = new HostFake();
            var p = new CNmeaParser(host);

            string stream = "";
            for (int i = 0; i < 5; i++) stream += Gga() + Vtg();
            Alimentar(p, stream, chunk);

            Assert.InRange(p.latitude, 53.43, 53.44);
            Assert.InRange(p.longitude, -111.17, -111.15);
            Assert.Equal(4, p.fixQualityData);            // RTK fix
            Assert.InRange(p.speedData, 3.45, 3.55);      // km/h del VTG (speedData
            // retiene; "speed" se consume a MaxValue al armar cada PGN 0xD6)
            Assert.True(host.Pgns.Count >= 4, "PGN 0xD6 emitidos: " + host.Pgns.Count);
            Assert.All(host.Pgns, b => Assert.Equal(0xD6, b[3]));
        }

        [Fact]
        public void CrLfPartidoEntreChunks_NoPierdeSentencias()
        {
            var host = new HostFake();
            var p = new CNmeaParser(host);
            string s = Gga() + Vtg();
            // Cortar EXACTAMENTE entre \r y \n de la primera sentencia.
            int cr = s.IndexOf('\r');
            p.ParseIncoming(s.Substring(0, cr + 1));
            p.ParseIncoming(s.Substring(cr + 1));

            Assert.InRange(p.latitude, 53.43, 53.44);
            Assert.InRange(p.speedData, 3.45, 3.55);
        }

        [Fact]
        public void BasuraEntreSentencias_SigueParseandoLasValidas()
        {
            var host = new HostFake();
            var p = new CNmeaParser(host);
            // Ruido de línea típico al enchufar el DB9 con el equipo prendido.
            string stream = "\xFF\xFE??basura sin dolar\r\n" + Gga() +
                            "$GPGGA,corrupta,sin,checksum\r\n" + Vtg() + Gga();
            Alimentar(p, stream, 11);

            Assert.InRange(p.latitude, 53.43, 53.44);
            Assert.InRange(p.speedData, 3.45, 3.55);
        }

        [Fact]
        public void ChecksumInvalido_SeDescartaLaSentencia()
        {
            var host = new HostFake();
            var p = new CNmeaParser(host);
            // GGA con checksum pisado a 00 — jamás debe aceptar la posición.
            string cuerpo = "GPGGA,120000.00,5326.163384,N,11109.602820,W,4,12,0.7,700.0,M,0.0,M,,";
            p.ParseIncoming("$" + cuerpo + "*00\r\n");

            Assert.Equal(0, p.latitude);        // sin fix aceptado
            Assert.Null(p.ggaSentence);         // ni capturada como "última GGA"
            Assert.Empty(host.Pgns);
        }

        [Fact]
        public void StreamLargo_NoAcumulaBufferSinLimite()
        {
            var host = new HostFake();
            var p = new CNmeaParser(host);
            // 30 s de GPS a 10 Hz en chunks impares: si rawBuffer no se drena,
            // esto lo delataría (el parser lo recorta a ~300 chars por diseño).
            string stream = "";
            for (int i = 0; i < 300; i++) stream += Gga() + Vtg();
            Alimentar(p, stream, 17);

            Assert.InRange(p.latitude, 53.43, 53.44);
            Assert.True(host.Pgns.Count >= 250, "PGNs: " + host.Pgns.Count);
        }
    }
}
