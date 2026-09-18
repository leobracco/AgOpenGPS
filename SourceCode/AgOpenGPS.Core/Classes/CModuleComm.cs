using System;
using System.Diagnostics;

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

        // ---- ToolX: switch de herramienta inalámbrico (ESP32) ----------------------------------
        // ToolX manda el PGN 253 con este byte de ORIGEN (data[2]) y sólo le importa el bit de
        // trabajo (byte 11, bit 0). El módulo de dirección real usa 0x7E y manda el mismo PGN a
        // 10 Hz: si los dos tocaran workSwitchHigh, el bit alternaría en cada paquete y
        // CheckWorkAndSteerSwitch (que dispara por flanco) prendería y apagaría las secciones sin
        // parar. Reglas (PgnReceiver, caso 253):
        //   · isToolXWorkSwitch apagado (pestaña Switches del perfil): los frames de ToolX se
        //     descartan enteros — un nodo ajeno en la LAN no puede tocar las secciones.
        //   · ToolX habilitado y VIVO (último frame hace < ToolXTimeoutSec): es el dueño del bit
        //     de trabajo; el módulo de dirección no lo pisa.
        //   · ToolX habilitado, visto alguna vez y PERDIDO (sin frames > timeout): el bit queda en
        //     su último valor y el módulo de dirección sólo lo escribe si SU PROPIO bit cambia
        //     (flanco propio). Sin esto, un microcorte WiFi de ToolX hacía que el AIO — que manda
        //     un valor FIJO cuando no tiene switch cableado — reescribiera el bit, y ese flanco
        //     cortaba las secciones en medio de la pasada (y las volvía a prender al regresar ToolX).
        //   · ToolX nunca visto (o deshabilitado): comportamiento AOG puro, manda el AIO.
        // La polaridad de ToolX se normaliza contra isWorkSwitchActiveLow al recibir el frame:
        // "Activo con contacto cerrado" habla del switch CABLEADO y no invierte a ToolX.
        public const byte ToolXSource = 0x7C;
        public const double ToolXTimeoutSec = 5.0;

        /// <summary>Perfil: aceptar el switch de trabajo inalámbrico ToolX (setF_isToolXWorkSwitch).</summary>
        public bool isToolXWorkSwitch;

        /// <summary>Stopwatch.GetTimestamp() del último frame ToolX aceptado. 0 = nunca. Reloj
        /// MONOTÓNICO a propósito: con DateTime.UtcNow un ajuste de hora (NTP/GPS) de más de
        /// unos segundos daba un "ToolX perdido" espurio y un flanco en las secciones.</summary>
        public long lastToolXTicks;

        /// <summary>Bit de trabajo crudo del ÚLTIMO frame del módulo de dirección, para detectar
        /// sus flancos propios mientras ToolX está perdido. Sólo válido con steerModuleWorkSeen.</summary>
        public bool steerModuleWorkHigh;
        public bool steerModuleWorkSeen;

        /// <summary>Ya se logueó la pérdida de ToolX (evita un log por frame del AIO).</summary>
        public bool toolXLostLogged;

        /// <summary>ToolX mandó al menos un frame aceptado desde el arranque.</summary>
        public bool ToolXSeenEver => lastToolXTicks != 0;

        /// <summary>true si ToolX mandó su bit hace menos de <see cref="ToolXTimeoutSec"/>.</summary>
        public bool IsToolXAlive =>
            lastToolXTicks != 0 &&
            (Stopwatch.GetTimestamp() - lastToolXTicks) < ToolXTimeoutSec * Stopwatch.Frequency;

        /// <summary>ToolX está habilitado en el perfil y vivo: es el dueño del bit de trabajo.</summary>
        public bool IsToolXOwningWorkSwitch => isToolXWorkSwitch && IsToolXAlive;

        /// <summary>Un frame de ToolX fue aceptado ahora.</summary>
        public void MarkToolXSeen()
        {
            lastToolXTicks = Stopwatch.GetTimestamp();
            toolXLostLogged = false;
        }

        /// <summary>Vuelve ToolX a "nunca visto". Se llama al prender o apagar la fila del
        /// perfil: el próximo frame aceptado aplica el NIVEL una vez (ver PgnReceiver) y no
        /// se loguea una pérdida que no existió.</summary>
        public void ResetToolX()
        {
            lastToolXTicks = 0;
            toolXLostLogged = false;
        }

        /// <summary>Cambia "Activo con contacto cerrado" sin generar un flanco espurio. El bit
        /// de ToolX está normalizado contra este flag: si sólo se cambiara el flag, el próximo
        /// frame de ToolX invertiría workSwitchHigh y CheckWorkAndSteerSwitch vería un flanco
        /// que no existió (re-aplicaría el nivel pisando, por ejemplo, un apagado manual del
        /// master). Con ToolX dueño se invierten workSwitchHigh y oldWorkSwitchHigh juntos, así
        /// el estado físico queda igual y no hay flanco. Con el switch cableado no hace falta:
        /// su bit es crudo y no depende del flag.</summary>
        public void SetWorkSwitchActiveLow(bool activeLow)
        {
            if (activeLow == isWorkSwitchActiveLow) return;
            isWorkSwitchActiveLow = activeLow;
            if (IsToolXOwningWorkSwitch)
            {
                workSwitchHigh = !workSwitchHigh;
                oldWorkSwitchHigh = !oldWorkSwitchHigh;
            }
        }

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