namespace AgOpenGPS
{
    public class CSound
    {
        //sound objects - wave files en resources, detrás de AgpSoundPlayer
        //(sin System.Media directo acá: traspaso portabilidad 2026-07-16)
        public readonly AgpSoundPlayer sndBoundaryAlarm = new AgpSoundPlayer(Properties.Resources.Alarm10);
        public readonly AgpSoundPlayer sndUTurnTooClose = new AgpSoundPlayer(Properties.Resources.TF012);

        public readonly AgpSoundPlayer sndAutoSteerOn = new AgpSoundPlayer(Properties.Resources.SteerOn);
        public readonly AgpSoundPlayer sndAutoSteerOff = new AgpSoundPlayer(Properties.Resources.SteerOff);
        public readonly AgpSoundPlayer sndHydLiftUp = new AgpSoundPlayer(Properties.Resources.HydUp);
        public readonly AgpSoundPlayer sndHydLiftDn = new AgpSoundPlayer(Properties.Resources.HydDown);
        public readonly AgpSoundPlayer sndRTKAlarm = new AgpSoundPlayer(Properties.Resources.rtk_lost);
        public readonly AgpSoundPlayer sndSectionOn = new AgpSoundPlayer(Properties.Resources.SectionOn);
        public readonly AgpSoundPlayer sndSectionOff = new AgpSoundPlayer(Properties.Resources.SectionOff);
        public readonly AgpSoundPlayer sndHeadland = new AgpSoundPlayer(Properties.Resources.Headland);
        public readonly AgpSoundPlayer sndRTKRecoverd = new AgpSoundPlayer(Properties.Resources.rtk_back);


        public bool isBoundAlarming, isRTKAlarming;
        public bool RTKWasAlarming = false;


        public bool isSteerSoundOn, isTurnSoundOn, isHydLiftSoundOn, isSectionsSoundOn;

        public bool isHydLiftChange;

        public CSound()
        {
            isSteerSoundOn = Properties.Settings.Default.setSound_isAutoSteerOn;
            isHydLiftSoundOn = Properties.Settings.Default.setSound_isHydLiftOn;
            isTurnSoundOn = Properties.Settings.Default.setSound_isUturnOn;
            isSectionsSoundOn = Properties.Settings.Default.setSound_isSectionsOn;
        }
    }
}