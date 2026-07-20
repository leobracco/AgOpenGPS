using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using AgOpenGPS.Properties;
using System.Globalization;
using System.IO;
using System.Media;
using System.Linq;

namespace AgOpenGPS
{
    // btnStates se movió a AgOpenGPS.Core/Classes/BtnStates.cs (traspaso 2026-07-17)

    public partial class FormGPS
    {
        //Off, Manual, and Auto, 3 states possible
        public btnStates manualBtnState = btnStates.Off;
        public btnStates autoBtnState = btnStates.Off;

        private void MarkAsWorkedTrack()
        {
            // return if there was a track selected
            if (this.trk.idx < 0) return;

            var track = this.trk.gArr[this.trk.idx];

            if (track.mode == TrackMode.AB)
            {
                track.workedTracks.Add(this.ABLine.howManyPathsAway);
            }
            else if (track.mode == TrackMode.Curve)
            {
                track.workedTracks.Add(this.curve.howManyPathsAway);
            }
        }


        //Section Manual and Auto buttons on right side
        private void btnSectionMasterManual_Click(object sender, EventArgs e)
        {
            //System.Media.SystemSounds.Asterisk.Play();
            if (sounds.isSectionsSoundOn && (!mc.isSteerWorkSwitchEnabled || !mc.isSteerWorkSwitchManualSections))
                sounds.sndSectionOff.Play();

            //if Auto is on, turn it off
            autoBtnState = btnStates.Off;
            btnSectionMasterAuto.Image = Properties.Resources.SectionMasterOff;

            switch (manualBtnState)
            {
                case btnStates.Off:
                    manualBtnState = btnStates.On;
                    btnSectionMasterManual.Image = Properties.Resources.ManualOn;

                    //add current track when it doesn't exist in the worked track list
                    MarkAsWorkedTrack();

                    break;

                case btnStates.On:
                    manualBtnState = btnStates.Off;
                    btnSectionMasterManual.Image = Properties.Resources.ManualOff;
                    break;
            }

            //go set the butons and section states
            if (tool.isSectionsNotZones)
                AllSectionsAndButtonsToState(manualBtnState);
            else
                AllZonesAndButtonsToState(manualBtnState);
        }
        private void btnSectionMasterAuto_Click(object sender, EventArgs e)
        {
            //turn off manual if on
            manualBtnState = btnStates.Off;
            btnSectionMasterManual.Image = Properties.Resources.ManualOff;

            switch (autoBtnState)
            {

                case btnStates.Off:

                    autoBtnState = btnStates.Auto;
                    btnSectionMasterAuto.Image = Properties.Resources.SectionMasterOn;
                    if (sounds.isSectionsSoundOn && (!mc.isSteerWorkSwitchEnabled || !mc.isSteerWorkSwitchManualSections))
                        sounds.sndSectionOn.Play();

                    //add current track when it doesn't exist in the worked track list
                    MarkAsWorkedTrack();

                    break;

                case btnStates.Auto:

                    autoBtnState = btnStates.Off;
                    btnSectionMasterAuto.Image = Properties.Resources.SectionMasterOff;
                    if (sounds.isSectionsSoundOn && (!mc.isSteerWorkSwitchEnabled || !mc.isSteerWorkSwitchManualSections))
                        sounds.sndSectionOn.Play();
                    break;
            }

            //go set the butons and section states
            if (tool.isSectionsNotZones)
                AllSectionsAndButtonsToState(autoBtnState);
            else
                AllZonesAndButtonsToState(autoBtnState);

        }

        //cycle thru states - Off,Auto,On
        private btnStates GetNextState(btnStates state)
        {
            if (state == btnStates.Off) return btnStates.Auto;
            else if (state == btnStates.Auto) return btnStates.On;
            else if (state == btnStates.On) return btnStates.Off;
            return btnStates.Off;
        }

