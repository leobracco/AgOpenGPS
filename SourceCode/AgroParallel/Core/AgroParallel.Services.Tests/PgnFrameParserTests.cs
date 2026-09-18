// PgnFrameParserTests.cs
// Tests del framing PGN serie extraido de SerialComm.Designer.cs (CoreX).
// Formato: [0x80][0x81][src 121..127][pgn][len][data...len][checksum]
// checksum = suma de bytes desde src (indice 2) hasta fin de data.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using AgroParallel.Services;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class PgnFrameParserTests
    {
        private static byte[] BuildFrame(byte src, byte pgn, byte[] data)
        {
            var frame = new List<byte> { 0x80, 0x81, src, pgn, (byte)data.Length };
            frame.AddRange(data);
            int ck = 0;
            for (int i = 2; i < frame.Count; i++) ck += frame[i];
            frame.Add((byte)ck);
            return frame.ToArray();
        }

        private static List<byte[]> Feed(PgnFrameParser parser, IEnumerable<byte> bytes)
        {
            var frames = new List<byte[]>();
            parser.OnFrame += f => frames.Add(f);
            foreach (var b in bytes) parser.ProcessByte(b);
            return frames;
        }

        [Test]
        public void TramaValida_EmiteFrameCompleto()
        {
            var input = BuildFrame(0x7F, 0xFD, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            var frames = Feed(new PgnFrameParser(), input);

            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0], Is.EqualTo(input));
        }

        [Test]
        public void ChecksumInvalido_NoEmiteFrame()
        {
            var input = BuildFrame(0x7F, 0xFD, new byte[] { 1, 2, 3 });
            input[input.Length - 1] ^= 0xFF; // romper checksum

            var frames = Feed(new PgnFrameParser(), input);

            Assert.That(frames, Is.Empty);
        }

        [Test]
        public void DosTramasSeguidas_EmiteAmbas()
        {
            var f1 = BuildFrame(0x7F, 0xFD, new byte[] { 10, 20 });
            var f2 = BuildFrame(0x7E, 0xEE, new byte[] { 1, 2, 3, 4 });

            var frames = Feed(new PgnFrameParser(), f1.Concat(f2));

            Assert.That(frames, Has.Count.EqualTo(2));
            Assert.That(frames[0], Is.EqualTo(f1));
            Assert.That(frames[1], Is.EqualTo(f2));
        }

        [Test]
        public void BasuraAntesDeLaTrama_Resincroniza()
        {
            var noise = new byte[] { 0x00, 0x55, 0xAA, 0x80, 0x00 }; // 0x80 falso sin 0x81
            var input = BuildFrame(0x7F, 0xFD, new byte[] { 9, 9 });

            var frames = Feed(new PgnFrameParser(), noise.Concat(input));

            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0], Is.EqualTo(input));
        }

        [Test]
        public void SourceFueraDeRango_Descarta()
        {
            // src 0x50 no esta en 121..127
            var input = BuildFrame(0x50, 0xFD, new byte[] { 1, 2 });

            var frames = Feed(new PgnFrameParser(), input);

            Assert.That(frames, Is.Empty);
        }

        [Test]
        public void LenOverflow_DescartaSinExcepcion()
        {
            // len=200 no entra en el buffer de 21 bytes: se descarta y el
            // parser sigue funcionando para la trama valida siguiente.
            var overflow = new byte[] { 0x80, 0x81, 0x7F, 0xFD, 200, 1, 2, 3 };
            var valid = BuildFrame(0x7F, 0xFD, new byte[] { 7, 7 });

            var parser = new PgnFrameParser();
            var frames = Feed(parser, overflow.Concat(valid));

            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0], Is.EqualTo(valid));
        }

        [Test]
        public void Reset_DescartaTramaParcial()
        {
            var input = BuildFrame(0x7F, 0xFD, new byte[] { 1, 2, 3, 4 });
            var parser = new PgnFrameParser();
            var frames = new List<byte[]>();
            parser.OnFrame += f => frames.Add(f);

            // alimentar media trama, resetear, y mandar la trama entera
            for (int i = 0; i < 6; i++) parser.ProcessByte(input[i]);
            parser.Reset();
            foreach (var b in input) parser.ProcessByte(b);

            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0], Is.EqualTo(input));
        }
    }
}
