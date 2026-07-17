// ============================================================================
// FormGps.BoundaryHost.cs
// Implementación de IBoundaryHost sobre FormGPS. CBoundary (partials
// CBoundary/CFence/CTurn/CHead) vive en AgOpenGPS.Core y consume el host a
// través de esta interfaz (inversión de dependencia — traspaso de
// portabilidad 2026-07-17).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : IBoundaryHost
    {
        CModuleComm IBoundaryHost.Mc => mc;
        CFieldData IBoundaryHost.Fd => fd;
        CSection[] IBoundaryHost.Section => section;

        double IBoundaryHost.UturnDistanceFromBoundary => yt.uturnDistanceFromBoundary;
        int IBoundaryHost.ABLineWidth => ABLine.lineWidth;

        vec3 IBoundaryHost.PivotAxlePos => pivotAxlePos;
        vec3 IBoundaryHost.ToolPivotPos => toolPivotPos;

        double IBoundaryHost.AvgSpeed => avgSpeed;
        bool IBoundaryHost.IsReverse => isReverse;
        bool IBoundaryHost.IsHydLiftOn => vehicle.isHydLiftOn;
        bool IBoundaryHost.IsHeadlandDistanceOn => isHeadlandDistanceOn;

        int IBoundaryHost.ToolNumOfSections => tool.numOfSections;
        int IBoundaryHost.ToolRpWidth => tool.rpWidth;
        double IBoundaryHost.ToolLookAheadOnPixelsLeft => tool.lookAheadDistanceOnPixelsLeft;
        double IBoundaryHost.ToolLookAheadOnPixelsRight => tool.lookAheadDistanceOnPixelsRight;

        bool IBoundaryHost.ToolIsLeftSideInHeadland
        {
            get => tool.isLeftSideInHeadland;
            set => tool.isLeftSideInHeadland = value;
        }

        bool IBoundaryHost.ToolIsRightSideInHeadland
        {
            get => tool.isRightSideInHeadland;
            set => tool.isRightSideInHeadland = value;
        }

        void IBoundaryHost.SetHydLiftPgn(byte value) => p_239.pgn[p_239.hydLift] = value;

        bool IBoundaryHost.IsHydLiftChange
        {
            get => sounds.isHydLiftChange;
            set => sounds.isHydLiftChange = value;
        }

        bool IBoundaryHost.IsHydLiftSoundOn => sounds.isHydLiftSoundOn;
        void IBoundaryHost.PlayHydLiftUp() => sounds.sndHydLiftUp.Play();
        void IBoundaryHost.PlayHydLiftDn() => sounds.sndHydLiftDn.Play();

        bool IBoundaryHost.IsBoundAlarming
        {
            get => sounds.isBoundAlarming;
            set => sounds.isBoundAlarming = value;
        }

        void IBoundaryHost.PlayHeadlandSound() => sounds.sndHeadland.Play();
    }
}
