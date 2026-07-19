using AgOpenGPS.Core.Models;
using System;
using System.Drawing;

namespace AgOpenGPS
{
    public class CTool
    {
        // Host invertido (FormGPS implementa IToolHost) — traspaso 2026-07-17.
        // internal (era private): lo lee ToolDrawExtensions (mismo assembly).
        internal readonly IToolHost mf;

        public double width, halfWidth, contourWidth;
        public double farLeftPosition = 0;
        public double farLeftSpeed = 0;
        public double farRightPosition = 0;
        public double farRightSpeed = 0;

        public double overlap;
        public double trailingHitchLength, tankTrailingHitchLength, trailingToolToPivotLength;
        public double offset;

        public double lookAheadOffSetting, lookAheadOnSetting;
        public double turnOffDelay;

        public double lookAheadDistanceOnPixelsLeft, lookAheadDistanceOnPixelsRight;
        public double lookAheadDistanceOffPixelsLeft, lookAheadDistanceOffPixelsRight;

        public bool isToolTrailing, isToolTBT;
        public bool isToolRearFixed, isToolFrontFixed;

        public bool isMultiColoredSections, isSectionOffWhenOut;

        public double hitchLength;

        //how many individual sections
        public int numOfSections;

        //used for super section off on
        public int minCoverage;

        public bool isLeftSideInHeadland = true, isRightSideInHeadland = true, isSectionsNotZones;

        //read pixel values
        public int rpXPosition;

        public int rpWidth;

        //textRotate eliminado: se escribía en DrawTool y no se leía nunca

        public Color[] secColors = new Color[16];

        public int zones;
        public int[] zoneRanges = new int[9];

        public bool isDisplayTramControl;

        //Constructor called by FormGPS
        public CTool(IToolHost _f)
        {
            mf = _f;

            //from settings grab the vehicle specifics

            trailingToolToPivotLength = Properties.Settings.Default.setTool_trailingToolToPivotLength;
            width = Properties.Settings.Default.setVehicle_toolWidth;
            overlap = Properties.Settings.Default.setVehicle_toolOverlap;

            offset = Properties.Settings.Default.setVehicle_toolOffset;

            trailingHitchLength = Properties.Settings.Default.setTool_toolTrailingHitchLength;
            tankTrailingHitchLength = Properties.Settings.Default.setVehicle_tankTrailingHitchLength;
            hitchLength = Properties.Settings.Default.setVehicle_hitchLength;

            isToolRearFixed = Properties.Settings.Default.setTool_isToolRearFixed;
            isToolTrailing = Properties.Settings.Default.setTool_isToolTrailing;
            isToolTBT = Properties.Settings.Default.setTool_isToolTBT;
            isToolFrontFixed = Properties.Settings.Default.setTool_isToolFront;

            lookAheadOnSetting = Properties.Settings.Default.setVehicle_toolLookAheadOn;
            lookAheadOffSetting = Properties.Settings.Default.setVehicle_toolLookAheadOff;
            turnOffDelay = Properties.Settings.Default.setVehicle_toolOffDelay;

            isSectionOffWhenOut = Properties.Settings.Default.setTool_isSectionOffWhenOut;

            isSectionsNotZones = Properties.Settings.Default.setTool_isSectionsNotZones;

            if (isSectionsNotZones)
                numOfSections = Properties.Settings.Default.setVehicle_numSections;
            else
                numOfSections = Properties.Settings.Default.setTool_numSectionsMulti;

            minCoverage = Properties.Settings.Default.setVehicle_minCoverage;
            isMultiColoredSections = Properties.Settings.Default.setColor_isMultiColorSections;

            secColors[0] = Properties.Settings.Default.setColor_sec01.CheckColorFor255();
            secColors[1] = Properties.Settings.Default.setColor_sec02.CheckColorFor255();
            secColors[2] = Properties.Settings.Default.setColor_sec03.CheckColorFor255();
            secColors[3] = Properties.Settings.Default.setColor_sec04.CheckColorFor255();
            secColors[4] = Properties.Settings.Default.setColor_sec05.CheckColorFor255();
            secColors[5] = Properties.Settings.Default.setColor_sec06.CheckColorFor255();
            secColors[6] = Properties.Settings.Default.setColor_sec07.CheckColorFor255();
            secColors[7] = Properties.Settings.Default.setColor_sec08.CheckColorFor255();
            secColors[8] = Properties.Settings.Default.setColor_sec09.CheckColorFor255();
            secColors[9] = Properties.Settings.Default.setColor_sec10.CheckColorFor255();
            secColors[10] = Properties.Settings.Default.setColor_sec11.CheckColorFor255();
            secColors[11] = Properties.Settings.Default.setColor_sec12.CheckColorFor255();
            secColors[12] = Properties.Settings.Default.setColor_sec13.CheckColorFor255();
            secColors[13] = Properties.Settings.Default.setColor_sec14.CheckColorFor255();
            secColors[14] = Properties.Settings.Default.setColor_sec15.CheckColorFor255();
            secColors[15] = Properties.Settings.Default.setColor_sec16.CheckColorFor255();

            string[] words = Properties.Settings.Default.setTool_zones.Split(',');
            zones = int.Parse(words[0]);

            for (int i = 0; i < words.Length; i++)
            {
                zoneRanges[i] = int.Parse(words[i]);
            }

            isDisplayTramControl = Properties.Settings.Default.setTool_isDisplayTramControl;
        }

        public double GetHitchLengthFromVehiclePivot()
        {
            double pivotToHitch = hitchLength;

            if (mf.VehicleConfig.Type == VehicleType.Articulated && !glm.IsZero(pivotToHitch))
            {
                double halfWheelbase = 0.5 * mf.VehicleConfig.Wheelbase;

                if (!glm.IsZero(halfWheelbase))
                {
                    pivotToHitch += Math.Sign(pivotToHitch) * halfWheelbase;
                }
            }

            return pivotToHitch;
        }

        public double GetHitchHeadingFromVehiclePivot(double pivotToHitchLength)
        {
            double hitchHeading = mf.FixHeading;

            if (mf.VehicleConfig.Type == VehicleType.Articulated && !glm.IsZero(pivotToHitchLength))
            {
                double steerAngleDegrees = mf.IsSimEnabled ? mf.Sim.steerAngle : mf.Mc.actualSteerAngleDegrees;
                double articulationRadians = glm.toRadians(steerAngleDegrees);

                // The hitch translation already starts from the averaged vehicle heading
                // (fixHeading). Applying the full rear-frame deflection on top of that
                // over-rotates the hitch, so scale the articulation once more to keep the
                // lateral movement aligned with the rear frame.
                double rearHeadingOffset = 0.25 * articulationRadians;

                if (pivotToHitchLength > 0)
                {
                    hitchHeading += rearHeadingOffset;
                }
                else
                {
                    hitchHeading -= rearHeadingOffset;
                }

                hitchHeading = NormalizeAngle(hitchHeading);
            }

            return hitchHeading;
        }

        private static double NormalizeAngle(double angle)
        {
            if (angle < 0)
            {
                angle = (angle % glm.twoPI) + glm.twoPI;
            }
            else if (angle >= glm.twoPI)
            {
                angle %= glm.twoPI;
            }

            return angle;
        }

        //DrawTool(), DrawHitch() y DrawTrailingHitch() se movieron a
        //ToolDrawExtensions (DrawLib/GuidanceDrawExtensions.cs): eran el único
        //uso de OpenTK/GLW acá (traspaso portabilidad).
    }
}