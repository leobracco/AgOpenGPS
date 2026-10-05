namespace AgOpenGPS
{
    /// <summary>
    /// Arma y envía los PGN de configuración (steer 252/251, machine 238,
    /// relay 236, anchos de sección 235). Vivía embebido en FormGPS.cs:
    /// se movió a Core porque es armado de bytes puro desde
    /// Properties.Settings — traspaso portabilidad, bloque 9 matriz
    /// Android (2026-07-19). El host cruza por ISettingsSenderHost.
    /// </summary>
    public class CSettingsSender
    {
        private readonly ISettingsSenderHost mf;

        public CSettingsSender(ISettingsSenderHost host)
        {
            mf = host;
        }

        public void SendSettings()
        {
            var settings = AgOpenGPS.Properties.Settings.Default;
            CPGN_FC p_252 = mf.P252;

            //Form Steer Settings
            p_252.pgn[p_252.countsPerDegree] = unchecked((byte)settings.setAS_countsPerDegree);
            p_252.pgn[p_252.ackerman] = unchecked((byte)settings.setAS_ackerman);

            p_252.pgn[p_252.wasOffsetHi] = unchecked((byte)(settings.setAS_wasOffset >> 8));
            p_252.pgn[p_252.wasOffsetLo] = unchecked((byte)(settings.setAS_wasOffset));

            p_252.pgn[p_252.highPWM] = unchecked((byte)settings.setAS_highSteerPWM);
            p_252.pgn[p_252.lowPWM] = unchecked((byte)settings.setAS_lowSteerPWM);
            p_252.pgn[p_252.gainProportional] = unchecked((byte)settings.setAS_Kp);
            p_252.pgn[p_252.minPWM] = unchecked((byte)settings.setAS_minSteerPWM);

            mf.SendPgnToLoop(p_252.pgn);

            //steer config
            CPGN_FB p_251 = mf.P251;
            p_251.pgn[p_251.set0] = settings.setArdSteer_setting0;
            p_251.pgn[p_251.set1] = settings.setArdSteer_setting1;
            p_251.pgn[p_251.maxPulse] = settings.setArdSteer_maxPulseCounts;
            p_251.pgn[p_251.minSpeed] = unchecked((byte)(settings.setAS_minSteerSpeed * 10));

            if (settings.setAS_isConstantContourOn)
                p_251.pgn[p_251.angVel] = 1;
            else p_251.pgn[p_251.angVel] = 0;

            mf.SendPgnToLoop(p_251.pgn);

            //machine settings
            CPGN_EE p_238 = mf.P238;
            p_238.pgn[p_238.set0] = settings.setArdMac_setting0;
            p_238.pgn[p_238.raiseTime] = settings.setArdMac_hydRaiseTime;
            p_238.pgn[p_238.lowerTime] = settings.setArdMac_hydLowerTime;

            p_238.pgn[p_238.user1] = settings.setArdMac_user1;
            p_238.pgn[p_238.user2] = settings.setArdMac_user2;
            p_238.pgn[p_238.user3] = settings.setArdMac_user3;
            p_238.pgn[p_238.user4] = settings.setArdMac_user4;

            mf.SendPgnToLoop(p_238.pgn);
        }

        public void SendRelaySettingsToMachineModule()
        {
            string[] words = AgOpenGPS.Properties.Settings.Default.setRelay_pinConfig.Split(',');

            CPGN_EC p_236 = mf.P236;

            //load the pgn
            p_236.pgn[p_236.pin0] = (byte)int.Parse(words[0]);
            p_236.pgn[p_236.pin1] = (byte)int.Parse(words[1]);
            p_236.pgn[p_236.pin2] = (byte)int.Parse(words[2]);
            p_236.pgn[p_236.pin3] = (byte)int.Parse(words[3]);
            p_236.pgn[p_236.pin4] = (byte)int.Parse(words[4]);
            p_236.pgn[p_236.pin5] = (byte)int.Parse(words[5]);
            p_236.pgn[p_236.pin6] = (byte)int.Parse(words[6]);
            p_236.pgn[p_236.pin7] = (byte)int.Parse(words[7]);
            p_236.pgn[p_236.pin8] = (byte)int.Parse(words[8]);
            p_236.pgn[p_236.pin9] = (byte)int.Parse(words[9]);

            p_236.pgn[p_236.pin10] = (byte)int.Parse(words[10]);
            p_236.pgn[p_236.pin11] = (byte)int.Parse(words[11]);
            p_236.pgn[p_236.pin12] = (byte)int.Parse(words[12]);
            p_236.pgn[p_236.pin13] = (byte)int.Parse(words[13]);
            p_236.pgn[p_236.pin14] = (byte)int.Parse(words[14]);
            p_236.pgn[p_236.pin15] = (byte)int.Parse(words[15]);
            p_236.pgn[p_236.pin16] = (byte)int.Parse(words[16]);
            p_236.pgn[p_236.pin17] = (byte)int.Parse(words[17]);
            p_236.pgn[p_236.pin18] = (byte)int.Parse(words[18]);
            p_236.pgn[p_236.pin19] = (byte)int.Parse(words[19]);

            p_236.pgn[p_236.pin20] = (byte)int.Parse(words[20]);
            p_236.pgn[p_236.pin21] = (byte)int.Parse(words[21]);
            p_236.pgn[p_236.pin22] = (byte)int.Parse(words[22]);
            p_236.pgn[p_236.pin23] = (byte)int.Parse(words[23]);
            mf.SendPgnToLoop(p_236.pgn);

            CPGN_EB p_235 = mf.P235;
            CSection[] section = mf.Section;

            p_235.pgn[p_235.sec0Lo] = unchecked((byte)(section[0].sectionWidth * 100));
            p_235.pgn[p_235.sec0Hi] = unchecked((byte)((int)((section[0].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec1Lo] = unchecked((byte)(section[1].sectionWidth * 100));
            p_235.pgn[p_235.sec1Hi] = unchecked((byte)((int)((section[1].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec2Lo] = unchecked((byte)(section[2].sectionWidth * 100));
            p_235.pgn[p_235.sec2Hi] = unchecked((byte)((int)((section[2].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec3Lo] = unchecked((byte)(section[3].sectionWidth * 100));
            p_235.pgn[p_235.sec3Hi] = unchecked((byte)((int)((section[3].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec4Lo] = unchecked((byte)(section[4].sectionWidth * 100));
            p_235.pgn[p_235.sec4Hi] = unchecked((byte)((int)((section[4].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec5Lo] = unchecked((byte)(section[5].sectionWidth * 100));
            p_235.pgn[p_235.sec5Hi] = unchecked((byte)((int)((section[5].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec6Lo] = unchecked((byte)(section[6].sectionWidth * 100));
            p_235.pgn[p_235.sec6Hi] = unchecked((byte)((int)((section[6].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec7Lo] = unchecked((byte)(section[7].sectionWidth * 100));
            p_235.pgn[p_235.sec7Hi] = unchecked((byte)((int)((section[7].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec8Lo] = unchecked((byte)(section[8].sectionWidth * 100));
            p_235.pgn[p_235.sec8Hi] = unchecked((byte)((int)((section[8].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec9Lo] = unchecked((byte)(section[9].sectionWidth * 100));
            p_235.pgn[p_235.sec9Hi] = unchecked((byte)((int)((section[9].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec10Lo] = unchecked((byte)(section[10].sectionWidth * 100));
            p_235.pgn[p_235.sec10Hi] = unchecked((byte)((int)((section[10].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec11Lo] = unchecked((byte)(section[11].sectionWidth * 100));
            p_235.pgn[p_235.sec11Hi] = unchecked((byte)((int)((section[11].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec12Lo] = unchecked((byte)(section[12].sectionWidth * 100));
            p_235.pgn[p_235.sec12Hi] = unchecked((byte)((int)((section[12].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec13Lo] = unchecked((byte)(section[13].sectionWidth * 100));
            p_235.pgn[p_235.sec13Hi] = unchecked((byte)((int)((section[13].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec14Lo] = unchecked((byte)(section[14].sectionWidth * 100));
            p_235.pgn[p_235.sec14Hi] = unchecked((byte)((int)((section[14].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec15Lo] = unchecked((byte)(section[15].sectionWidth * 100));
            p_235.pgn[p_235.sec15Hi] = unchecked((byte)((int)((section[15].sectionWidth * 100)) >> 8));

            p_235.pgn[p_235.numSections] = (byte)mf.Tool.numOfSections;

            mf.SendPgnToLoop(p_235.pgn);
        }
    }
}
