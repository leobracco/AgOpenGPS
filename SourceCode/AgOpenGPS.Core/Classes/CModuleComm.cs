using System;

namespace AgOpenGPS
{
    public class CModuleComm
    {
        // Host invertido (FormGPS implementa IModuleCommHost) — traspaso 2026-07-17
        private readonly IModuleCommHost mf;

        //acciones que la UI cablea (FormGPS las apunta a los botones nativos).
        //Antes esta clase llamaba btn*.PerformClick() directo — dependencia
        //WinForms innecesaria para la lógica de switches (traspaso
        //portabilidad 2026-07-16).
        public Action ToggleAutoSteer;
        public Action ToggleSectionMasterManual;
        public Action ToggleSectionMasterAuto;

        //Critical Safety Properties
        public bool isOutOfBounds = true;

        // ---- Section control switches to AOG  ---------------------------------------------------------
        //PGN - 32736 - 127.249 0x7FF9
        public byte[] ss = new byte[9];

        public byte[] ssP = new byte[9];

        public int
            swHeader = 0,
            swMain = 1,
            swAutoGr0 = 2,
            swAutoGr1 = 3,
            swNumSections = 4,
            swOnGr0 = 5,
            swOffGr0 = 6,
            swOnGr1 = 7,
            swOffGr1 = 8;

        public int pwmDisplay = 0;
        public double actualSteerAngleDegrees = 0;
        public int actualSteerAngleChart = 0, sensorData = -1;

        //for the workswitch
        public bool isWorkSwitchActiveLow, isRemoteWorkSystemOn, isWorkSwitchEnabled,
            isWorkSwitchManualSections, isSteerWorkSwitchManualSections, isSteerWorkSwitchEnabled;

        public bool workSwitchHigh, oldWorkSwitchHigh, steerSwitchHigh, oldSteerSwitchHigh, oldSteerSwitchRemote;

        //constructor
        public CModuleComm(IModuleCommHost _f)
        {
            mf = _f;
            //WorkSwitch logic
            isRemoteWorkSystemOn = false;

            //does a low, grounded out, mean on
            isWorkSwitchActiveLow = true;
        }

        //Called from "OpenGL.Designer.cs" when requied
        public void CheckWorkAndSteerSwitch()
        {
            //AutoSteerAuto button enable - Ray Bear inspired code - Thx Ray!
            if (mf.IsAutoSteerAuto && steerSwitchHigh != oldSteerSwitchRemote)
            {
                oldSteerSwitchRemote = steerSwitchHigh;
                //steerSwith is active low
                if (steerSwitchHigh == mf.IsBtnAutoSteerOn)
                {
                    ToggleAutoSteer?.Invoke();
                }
            }

            if (isRemoteWorkSystemOn)
            {
                if (isWorkSwitchEnabled && (oldWorkSwitchHigh != workSwitchHigh))
                {
                    oldWorkSwitchHigh = workSwitchHigh;

                    if (workSwitchHigh != isWorkSwitchActiveLow)
                    {
                        if (isWorkSwitchManualSections)
                        {
                            if (mf.ManualBtnState != btnStates.On)
                                ToggleSectionMasterManual?.Invoke();
                        }
                        else
                        {
                            if (mf.AutoBtnState != btnStates.Auto)
                                ToggleSectionMasterAuto?.Invoke();
                        }
                    }

                    else//Checks both on-screen buttons, performs click if button is not off
                    {
                        if (mf.AutoBtnState != btnStates.Off)
                            ToggleSectionMasterAuto?.Invoke();
                        if (mf.ManualBtnState != btnStates.Off)
                            ToggleSectionMasterManual?.Invoke();
                    }
                }

                if (isSteerWorkSwitchEnabled && (oldSteerSwitchHigh != steerSwitchHigh))
                {
                    oldSteerSwitchHigh = steerSwitchHigh;

                    if ((mf.IsBtnAutoSteerOn && mf.IsAutoSteerAuto)
                        || !mf.IsAutoSteerAuto && !steerSwitchHigh)
                    {
                        if (isSteerWorkSwitchManualSections)
                        {
                            if (mf.ManualBtnState != btnStates.On)
                                ToggleSectionMasterManual?.Invoke();
                        }
                        else
                        {
                            if (mf.AutoBtnState != btnStates.Auto)
                                ToggleSectionMasterAuto?.Invoke();
                        }
                    }

                    else//Checks both on-screen buttons, performs click if button is not off
                    {
                        if (mf.AutoBtnState != btnStates.Off)
                            ToggleSectionMasterAuto?.Invoke();
                        if (mf.ManualBtnState != btnStates.Off)
                            ToggleSectionMasterManual?.Invoke();
                    }
                }
            }
        }
    }
}