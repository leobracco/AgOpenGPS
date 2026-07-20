using System;

namespace AgOpenGPS
{
    /// <summary>
    /// Cálculo de posiciones/anchos de sección y armado del byte de máquina
    /// para los PGN de sección (254/239/229). Vivía embebido en FormGPS
    /// (GPS/Forms/Sections.Designer.cs): se movió a Core porque es cálculo
    /// puro sin GL ni UI — traspaso portabilidad, bloque 9 matriz Android
    /// (2026-07-19). El form conserva los botones/handlers y delega estos
    /// cuatro métodos acá; el host cruza por ISectionsHost.
    /// </summary>
    public class CSectionCalculator
    {
        private readonly ISectionsHost mf;

        public CSectionCalculator(ISectionsHost host)
        {
            mf = host;
        }

        //function to set section positions
        public void SectionSetPosition()
        {
            CTool tool = mf.Tool;
            if (!tool.isSectionsNotZones) return;

            CSection[] section = mf.Section;
            var settings = AgOpenGPS.Properties.Settings.Default;
            double offset = settings.setVehicle_toolOffset;

            section[0].positionLeft = (double)settings.setSection_position1 + offset;
            section[0].positionRight = (double)settings.setSection_position2 + offset;

            section[1].positionLeft = (double)settings.setSection_position2 + offset;
            section[1].positionRight = (double)settings.setSection_position3 + offset;

            section[2].positionLeft = (double)settings.setSection_position3 + offset;
            section[2].positionRight = (double)settings.setSection_position4 + offset;

            section[3].positionLeft = (double)settings.setSection_position4 + offset;
            section[3].positionRight = (double)settings.setSection_position5 + offset;

            section[4].positionLeft = (double)settings.setSection_position5 + offset;
            section[4].positionRight = (double)settings.setSection_position6 + offset;

            section[5].positionLeft = (double)settings.setSection_position6 + offset;
            section[5].positionRight = (double)settings.setSection_position7 + offset;

            section[6].positionLeft = (double)settings.setSection_position7 + offset;
            section[6].positionRight = (double)settings.setSection_position8 + offset;

            section[7].positionLeft = (double)settings.setSection_position8 + offset;
            section[7].positionRight = (double)settings.setSection_position9 + offset;

            section[8].positionLeft = (double)settings.setSection_position9 + offset;
            section[8].positionRight = (double)settings.setSection_position10 + offset;

            section[9].positionLeft = (double)settings.setSection_position10 + offset;
            section[9].positionRight = (double)settings.setSection_position11 + offset;

            section[10].positionLeft = (double)settings.setSection_position11 + offset;
            section[10].positionRight = (double)settings.setSection_position12 + offset;

            section[11].positionLeft = (double)settings.setSection_position12 + offset;
            section[11].positionRight = (double)settings.setSection_position13 + offset;

            section[12].positionLeft = (double)settings.setSection_position13 + offset;
            section[12].positionRight = (double)settings.setSection_position14 + offset;

            section[13].positionLeft = (double)settings.setSection_position14 + offset;
            section[13].positionRight = (double)settings.setSection_position15 + offset;

            section[14].positionLeft = (double)settings.setSection_position15 + offset;
            section[14].positionRight = (double)settings.setSection_position16 + offset;

            section[15].positionLeft = (double)settings.setSection_position16 + offset;
            section[15].positionRight = (double)settings.setSection_position17 + offset;
        }

        //function to calculate the width of each section and update
        public void SectionCalcWidths()
        {
            CTool tool = mf.Tool;
            if (!tool.isSectionsNotZones) return;

            CSection[] section = mf.Section;

            for (int j = 0; j < mf.MaxSections; j++)
            {
                section[j].sectionWidth = (section[j].positionRight - section[j].positionLeft);
                section[j].rpSectionPosition = 250 + (int)(Math.Round(section[j].positionLeft * 10, 0, MidpointRounding.AwayFromZero));
                section[j].rpSectionWidth = (int)(Math.Round(section[j].sectionWidth * 10, 0, MidpointRounding.AwayFromZero));
            }

            //calculate tool width based on extreme right and left values
            tool.width = (section[tool.numOfSections - 1].positionRight) - (section[0].positionLeft);

            //left and right tool position
            tool.farLeftPosition = section[0].positionLeft;
            tool.farRightPosition = section[tool.numOfSections - 1].positionRight;

            //find the right side pixel position
            tool.rpXPosition = 250 + (int)(Math.Round(tool.farLeftPosition * 10, 0, MidpointRounding.AwayFromZero));
            tool.rpWidth = (int)(Math.Round(tool.width * 10, 0, MidpointRounding.AwayFromZero));
        }

