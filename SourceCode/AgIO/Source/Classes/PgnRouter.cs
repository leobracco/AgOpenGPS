namespace AgIO
{
    /// <summary>
    /// Lógica pura de ruteo/parsing de PGNs de CoreX, extraída de
    /// UDP.designer.cs (partial de FormLoop) para portabilidad
    /// (traspaso 2026-07-17). Sin sockets ni WinForms: sólo decide, a
    /// partir de los bytes de un PGN, a qué puerto va y qué significa una
    /// respuesta de scan. El ciclo de vida de los sockets queda en FormLoop.
    /// </summary>
    public static class CPgnRouter
    {
        /// <summary>
        /// PGN recibido por loopback desde PilotX: decide a qué módulos serie
        /// se reenvía (steer / machine). Espeja el switch histórico de
        /// ReceiveFromLoopBack (data[3] = número de PGN).
        /// </summary>
        public static void RouteLoopbackPgn(byte pgn, out bool toSteer, out bool toMachine)
        {
            toSteer = false;
            toMachine = false;

            switch (pgn)
            {
                case 0xFE: // 254 AutoSteer Data
                    toSteer = true;
                    toMachine = true;
                    break;
                case 0xEF: // 239 machine pgn
                    toMachine = true;
                    toSteer = true;
                    break;
                case 0xE5: // 229 Symmetric Sections - Zones
                    toMachine = true;
                    break;
                case 0xFC: // 252 steer settings
                    toSteer = true;
                    break;
                case 0xFB: // 251 steer config
                    toSteer = true;
                    break;
                case 0xEE: // 238 machine config
                    toMachine = true;
                    toSteer = true;
                    break;
                case 0xEC: // 236 machine config
                    toMachine = true;
                    toSteer = true;
                    break;
            }
        }

        /// <summary>
        /// Respuesta de scan (PGN 203, 13 bytes) de un módulo por UDP: llena
        /// la IP y subnet correspondiente en <paramref name="scanReply"/>
        /// según el tipo de módulo (data[2]: 126 steer / 123 machine /
        /// 121 IMU / 120 GPS) y marca los flags isNew*. Espeja el bloque
        /// histórico de ReceiveFromUDP.
        /// </summary>
        public static void ParseScanReply(byte[] data, CScanReply scanReply)
        {
            string ip = data[5].ToString() + "." + data[6].ToString() + "." +
                        data[7].ToString() + "." + data[8].ToString();
            string subnetStr = data[9].ToString() + "." + data[10].ToString() + "." +
                               data[11].ToString();

            switch (data[2])
            {
                case 126: // steer module
                    scanReply.steerIP = ip;
                    scanReply.isNewSteer = true;
                    break;
                case 123: // machine module
                    scanReply.machineIP = ip;
                    scanReply.isNewMachine = true;
                    break;
                case 121: // IMU module
                    scanReply.IMU_IP = ip;
                    scanReply.isNewIMU = true;
                    break;
                case 120: // GPS module
                    scanReply.GPS_IP = ip;
                    scanReply.isNewGPS = true;
                    break;
                default:
                    return; // tipo desconocido: no toca subnet ni isNewData
            }

            scanReply.subnet[0] = data[9];
            scanReply.subnet[1] = data[10];
            scanReply.subnet[2] = data[11];
            scanReply.subnetStr = subnetStr;
            scanReply.isNewData = true;
        }
    }
}
