// ============================================================================
// GuidanceEngineHost.Models.cs — el resto de las interfaces "transversales"
// que consumen las clases de modelo (CNMEA, CSim, CVehicle, CBoundary,
// CTool, CModuleComm, CContour, CRecordedPath, CFieldData, CISOBUS, CTram,
// CPatches). Mismo patrón que los FormGps.*Host.cs equivalentes.
// ============================================================================

using System.Collections.Generic;
using AgOpenGPS.Core;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost :
        INmeaHost, ISimHost, IVehicleHost, IBoundaryHost, IToolHost,
        IModuleCommHost, IContourHost, IRecordedPathHost, IFieldDataHost,
        IIsobusHost, ITramHost, IPatchesHost
    {
        private uint sentenceCounter;
        public bool isHeadlandDistanceOn;
        public bool isHydLiftChange;
        public bool isPureDisplayOn;
        public List<List<vec3>> contourSaveList = new List<List<vec3>>();
        public List<List<vec3>> patchSaveList = new List<List<vec3>>();
        public string displayFieldName = "";

        // ---- INmeaHost ----
        ApplicationModel INmeaHost.AppModel => AppModelField;
        double INmeaHost.AvgSpeed { get => avgSpeed; set => avgSpeed = value; }
        bool INmeaHost.IsSimTimerEnabled => isSimTimerEnabled;
        CSim INmeaHost.Sim => Sim;
        WorldGrid INmeaHost.WorldGrid => WorldGridField;

        // ---- ISimHost ----
        CModuleComm ISimHost.Mc => Mc;
        CAHRS ISimHost.Ahrs => Ahrs;
        ApplicationModel ISimHost.AppModel => AppModelField;
        double ISimHost.VtgSpeed { set => Pn.vtgSpeed = value; }
        void ISimHost.AverageTheSpeed() => Pn.AverageTheSpeed();
        double ISimHost.FixNorthing { set { var f = Pn.fix; f.northing = value; Pn.fix = f; } }
        double ISimHost.FixEasting { set { var f = Pn.fix; f.easting = value; Pn.fix = f; } }
        double ISimHost.HeadingTrueDegrees { set => Pn.headingTrue = Pn.headingTrueDual = value; }
        double ISimHost.Hdop { set => Pn.hdop = value; }
        double ISimHost.Altitude { set => Pn.altitude = value; }
        int ISimHost.SatellitesTracked { set => Pn.satellitesTracked = value; }
        int ISimHost.SentenceCounter { set => sentenceCounter = (uint)value; }
        // Serializado: el timer del simulador es OTRO hilo que el UDP loopback;
        // sin el lock compartido los dos entran juntos al pipeline de fix (ver
        // _fixPipelineLock en ReceiveAppData).
        void ISimHost.UpdateFixPosition() => ProcesarFixSerializado(UpdateFixPosition);

        // ---- IVehicleHost ----
        CModuleComm IVehicleHost.Mc => Mc;
        CSim IVehicleHost.Sim => Sim;
        CTool IVehicleHost.Tool => Tool;
        CBoundary IVehicleHost.Bnd => Bnd;
        double IVehicleHost.AvgSpeed => avgSpeed;
        double IVehicleHost.FixHeading => fixHeading;
        bool IVehicleHost.IsFirstHeadingSet => isFirstHeadingSet;
        string IVehicleHost.HeadingFromSource => headingFromSource;
        bool IVehicleHost.IsSimEnabled => isSimTimerEnabled;
        double IVehicleHost.CamSetDistance => camSetDistance;
        bool IVehicleHost.IsSvennArrowOn => isSvennArrowOn;
        int IVehicleHost.ABLineWidth => ABLineField.lineWidth;

        // ---- IBoundaryHost ----
        CModuleComm IBoundaryHost.Mc => Mc;
        CFieldData IBoundaryHost.Fd => Fd;
        CSection[] IBoundaryHost.Section => Sections;
        double IBoundaryHost.UturnDistanceFromBoundary => Yt.uturnDistanceFromBoundary;
        int IBoundaryHost.ABLineWidth => ABLineField.lineWidth;
        vec3 IBoundaryHost.PivotAxlePos => pivotAxlePos;
        vec3 IBoundaryHost.ToolPivotPos => toolPivotPos;
        double IBoundaryHost.AvgSpeed => avgSpeed;
        bool IBoundaryHost.IsReverse => isReverse;
        bool IBoundaryHost.IsHydLiftOn => Vehicle.isHydLiftOn;
        bool IBoundaryHost.IsHeadlandDistanceOn => isHeadlandDistanceOn;
        int IBoundaryHost.ToolNumOfSections => Tool.numOfSections;
        int IBoundaryHost.ToolRpWidth => Tool.rpWidth;
        double IBoundaryHost.ToolLookAheadOnPixelsLeft => Tool.lookAheadDistanceOnPixelsLeft;
        double IBoundaryHost.ToolLookAheadOnPixelsRight => Tool.lookAheadDistanceOnPixelsRight;
        bool IBoundaryHost.ToolIsLeftSideInHeadland { get => Tool.isLeftSideInHeadland; set => Tool.isLeftSideInHeadland = value; }
        bool IBoundaryHost.ToolIsRightSideInHeadland { get => Tool.isRightSideInHeadland; set => Tool.isRightSideInHeadland = value; }
        void IBoundaryHost.SetHydLiftPgn(byte value) => P239Field.pgn[P239Field.hydLift] = value;
        bool IBoundaryHost.IsHydLiftChange { get => isHydLiftChange; set => isHydLiftChange = value; }
        bool IBoundaryHost.IsHydLiftSoundOn => false;
        void IBoundaryHost.PlayHydLiftUp() { }
        void IBoundaryHost.PlayHydLiftDn() { }
        bool IBoundaryHost.IsBoundAlarming { get => isBoundAlarming; set => isBoundAlarming = value; }
        void IBoundaryHost.PlayHeadlandSound() { }

        // ---- IToolHost ----
        CModuleComm IToolHost.Mc => Mc;
        CSim IToolHost.Sim => Sim;
        CSection[] IToolHost.Section => Sections;
        CTram IToolHost.Tram => Tram;
        VehicleConfig IToolHost.VehicleConfig => Vehicle.VehicleConfig;
        double IToolHost.FixHeading => fixHeading;
        bool IToolHost.IsSimEnabled => isSimTimerEnabled;
        vec3 IToolHost.PivotAxlePos => pivotAxlePos;
        vec3 IToolHost.ToolPivotPos => toolPivotPos;
        vec3 IToolHost.TankPos => tankPos;
        double IToolHost.CamSetDistance => camSetDistance;
        bool IToolHost.IsJobStarted => IsJobStarted;
        bool IToolHost.IsHydLiftOn => Vehicle.isHydLiftOn;
        double IToolHost.HydLiftLookAheadDistanceLeft => Vehicle.hydLiftLookAheadDistanceLeft;
        double IToolHost.HydLiftLookAheadDistanceRight => Vehicle.hydLiftLookAheadDistanceRight;

        // ---- IModuleCommHost ----
        bool IModuleCommHost.IsAutoSteerAuto => Ahrs.isAutoSteerAuto;
        bool IModuleCommHost.IsBtnAutoSteerOn => isBtnAutoSteerOn;
        btnStates IModuleCommHost.AutoBtnState => autoBtnState;
        btnStates IModuleCommHost.ManualBtnState => manualBtnState;

        // ---- IContourHost ----
        CABLine IContourHost.ABLine => ABLineField;
        void IContourHost.SetContourLockImage(bool isOn) { }
        vec2 IContourHost.PnFix => Pn.fix;
        bool IContourHost.IsPureDisplayOn => isPureDisplayOn;
        List<List<vec3>> IContourHost.ContourSaveList => contourSaveList;

        // ---- IRecordedPathHost ----
        CYouTurn IRecordedPathHost.YouTurn => Yt;
        double IRecordedPathHost.SimStepDistance { set => Sim.stepDistance = value; }
        void IRecordedPathHost.ClickSectionMasterAuto() { }
        void IRecordedPathHost.OnRecordedPathStopped() { }

        // ---- IFieldDataHost ----
        double IFieldDataHost.AvgSpeed => avgSpeed;
        string IFieldDataHost.DisplayFieldName => displayFieldName;
        double IFieldDataHost.ToolWidth => Tool.width;
        int IFieldDataHost.ToolNumOfSections => Tool.numOfSections;
        double IFieldDataHost.ToolOverlap => Tool.overlap;
        IReadOnlyList<CBoundaryList> IFieldDataHost.BoundaryList => Bnd.bndList;

        // ---- IIsobusHost ---- (SendPgnToLoop ya está implementado en el archivo principal)
        bool IIsobusHost.IsobusSectionControlImageOn { set { } }
        bool IIsobusHost.IsobusButtonVisible { set { } }

        // ---- ITramHost ----
        double ITramHost.ToolWidth => Tool.width;
        double ITramHost.CamSetDistance => camSetDistance;
        IReadOnlyList<CBoundaryList> ITramHost.BoundaryList => Bnd.bndList;

        // ---- IPatchesHost ----
        CFieldData IPatchesHost.Fd => Fd;
        CSection[] IPatchesHost.Section => Sections;
        bool IPatchesHost.ToolIsMultiColoredSections => Tool.isMultiColoredSections;
        bool IPatchesHost.ToolIsSectionsNotZones => Tool.isSectionsNotZones;
        vec3 IPatchesHost.SectionColorDayVec => new vec3(0, 200, 0);
        vec3 IPatchesHost.SecColorVec(int j) => new vec3(0, 200, 0);
        void IPatchesHost.IncrementPatchCounter() => patchCounter++;
        List<List<vec3>> IPatchesHost.PatchSaveList => patchSaveList;

        /// <summary>
        /// Grilla de cobertura neta (suelo pintado sin repintado). Alimenta
        /// Fd.actualAreaCovered, que el AOG original sacaba de OpenGL y aca
        /// nunca se calculaba (Neta 0 / Repintado 100 % en pantalla).
        /// La llenan CPatches.AddMappingPoint (en vivo) y CargarCobertura
        /// (al reabrir el lote); se vacia al cerrar el lote.
        /// </summary>
        public readonly CoberturaNeta Neta = new CoberturaNeta(0.5);

        void IPatchesHost.QuadPintado(vec3 izqAnt, vec3 derAnt, vec3 izq, vec3 der)
        {
            Neta.MarcarQuad(izqAnt, derAnt, izq, der);
            Fd.actualAreaCovered = Neta.AreaM2;
        }
    }
}