        //zone buttons
        private void btnZoneX_Click(object sender, EventArgs e)
        {
            int zoneIndex = int.Parse(((Button)sender).Text);
            btnStates state = GetNextState(section[tool.zoneRanges[zoneIndex] - 1].sectionBtnState);
            if (zoneIndex == 1)
            {
                IndividualZoneAndButtonToState(state, 0, tool.zoneRanges[1], (Button)sender);
            }
            else
            {
                IndividualZoneAndButtonToState(state, tool.zoneRanges[zoneIndex - 1], tool.zoneRanges[zoneIndex], (Button)sender);
            }
        }

        //individual buttons for sections
        private void btnSectionXMan_Click(object sender, EventArgs e)
        {
            int sectionX = int.Parse(((Button)sender).Text);
            btnStates state = GetNextState(section[sectionX - 1].sectionBtnState);
            IndividualSectionAndButonToState(state, sectionX - 1, (Button)sender);
        }

        //Section buttons************************8
        public void AllSectionsAndButtonsToState(btnStates state)
        {
            for (int i = 1; i <= 16; i++)
            {
                IndividualSectionAndButonToState(state, i - 1, this.Controls.Find("btnSection" + i.ToString() + "Man", true).First() as Button);
            }
        }

        private void SetColors(Button button, btnStates state)
        {
            // Dark cockpit palette — funcional, no retinable. Matchea theme.css:
            //   bad #E08A8A, ok #5BC94F, warn #E6C771.
            switch (state)
            {
                case btnStates.Off:
                    button.BackColor = isDay ? Color.FromArgb(178, 62, 62) : Color.FromArgb(224, 138, 138);
                    break;
                case btnStates.Auto:
                    button.BackColor = isDay ? Color.FromArgb(74, 186, 62) : Color.FromArgb(91, 201, 79);
                    break;

                case btnStates.On:
                    button.BackColor = isDay ? Color.FromArgb(196, 154, 46) : Color.FromArgb(230, 199, 113);
                    break;
            }
            button.ForeColor = Color.FromArgb(20, 24, 27); // dark text para legibilidad sobre fondos claros
        }

        private void IndividualSectionAndButonToState(btnStates state, int sectNumber, Button btn)
        {
            section[sectNumber].sectionBtnState = state;
            SetColors(btn, state);
        }

        public void HideSections()
        {
            for (int i = 1; i <= 16; i++)
                (this.Controls.Find("btnSection" + i.ToString() + "Man", true).First() as Button).Visible = false;
        }

        public void HideZones()
        {
            for (int i = 1; i <= 8; i++)
                (this.Controls.Find("btnZone" + i.ToString(), true).First() as Button).Visible = false;
        }

        public void LineUpIndividualSectionBtns()
        {
            if (!isJobStarted)
            {
                HideSections();
                HideZones();
                return;
            }
            HideZones();

            int oglCenter = oglMain.Width / 2 + 30; //PilotX: sin botoneras, el mapa siempre ocupa todo el ancho

            int top = 140;

            int buttonMaxWidth = 360, buttonHeight = 35;

            //PilotX: si el menú de abajo está reabierto (flotando), las secciones
            //van arriba del panel como en el layout normal
            if ((Height - oglMain.Height) < 80) //max size - buttons hid
            {
                top = Height - 85;
                if (panelSim.Visible == true)
                {
                    top = Height - 120;
                    panelSim.Top = Height - 78;
                }
            }
            else //buttons exposed
            {
                top = Height - 135;
                if (panelSim.Visible == true)
                {
                    top = Height - 185;
                    panelSim.Top = Height - 128;
                }
            }

            //PilotX modo barras HTML: la barra de abajo dockeada (74px + margen)
            //taparía las secciones; se corren arriba de la barra y quedan FIJAS.
            if (isHtmlBarsMode)
            {
                top = Height - 165;
                if (panelSim.Visible == true)
                {
                    top = Height - 200;
                    panelSim.Top = Height - 158;
                }
            }

            if (tool.isSectionsNotZones)
            {
                //if (!isJobStarted) top = Height - 40;

                int oglButtonWidth = oglMain.Width * 3 / 4;

                int buttonWidth = Math.Min(oglButtonWidth / tool.numOfSections, buttonMaxWidth);

                Size size = new System.Drawing.Size(buttonWidth, buttonHeight);
                for (int i = 1; i <= 16; i++)
                {
                    Button btn = this.Controls.Find("btnSection" + i.ToString() + "Man", true).First() as Button;
                    btn.Size = size;
                    btn.Top = top;
                    if (i == 1)
                    {
                        btnSection1Man.Left = (oglCenter) - (tool.numOfSections * btnSection1Man.Size.Width) / 2;
                    }
                    else
                    {
                        Button btnPrev = this.Controls.Find("btnSection" + (i - 1).ToString() + "Man", true).First() as Button;
                        btn.Left = btnPrev.Left + btnPrev.Size.Width;
                    }
                    btn.Visible = tool.numOfSections > (i - 1);
                }

            }
        }

