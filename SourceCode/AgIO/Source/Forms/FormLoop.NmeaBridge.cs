// ============================================================================
// FormLoop.NmeaBridge.cs
// Puente entre FormLoop y CNmeaParser (ex NMEA.Designer.cs). El parsing NMEA
// vive ahora en Classes/NmeaParser.cs sin dependencias de UI; acá queda:
//   1) la implementación de INmeaParserHost (flags, monitor, envío del PGN)
//   2) forwarders con los nombres históricos para que los consumidores
//      existentes (FormGPSData, NTRIPComm, CoreXSnapshot, FormNtrip, FormRadio,
//      FormLoop.cs) sigan compilando sin cambios.
// Traspaso de portabilidad 2026-07-17.
// ============================================================================

using System;

namespace AgIO
{
    public partial class FormLoop : INmeaParserHost
    {
        public CNmeaParser nmea;

        bool INmeaParserHost.IsGpsSentencesOn => isGPSSentencesOn;

        bool INmeaParserHost.IsLogMonitorOn => isLogMonitorOn;

        void INmeaParserHost.AppendLogMonitor(string text) => logMonitorSentence.Append(text);

        void INmeaParserHost.SendNmeaPgn(byte[] pgn)
        {
            //Send nmea to AgOpenGPS
            SendToLoopBackMessageAOG(pgn);

            //Send nmea to autosteer module 8888
            if (isSendNMEAToUDP) SendUDPMessage(pgn, epModule);
        }

        // ---- Forwarders de solo lectura (nombres históricos) ----

        public string ggaSentence => nmea.ggaSentence;
        public string vtgSentence => nmea.vtgSentence;
        public string hdtSentence => nmea.hdtSentence;
        public string avrSentence => nmea.avrSentence;
        public string paogiSentence => nmea.paogiSentence;
        public string hpdSentence => nmea.hpdSentence;
        public string pandaSentence => nmea.pandaSentence;
        public string ksxtSentence => nmea.ksxtSentence;

        public double latitude => nmea.latitude;
        public double longitude => nmea.longitude;

        public byte fixQualityData => nmea.fixQualityData;
        public ushort satellitesData => nmea.satellitesData;
        public float hdopData => nmea.hdopData;
        public float altitudeData => nmea.altitudeData;
        public float speedData => nmea.speedData;
        public float rollData => nmea.rollData;
        public float headingTrueData => nmea.headingTrueData;
        public float headingTrueDualData => nmea.headingTrueDualData;
        public float ageData => nmea.ageData;

        public ushort imuHeadingData => nmea.imuHeadingData;
        public short imuRollData => nmea.imuRollData;
        public short imuPitchData => nmea.imuPitchData;
        public short imuYawRateData => nmea.imuYawRateData;

        public string FixQuality => nmea.FixQuality;
    }
}
