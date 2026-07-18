using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class CBoundary
    {
        // Host invertido (FormGPS implementa IBoundaryHost) — traspaso 2026-07-17
        private readonly IBoundaryHost mf;



        public List<CBoundaryList> bndList = new List<CBoundaryList>();

        //constructor
        public CBoundary(IBoundaryHost _f)
        {
            mf = _f;
            turnSelected = 0;
            isHeadlandOn = false;
            isSectionControlledByHeadland = Properties.Settings.Default.setHeadland_isSectionControlled;
        }
    }
}
