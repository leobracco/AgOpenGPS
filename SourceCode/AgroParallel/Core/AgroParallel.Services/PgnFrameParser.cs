// ============================================================================
// PgnFrameParser.cs — Framing PGN portable (netstandard2.0).
// Máquina de estados byte a byte para armar tramas PGN 0x80 0x81 que llegan
// por puerto serie (steer/machine/IMU). Extraída de SerialComm.Designer.cs
// de CoreX, donde estaba triplicada. Formato de trama:
//   [0x80][0x81][src 121..127][pgn][len][data...len][checksum]
// checksum = suma de bytes desde src (índice 2) hasta el final de data.
// ============================================================================

using System;

namespace AgroParallel.Services
{
    public sealed class PgnFrameParser
    {
        private const int HeaderBytes = 5; // 0x80,0x81,src,pgn,len

        private readonly byte[] _buf = new byte[21];
        private int _idx;

        // Trama completa validada (incluye checksum al final).
        public event Action<byte[]> OnFrame;

        public void Reset() => _idx = 0;

        public void ProcessByte(byte a)
        {
            switch (_idx)
            {
                case 0: //find 0x80
                    if (a == 128) _buf[_idx++] = a;
                    else _idx = 0;
                    break;

                case 1: //find 0x81 (o resync en 0xB5)
                    if (a == 129) _buf[_idx++] = a;
                    else if (a == 181)
                    {
                        _idx = 0;
                        _buf[_idx++] = a;
                    }
                    else _idx = 0;
                    break;

                case 2: //Source Address (7F)
                    if (a < 128 && a > 120) _buf[_idx++] = a;
                    else _idx = 0;
                    break;

                case 3: //PGN ID
                case 4: //Num of data bytes
                    _buf[_idx++] = a;
                    break;

                default: //Data load and Checksum
                    if (_idx > 4)
                    {
                        int length = _buf[4] + HeaderBytes;
                        if (length >= _buf.Length)
                        {
                            // len declara más de lo que entra en el buffer: descartar.
                            _idx = 0;
                            break;
                        }

                        if (_idx < length)
                        {
                            _buf[_idx++] = a;
                            break;
                        }

                        //crc: suma desde src hasta fin de data
                        int ckA = 0;
                        for (int j = 2; j < length; j++) ckA += _buf[j];

                        if (a == (byte)ckA)
                        {
                            _buf[_idx] = (byte)ckA;
                            var frame = new byte[length + 1];
                            Array.Copy(_buf, frame, length + 1);
                            OnFrame?.Invoke(frame);
                        }

                        _idx = 0;
                    }
                    break;
            }
        }
    }
}
