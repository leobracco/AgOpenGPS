using System;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        //Las clases CPGN_* se movieron a AgOpenGPS.Core/Protocols/PgnDefinitions.cs
        //(datos puros, traspaso portabilidad). Aca quedan solo las instancias.

        //pgn instances

        /// <summary>
        /// autoSteerData - FE - 254 - 
        /// </summary>
        public CPGN_FE p_254 = new CPGN_FE();

        /// <summary>
        /// autoSteerSettings PGN - 252 - FC
        /// </summary>
        public CPGN_FC p_252 = new CPGN_FC();

        /// <summary>
        /// autoSteerConfig PGN - 251 - FB
        /// </summary>
        public CPGN_FB p_251 = new CPGN_FB();

        /// <summary>
        /// machineData PGN - 239 - EF
        /// </summary>
        public CPGN_EF p_239 = new CPGN_EF();

        /// <summary>
        /// machineConfig PGN - 238 - EE
        /// </summary>
        public CPGN_EE p_238 = new CPGN_EE();

        /// <summary>
        /// relayConfig PGN - 236 - EC
        /// </summary>
        public CPGN_EC p_236 = new CPGN_EC();

        /// <summary>
        /// Section dimensions PGN - 235 - EB
        /// </summary>
        public CPGN_EB p_235 = new CPGN_EB();

        /// <summary>
        /// Section dimensions PGN - 228 - E4
        /// </summary>
        public CPGN_E4 p_228 = new CPGN_E4();

        /// <summary>
        /// Section Symmetric PGN - 229 - EB
        /// </summary>
        public CPGN_E5 p_229 = new CPGN_E5();

        /// <summary>
        /// LatitudeLongitude - D0 - 
        /// </summary>
        //public CPGN_D0 p_208 = new CPGN_D0();

    }
}