        //Zone buttons ************************************
        public void AllZonesAndButtonsToState(btnStates state)
        {
            if (tool.zoneRanges[0] == 0) return;
            if (tool.zoneRanges[1] != 0) IndividualZoneAndButtonToState(state, 0, tool.zoneRanges[1], btnZone1);

            for (int i = 2; i <= 8; i++)
            {
                if (tool.zoneRanges[i] != 0)
                {
                    IndividualZoneAndButtonToState(state,
                        tool.zoneRanges[i - 1],
                        tool.zoneRanges[i],
                        this.Controls.Find("btnZone" + i.ToString(), true).First() as Button
                    );
                }
            }
        }

        private void IndividualZoneAndButtonToState(btnStates state, int sectionStartNumber, int sectionEndNumber, Button btn)
        {
            for (int i = sectionStartNumber; i < sectionEndNumber; i++)
            {
                section[i].sectionBtnState = state;
            }
            SetColors(btn, state);
        }

        public void LineUpAllZoneButtons()
        {
            if (!isJobStarted)
            {
                HideSections();
                HideZones();
                return;
            }

            int oglCenter = oglMain.Width / 2 + 30; //PilotX: sin botoneras, el mapa siempre ocupa todo el ancho

            int top = 130;

            int buttonMaxWidth = 400, buttonHeight = 30;

            //PilotX: ídem secciones — con el menú de abajo flotando, zonas arriba
            if ((Height - oglMain.Height) < 80) //max size - buttons hid
            {
                top = Height - 70;
                if (panelSim.Visible == true)
                {
                    top = Height - 100;
                    panelSim.Top = Height - 60;
                }
            }
            else //buttons exposed
            {
                top = Height - 130;
                if (panelSim.Visible == true)
                {
                    top = Height - 160;
                    panelSim.Top = Height - 120;
                }
            }

            //PilotX modo barras HTML: ídem secciones — zonas arriba de la barra de abajo
            if (isHtmlBarsMode)
            {
                top = Height - 150;
                if (panelSim.Visible == true)
                {
                    top = Height - 185;
                    panelSim.Top = Height - 145;
                }
            }

            //if (tool.zones == 0) return;
            int oglButtonWidth = oglMain.Width * 3 / 4;
            int buttonWidth = Math.Min(oglButtonWidth / tool.zones, buttonMaxWidth);
            Size size = new System.Drawing.Size(buttonWidth, buttonHeight);

            for (int i = 1; i <= 8; i++)
            {
                Button btn = this.Controls.Find("btnZone" + i.ToString(), true).First() as Button;
                btn.Visible = tool.zones > (i - 1);
                btn.Top = top;
                btn.Size = size;
                if (isJobStarted)
                {
                    btn.BackColor = Color.Red;
                }
                else
                {
                    btn.BackColor = Color.Silver;
                }
                if (i == 1)
                {
                    btn.Left = (oglCenter) - (tool.zones * btn.Size.Width) / 2;
                }
                else
                {
                    btn.Left = this.Controls.Find("btnZone" + (i - 1).ToString(), true).First().Left + btnZone1.Size.Width;
                }
            }
        }