        public void SectionCalcMulti()
        {
            CTool tool = mf.Tool;
            CSection[] section = mf.Section;

            double leftside = tool.width / -2.0;
            double defaultSectionWidth = AgOpenGPS.Properties.Settings.Default.setTool_sectionWidthMulti;
            double offset = AgOpenGPS.Properties.Settings.Default.setVehicle_toolOffset;
            section[0].positionLeft = leftside + offset;

            for (int i = 0; i < tool.numOfSections - 1; i++)
            {
                leftside += defaultSectionWidth;

                section[i].positionRight = leftside + offset;
                section[i + 1].positionLeft = leftside + offset;
                section[i].sectionWidth = defaultSectionWidth;
                section[i].rpSectionPosition = 250 + (int)(Math.Round(section[i].positionLeft * 10, 0, MidpointRounding.AwayFromZero));
                section[i].rpSectionWidth = (int)(Math.Round(section[i].sectionWidth * 10, 0, MidpointRounding.AwayFromZero));
            }

            leftside += defaultSectionWidth;
            section[tool.numOfSections - 1].positionRight = leftside + offset;
            section[tool.numOfSections - 1].sectionWidth = defaultSectionWidth;
            section[tool.numOfSections - 1].rpSectionPosition = 250 + (int)(Math.Round(section[tool.numOfSections - 1].positionLeft * 10, 0, MidpointRounding.AwayFromZero));
            section[tool.numOfSections - 1].rpSectionWidth = (int)(Math.Round(section[tool.numOfSections - 1].sectionWidth * 10, 0, MidpointRounding.AwayFromZero));

            //calculate tool width based on extreme right and left values
            tool.width = (section[tool.numOfSections - 1].positionRight) - (section[0].positionLeft);

            //left and right tool position
            tool.farLeftPosition = section[0].positionLeft;
            tool.farRightPosition = section[tool.numOfSections - 1].positionRight;

            //find the right side pixel position
            tool.rpXPosition = 250 + (int)(Math.Round(tool.farLeftPosition * 10, 0, MidpointRounding.AwayFromZero));
            tool.rpWidth = (int)(Math.Round(tool.width * 10, 0, MidpointRounding.AwayFromZero));
        }

        public void BuildMachineByte()
        {
            CTool tool = mf.Tool;
            CSection[] section = mf.Section;
            CPGN_FE p_254 = mf.P254;
            CPGN_EF p_239 = mf.P239;
            CPGN_E5 p_229 = mf.P229;

            if (tool.isSectionsNotZones)
            {
                p_254.pgn[p_254.sc1to8] = 0;
                p_254.pgn[p_254.sc9to16] = 0;

                int number = 0;
                for (int j = 0; j < 8; j++)
                {
                    if (section[j].isSectionOn)
                        number |= 1 << j;
                }
                p_254.pgn[p_254.sc1to8] = unchecked((byte)number);
                number = 0;

                for (int j = 8; j < 16; j++)
                {
                    if (section[j].isSectionOn)
                        number |= 1 << (j - 8);
                }
                p_254.pgn[p_254.sc9to16] = unchecked((byte)number);

                //machine pgn
                p_239.pgn[p_239.sc1to8] = p_254.pgn[p_254.sc1to8];
                p_239.pgn[p_239.sc9to16] = p_254.pgn[p_254.sc9to16];
                p_229.pgn[p_229.sc1to8] = p_254.pgn[p_254.sc1to8];
                p_229.pgn[p_229.sc9to16] = p_254.pgn[p_254.sc9to16];
                p_229.pgn[p_229.toolLSpeed] = unchecked((byte)(tool.farLeftSpeed * 10));
                p_229.pgn[p_229.toolRSpeed] = unchecked((byte)(tool.farRightSpeed * 10));
            }
            else
            {
                //zero all the bytes - set only if on
                for (int i = 5; i < 13; i++)
                {
                    p_229.pgn[i] = 0;
                }

                int number = 0;
                for (int k = 0; k < 8; k++)
                {
                    for (int j = 0; j < 8; j++)
                    {
                        if (section[j + k * 8].isSectionOn)
                            number |= 1 << j;
                    }
                    p_229.pgn[5 + k] = unchecked((byte)number);
                    number = 0;
                }

                //tool speed to calc ramp
                p_229.pgn[p_229.toolLSpeed] = unchecked((byte)(tool.farLeftSpeed * 10));
                p_229.pgn[p_229.toolRSpeed] = unchecked((byte)(tool.farRightSpeed * 10));

                p_239.pgn[p_239.sc1to8] = p_229.pgn[p_229.sc1to8];
                p_239.pgn[p_239.sc9to16] = p_229.pgn[p_229.sc9to16];

                p_254.pgn[p_254.sc1to8] = p_229.pgn[p_229.sc1to8];
                p_254.pgn[p_254.sc9to16] = p_229.pgn[p_229.sc9to16];
            }

            p_239.pgn[p_239.speed] = unchecked((byte)(mf.AvgSpeed * 10));
            p_239.pgn[p_239.tram] = unchecked((byte)mf.Tram.controlByte);
        }
    }
}