        //cálculo de posiciones/anchos/PGN de sección — Core: CSectionCalculator (traspaso portabilidad 2026-07-19)
        public CSectionCalculator sectionCalculator;

        //function to set section positions
        public void SectionSetPosition() => sectionCalculator.SectionSetPosition();

        //function to calculate the width of each section and update
        public void SectionCalcWidths() => sectionCalculator.SectionCalcWidths();

        public void SectionCalcMulti() => sectionCalculator.SectionCalcMulti();

        private void BuildMachineByte() => sectionCalculator.BuildMachineByte();


        private void DoRemoteSwitches()
        {
            //MTZ8302 Feb 2020 and hagre 2024
            if (isJobStarted)
            {
                //check if third bit in the pgn234 received Main-Byte is set to indicate the use of buttons (0) or switches (1) in the SC hardware
                if ((mc.ss[mc.swMain] & (1 << 2)) == 0) // Button hardware by MTZ8302 Feb 2020 (3dr bit - check)
                {
                    HandleButtonHardware();
                }
                else  // Switch hardware by hagre 05 2024
                {
                    HandleSwitchHardware();
                }
            }
        }

        private void HandleButtonHardware()
        {
            //MainSW was used
            if (mc.ss[mc.swMain] != mc.ssP[mc.swMain])
            {
                //Main SW pressed
                if ((mc.ss[mc.swMain] & 1) == 1)
                {
                    //set butto off and then press it = ON
                    autoBtnState = btnStates.Off;
                    btnSectionMasterAuto.PerformClick();
                } // if Main SW ON

                //if Main SW in Arduino is pressed OFF
                if ((mc.ss[mc.swMain] & 2) == 2)
                {
                    //set button on and then press it = OFF
                    autoBtnState = btnStates.Auto;
                    btnSectionMasterAuto.PerformClick();
                } // if Main SW OFF

                mc.ssP[mc.swMain] = mc.ss[mc.swMain];
            }  //Main or shpList SW

            if (tool.isSectionsNotZones)  // NO Zones
            {
                if (mc.ss[mc.swOnGr0] != 0)
                {
                    // ON Signal from Arduino 
                    for (int i = 0; i < 8; i++)
                    {
                        if (((mc.ss[mc.swOnGr0] & (1 << i)) == (1 << i)) && (tool.numOfSections > i))
                        {
                            if (section[i].sectionBtnState != btnStates.Auto)
                            {
                                section[i].sectionBtnState = btnStates.Auto;
                            }
                            PerformSectionClick(i);
                        }
                    }
                    mc.ssP[mc.swOnGr0] = mc.ss[mc.swOnGr0];

                } //if swONLo != 0 
                else
                {
                    if (mc.ssP[mc.swOnGr0] != 0)
                    {
                        mc.ssP[mc.swOnGr0] = 0;
                    }
                }


                if (mc.ss[mc.swOnGr1] != 0)
                {
                    // sections ON signal from Arduino  
                    for (int i = 0; i < 8; i++)
                    {
                        if (((mc.ss[mc.swOnGr1] & (1 << i)) == (1 << i)) && (tool.numOfSections > i + 8))
                        {
                            if (section[i + 8].sectionBtnState != btnStates.Auto)
                            {
                                section[i + 8].sectionBtnState = btnStates.Auto;
                            }
                            PerformSectionClick(i + 8);
                        }
                    }
                    mc.ssP[mc.swOnGr1] = mc.ss[mc.swOnGr1];

                } //if swONHi != 0   
                else
                {
                    if (mc.ssP[mc.swOnGr1] != 0)
                    {
                        mc.ssP[mc.swOnGr1] = 0;
                    }
                }

                // Switches have changed
                if (mc.ss[mc.swOffGr0] != mc.ssP[mc.swOffGr0])
                {
                    //if Main = Auto then change section to Auto if Off signal from Arduino stopped
                    if (autoBtnState == btnStates.Auto)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            if (((mc.ssP[mc.swOffGr0] & (1 << i)) == (1 << i)) && ((mc.ss[mc.swOffGr0] & (1 << i)) != (1 << i)) && (section[i].sectionBtnState == btnStates.Off))
                            {
                                PerformSectionClick(i);
                            }
                        }
                    }
                    mc.ssP[mc.swOffGr0] = mc.ss[mc.swOffGr0];
                }

                if (mc.ss[mc.swOffGr1] != mc.ssP[mc.swOffGr1])
                {
                    //if Main = Auto then change section to Auto if Off signal from Arduino stopped
                    if (autoBtnState == btnStates.Auto)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            if (((mc.ssP[mc.swOffGr1] & (1 << i)) == (1 << i)) && ((mc.ss[mc.swOffGr1] & (1 << i)) != (1 << i)) && (section[i + 8].sectionBtnState == btnStates.Off))
                            {
                                PerformSectionClick(i + 8);
                            }
                        }
                    }
                    mc.ssP[mc.swOffGr1] = mc.ss[mc.swOffGr1];
                }

                // OFF Signal from Arduino
                if (mc.ss[mc.swOffGr0] != 0)
                {
                    //if section SW in Arduino is switched to OFF; check always, if switch is locked to off GUI should not change
                    for (int i = 0; i < 8; i++)
                    {
                        if (((mc.ss[mc.swOffGr0] & (1 << i)) == (1 << i)) && (section[i].sectionBtnState != btnStates.Off))
                        {
                            section[i].sectionBtnState = btnStates.On;
                            PerformSectionClick(i);
                        }
                    }

                } // if swOFFLo !=0

                if (mc.ss[mc.swOffGr1] != 0)
                {
                    //if section SW in Arduino is switched to OFF; check always, if switch is locked to off GUI should not change
                    for (int i = 0; i < 8; i++)
                    {
                        if (((mc.ss[mc.swOffGr1] & (1 << i)) == (1 << i)) && (section[i + 8].sectionBtnState != btnStates.Off))
                        {
                            section[i + 8].sectionBtnState = btnStates.On;
                            PerformSectionClick(i + 8);
                        }
                    }
                } // if swOFFHi !=0
            }
            else// zones to on
            {
                if (mc.ss[mc.swOnGr0] != 0)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        if ((tool.zoneRanges[i + 1] > 0) && ((mc.ss[mc.swOnGr0] & (1 << i)) == (1 << i)))
                        {
                            if (section[tool.zoneRanges[i + 1] - 1].sectionBtnState != btnStates.Auto)
                            {
                                section[tool.zoneRanges[i + 1] - 1].sectionBtnState = btnStates.Auto;
                                PerformZoneClick(i);
                            }
                        }
                    }
                    mc.ssP[mc.swOnGr0] = mc.ss[mc.swOnGr0];
                }
                else
                {
                    if (mc.ssP[mc.swOnGr0] != 0)
                    {
                        mc.ssP[mc.swOnGr0] = 0;
                    }
                }

                // zones to auto
                if (mc.ss[mc.swOffGr0] != mc.ssP[mc.swOffGr0])
                {
                    if (autoBtnState == btnStates.Auto)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            if ((tool.zoneRanges[i + 1] > 0) && ((mc.ssP[mc.swOffGr0] & (1 << i)) == (1 << i)) && ((mc.ss[mc.swOffGr0] & (1 << i)) != (1 << i)) && (section[tool.zoneRanges[i + 1] - 1].sectionBtnState == btnStates.Off))
                            {
                                PerformZoneClick(i);
                            }
                        }
                    }
                    mc.ssP[mc.swOffGr0] = mc.ss[mc.swOffGr0];
                }

                // zones to off
                if (mc.ss[mc.swOffGr0] != 0)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        if ((tool.zoneRanges[i + 1] > 0) && ((mc.ss[mc.swOffGr0] & (1 << i)) == (1 << i)) && (section[tool.zoneRanges[i + 1] - 1].sectionBtnState != btnStates.Off))
                        {
                            section[tool.zoneRanges[i + 1] - 1].sectionBtnState = btnStates.On;
                            PerformZoneClick(i);
                        }
                    }
                }
            }
        }

        private void HandleSwitchHardware()
        {
            //MainSW Byte is AUTO
            if ((autoBtnState != btnStates.Auto) && ((mc.ss[mc.swMain] & 1) == 1))
            {
                //set button off and then press it = ON
                autoBtnState = btnStates.Off;
                btnSectionMasterAuto.PerformClick();
            }
            //MainSW Byte is OFF
            else if ((autoBtnState != btnStates.Off) && ((mc.ss[mc.swMain] & 2) == 2))
            {
                //set button on and then press it = OFF
                autoBtnState = btnStates.Auto;
                btnSectionMasterAuto.PerformClick();
            }

            if (tool.isSectionsNotZones) // NO Zones
            {
                if (mc.ss[mc.swAutoGr0] != 0)
                {
                    // AUTO Signal from Arduino Gr0
                    for (int i = 0; i < 8; i++)
                    {
                        if ((section[i].sectionBtnState != btnStates.Auto) && ((mc.ss[mc.swAutoGr0] & (1 << i)) == (1 << i)) && (tool.numOfSections > i) && (mc.ss[mc.swNumSections] > i))
                        {
                            section[i].sectionBtnState = btnStates.Off;
                            PerformSectionClick(i);
                        }
                    }
                }
                if (mc.ss[mc.swAutoGr1] != 0)
                {
                    // AUTO Signal from Arduino Gr1
                    for (int i = 0; i < 8; i++)
                    {
                        if ((section[i + 8].sectionBtnState != btnStates.Auto) && ((mc.ss[mc.swAutoGr1] & (1 << i)) == (1 << i)) && (tool.numOfSections > i + 8) && (mc.ss[mc.swNumSections] > i + 8))
                        {
                            PerformSectionClick(i + 8);
                        }
                    }
                }

                if (mc.ss[mc.swOnGr0] != 0)
                {
                    // ON Signal from Arduino Gr0
                    for (int i = 0; i < 8; i++)
                    {
                        if (((section[i].sectionBtnState != btnStates.On) && (mc.ss[mc.swOnGr0] & (1 << i)) == (1 << i)) && (tool.numOfSections > i) && (mc.ss[mc.swNumSections] > i))
                        {
                            section[i].sectionBtnState = btnStates.Auto;
                            PerformSectionClick(i);
                        }
                    }
                }
                if (mc.ss[mc.swOnGr1] != 0)
                {
                    // ON Signal from Arduino Gr1
                    for (int i = 0; i < 8; i++)
                    {
                        if ((section[i + 8].sectionBtnState != btnStates.On) && ((mc.ss[mc.swOnGr1] & (1 << i)) == (1 << i)) && (tool.numOfSections > i + 8) && (mc.ss[mc.swNumSections] > i + 8))
                        {
                            section[i + 8].sectionBtnState = btnStates.Auto;
                            PerformSectionClick(i + 8);
                        }
                    }
                }


                if (mc.ss[mc.swOffGr0] != 0)
                {
                    // OFF Signal from Arduino Gr0
                    for (int i = 0; i < 8; i++)
                    {
                        if ((section[i].sectionBtnState != btnStates.Off) && ((mc.ss[mc.swOffGr0] & (1 << i)) == (1 << i)) && (tool.numOfSections > i))  // !check mc.ss[tool.numOfSections] => to be on the save side by switching eveything off 
                        {
                            section[i].sectionBtnState = btnStates.On;
                            PerformSectionClick(i);
                        }
                    }
                }
                if (mc.ss[mc.swOffGr1] != 0)
                {
                    // OFF Signal from Arduino Gr1
                    for (int i = 0; i < 8; i++)
                    {
                        if ((section[i + 8].sectionBtnState != btnStates.Off) && ((mc.ss[mc.swOffGr1] & (1 << i)) == (1 << i)) && (tool.numOfSections > i + 8)) // !check mc.ss[tool.numOfSections] => to be on the save side by switching eveything off
                        {
                            PerformSectionClick(i + 8);
                        }
                    }
                }
            }
            else
            {
                // zones to auto
                if (mc.ss[mc.swAutoGr0] != 0)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        if ((tool.zoneRanges[i + 1] > 0) && ((mc.ss[mc.swAutoGr0] & (1 << i)) == (1 << i)) && ((mc.ss[mc.swOnGr0] & (1 << i)) != (1 << i)) && ((mc.ss[mc.swOffGr0] & (1 << i)) != (1 << i)) && (section[tool.zoneRanges[i + 1] - 1].sectionBtnState != btnStates.Auto))
                        {
                            section[tool.zoneRanges[i + 1] - 1].sectionBtnState = btnStates.Off;
                            PerformZoneClick(i);
                        }
                    }

                }

                // zones to on
                if (mc.ss[mc.swOnGr0] != 0)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        if ((tool.zoneRanges[i + 1] > 0) && ((mc.ss[mc.swOnGr0] & (1 << i)) == (1 << i)) && ((mc.ss[mc.swOffGr0] & (1 << i)) != (1 << i)) && (section[tool.zoneRanges[i + 1] - 1].sectionBtnState != btnStates.On))
                        {
                            section[tool.zoneRanges[i + 1] - 1].sectionBtnState = btnStates.Auto;
                            PerformZoneClick(i);

                        }
                    }
                }

                // zones to off
                if (mc.ss[mc.swOffGr0] != 0)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        if ((tool.zoneRanges[i + 1] > 0) && ((mc.ss[mc.swOffGr0] & (1 << i)) == (1 << i)) && (section[tool.zoneRanges[i + 1] - 1].sectionBtnState != btnStates.Off))
                        {
                            section[tool.zoneRanges[i + 1] - 1].sectionBtnState = btnStates.On;
                            PerformZoneClick(i);
                        }
                    }
                }
            }
        }


        private void PerformZoneClick(int Btn)
        {
            (this.Controls.Find("btnZone" + (Btn + 1).ToString(), true).First() as Button).PerformClick();
        }

        private void PerformSectionClick(int Btn)
        {
            (this.Controls.Find("btnSection" + (Btn + 1).ToString() + "Man", true).First() as Button).PerformClick();
        }

        // =====================================================================
        // QuantiX calibration helpers
        // Usados por FormQuantiXCalibrar: durante la calibración el motor solo
        // dosifica si las secciones AOG están abiertas (SectionXBridge propaga
        // el estado al PCA9685/embrague). Forzamos manual=ON mientras dura la
        // ventana de calibración y restauramos el estado previo al cerrar.
        // =====================================================================
        private btnStates _calSavedManualState;
        private btnStates _calSavedAutoState;
        private bool _calSectionsForced;

        public void ForceAllSectionsOnForCalibration()
        {
            if (_calSectionsForced) return;
            _calSavedManualState = manualBtnState;
            _calSavedAutoState = autoBtnState;
            _calSectionsForced = true;

            // Apagar auto primero si está prendido (un click lo apaga).
            if (autoBtnState != btnStates.Off)
                btnSectionMasterAuto.PerformClick();

            // Encender manual si todavía no lo está.
            if (manualBtnState != btnStates.On)
                btnSectionMasterManual.PerformClick();
        }

        public void RestoreSectionStateAfterCalibration()
        {
            if (!_calSectionsForced) return;
            _calSectionsForced = false;

            // Estado actual: manual=On, auto=Off (lo dejamos así arriba).
            // Llevar todo a Off para arrancar limpio.
            if (manualBtnState == btnStates.On)
                btnSectionMasterManual.PerformClick(); // On -> Off
            if (autoBtnState != btnStates.Off)
                btnSectionMasterAuto.PerformClick();   // Auto -> Off

            // Re-aplicar el estado guardado.
            if (_calSavedAutoState == btnStates.Auto)
                btnSectionMasterAuto.PerformClick();
            else if (_calSavedManualState == btnStates.On)
                btnSectionMasterManual.PerformClick();
        }
    }
}
