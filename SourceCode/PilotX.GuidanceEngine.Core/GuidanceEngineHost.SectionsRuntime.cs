// ============================================================================
// GuidanceEngineHost.SectionsRuntime.cs — control de secciones + cobertura,
// portado del bloque "Section Control" de FormGPS.oglBack_Paint
// (GPS/Forms/OpenGL.Designer.cs ~1164-1538) al tick headless del engine.
//
// Sin esto, el engine nunca crea tiras de cobertura (CPatches): TriStripField
// queda vacío, AddSectionOrPathPoints (CPositionUpdater, ya corre por fix) no
// tiene ninguna tira con isDrawing=true para llenar, y /api/aog/coverage
// devuelve sections:[] → "no pinta".
//
// MVP: se saltea el anti-overlap por PÍXELES (grnPixels, que en FormGPS sale de
// GL.ReadPixels y NO es portable a headless). Una sección en Auto queda ON si
// está dentro del boundary (o no hay boundary) y el tractor avanza a velocidad
// suficiente. El resto (máquina de estados on/off + mapping + ciclo de tiras +
// BuildMachineByte) se porta fiel. Los triángulos los agrega
// AddSectionOrPathPoints sobre las tiras con isDrawing=true.
//
// DIFERIDO (necesitan un rasterizador de cobertura por software): anti-overlap
// (no re-pintar lo ya trabajado), headland-por-pixel, y minCoverage.
// ============================================================================

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        // Equivalente al lastNumber de FormGPS (OpenGL.Designer.cs:46): bitmask de
        // qué secciones estaban mapeando en el fix anterior. Solo cuando cambia se
        // crean/cierran tiras.
        private ulong lastSectionNumber = 0;

        /// <summary>
        /// Corte por área ya trabajada. null (el default) = como siempre: en Auto
        /// la sección va encendida. Lo inyecta el proyecto de afuera; ver
        /// IAntiSolapeSecciones para por qué la dependencia va en ese sentido.
        /// </summary>
        public IAntiSolapeSecciones AntiSolape;

        // Llamar en CADA fix con lote abierto (desde UpdateFixPosition), después
        // del pipeline de posición (que ya corrió AddSectionOrPathPoints) y antes
        // de enviar P239/P229 (BuildMachineByte puebla sus bytes de sección).
        public void SectionControlToUpdate()
        {
            var tool = Tool;
            var section = Sections;
            var triStrip = TriStripField;
            double slowCut = Vehicle.slowSpeedCutoff;

            // Poner al día la cobertura conocida ANTES de decidir, así el corte
            // usa lo que se pintó en este mismo fix y no lo del anterior.
            bool antiSolape = AntiSolape != null && AntiSolape.Habilitado;
            if (antiSolape) AntiSolape.Sincronizar();

            // Dónde está la herramienta respecto de la CABECERA (CHead, Core):
            // puebla Section[j].isLookOnInHeadland con los puntos lookahead de
            // cada sección. En FormGPS esto corre en el DIBUJO (oglBack_Paint,
            // OpenGL.Designer.cs:1022/1170) — como el motor no dibuja, no
            // corría nunca y el corte por cabecera era letra muerta.
            bool corteCabecera = Bnd.isHeadlandOn && Bnd.isSectionControlledByHeadland
                && Bnd.bndList.Count > 0 && Bnd.bndList[0].hdLine.Count > 0;
            if (corteCabecera)
            {
                Bnd.WhereAreToolCorners();
                Bnd.WhereAreToolLookOnPoints();
            }

            // ---- 1) Decisión on/off por sección ----
            for (int j = 0; j < tool.numOfSections; j++)
            {
                // Off, muy lento, o yendo para atrás.
                if (section[j].sectionBtnState == btnStates.Off || avgSpeed < slowCut || section[j].speedPixels < 0)
                {
                    section[j].sectionOnRequest = false;
                    section[j].sectionOffRequest = true;

                    // Manual On: forzar encendido igual.
                    if (section[j].sectionBtnState == btnStates.On)
                    {
                        section[j].sectionOnRequest = true;
                        section[j].sectionOffRequest = false;
                    }
                    continue;
                }

                // Manual On: forzar encendido.
                if (section[j].sectionBtnState == btnStates.On)
                {
                    section[j].sectionOnRequest = true;
                    section[j].sectionOffRequest = false;
                    continue;
                }

                // Auto. Sin anti-solape enchufado, la sección va encendida (que
                // era todo el comportamiento del MVP). Con anti-solape, decide
                // cuánto de su ancho cae sobre lo ya trabajado.
                section[j].isSectionRequiredOn = true;
                if (antiSolape)
                {
                    section[j].isSectionRequiredOn = AntiSolape.SeccionRequeridaOn(
                        section[j].leftPoint.easting,
                        section[j].leftPoint.northing,
                        section[j].rightPoint.easting,
                        section[j].rightPoint.northing,
                        toolPivotPos.heading,
                        avgSpeed,
                        section[j].isSectionOn);
                }

                // Fuera de boundary → off. Sin boundary, isInBoundary queda true
                // (default) y no se apaga.
                if (Bnd.bndList.Count > 0 && !section[j].isInBoundary)
                {
                    section[j].isSectionRequiredOn = false;
                    section[j].sectionOffRequest = true;
                    section[j].sectionOnRequest = false;
                    section[j].sectionOffTimer = 0;
                    section[j].sectionOnTimer = 0;
                    continue;
                }

                // Corte por CABECERA (geométrico): con "secciones controladas
                // en cabecera" activo, la sección cuyos puntos lookahead
                // cayeron enteros dentro de la franja de cabecera se apaga —
                // pisarla es donde se dobla, no donde se dosifica. El
                // refinamiento por píxeles del legacy (re-entrar si adelante
                // hay área sin trabajar) necesita el rasterizador y queda
                // diferido, igual que el anti-overlap por píxeles.
                if (corteCabecera && section[j].isLookOnInHeadland)
                {
                    section[j].isSectionRequiredOn = false;
                    section[j].sectionOffRequest = true;
                    section[j].sectionOnRequest = false;
                    continue;
                }

                section[j].sectionOnRequest = section[j].isSectionRequiredOn;
                section[j].sectionOffRequest = !section[j].sectionOnRequest;
            }

            // ---- 2) Timers de sección + de mapping (idéntico a FormGPS) ----
            for (int j = 0; j < tool.numOfSections; j++)
            {
                if (section[j].sectionOnRequest) section[j].isSectionOn = true;

                if (tool.turnOffDelay > 0)
                {
                    if (!section[j].sectionOffRequest) section[j].sectionOffTimer = (int)(gpsHz * tool.turnOffDelay);
                    if (section[j].sectionOffTimer > 0) section[j].sectionOffTimer--;
                    if (section[j].sectionOffRequest && section[j].sectionOffTimer == 0)
                    {
                        if (section[j].isSectionOn) section[j].isSectionOn = false;
                    }
                }
                else
                {
                    if (section[j].sectionOffRequest) section[j].isSectionOn = false;
                }

                // Mapping timers.
                if (section[j].sectionOnRequest && !section[j].isMappingOn && section[j].mappingOnTimer == 0)
                {
                    section[j].mappingOnTimer = (int)(tool.lookAheadOnSetting * gpsHz - 1);
                }
                else if (section[j].sectionOnRequest && section[j].isMappingOn && section[j].mappingOffTimer > 1)
                {
                    section[j].mappingOffTimer = 0;
                    section[j].mappingOnTimer = (int)(tool.lookAheadOnSetting * gpsHz - 1);
                }

                if (tool.lookAheadOffSetting > 0)
                {
                    if (section[j].sectionOffRequest && section[j].isMappingOn && section[j].mappingOffTimer == 0)
                        section[j].mappingOffTimer = (int)(tool.lookAheadOffSetting * gpsHz + 4);
                }
                else if (tool.turnOffDelay > 0)
                {
                    if (section[j].sectionOffRequest && section[j].isMappingOn && section[j].mappingOffTimer == 0)
                        section[j].mappingOffTimer = (int)(tool.turnOffDelay * gpsHz);
                }
                else
                {
                    section[j].mappingOffTimer = 0;
                }

                if (section[j].sectionOnRequest)
                {
                    section[j].mappingOffTimer = 0;
                    if (section[j].mappingOnTimer > 1) section[j].mappingOnTimer--;
                    else section[j].isMappingOn = true;
                }

                if (section[j].sectionOffRequest)
                {
                    section[j].mappingOnTimer = 0;
                    if (section[j].mappingOffTimer > 1) section[j].mappingOffTimer--;
                    else section[j].isMappingOn = false;
                }
            }

            // ---- 3) Cambio de estado on/off → crear/gestionar tiras (CPatches) ----
            ulong number = 0;
            for (int j = 0; j < tool.numOfSections; j++)
                if (section[j].isMappingOn) number |= 1ul << j;

            if (number != lastSectionNumber)
            {
                int sectionOnOffZones = 0, patchingZones = 0;

                if (number == 0)
                {
                    for (int j = 0; j < triStrip.Count; j++)
                        if (triStrip[j].isDrawing) triStrip[j].TurnMappingOff();
                }
                else if (!tool.isMultiColoredSections)
                {
                    // Agrupar secciones contiguas que mapean en zonas.
                    for (int j = 0; j < tool.numOfSections; j++)
                    {
                        if (!section[j].isMappingOn) continue;

                        if (triStrip.Count < sectionOnOffZones + 1) triStrip.Add(new CPatches(this));

                        triStrip[sectionOnOffZones].newStartSectionNum = j;
                        while ((j + 1) < tool.numOfSections && section[j + 1].isMappingOn) j++;
                        triStrip[sectionOnOffZones].newEndSectionNum = j;
                        sectionOnOffZones++;
                    }

                    for (int j = 0; j < triStrip.Count; j++)
                        if (triStrip[j].isDrawing) patchingZones++;

                    bool isOk = (patchingZones == sectionOnOffZones && sectionOnOffZones < 3);

                    if (isOk)
                        for (int j = 0; j < sectionOnOffZones; j++)
                            if (triStrip[j].newStartSectionNum > triStrip[j].currentEndSectionNum
                                || triStrip[j].newEndSectionNum < triStrip[j].currentStartSectionNum)
                                isOk = false;

                    if (isOk)
                    {
                        for (int j = 0; j < sectionOnOffZones; j++)
                        {
                            if (triStrip[j].newStartSectionNum != triStrip[j].currentStartSectionNum
                                || triStrip[j].newEndSectionNum != triStrip[j].currentEndSectionNum)
                            {
                                triStrip[j].AddMappingPoint(0);
                                triStrip[j].currentStartSectionNum = triStrip[j].newStartSectionNum;
                                triStrip[j].currentEndSectionNum = triStrip[j].newEndSectionNum;
                                triStrip[j].AddMappingPoint(0);
                            }
                        }
                    }
                    else
                    {
                        // Demasiado complicado → cerrar y arrancar tiras nuevas.
                        for (int j = 0; j < triStrip.Count; j++)
                            if (triStrip[j].isDrawing) triStrip[j].TurnMappingOff();

                        for (int j = 0; j < sectionOnOffZones; j++)
                        {
                            triStrip[j].currentStartSectionNum = triStrip[j].newStartSectionNum;
                            triStrip[j].currentEndSectionNum = triStrip[j].newEndSectionNum;
                            triStrip[j].TurnMappingOn(0);
                        }
                    }
                }
                else // isMultiColoredSections: una tira por sección
                {
                    for (int j = 0; j < tool.numOfSections; j++)
                    {
                        if (triStrip.Count < sectionOnOffZones + 1) triStrip.Add(new CPatches(this));

                        triStrip[sectionOnOffZones].newStartSectionNum = j;
                        triStrip[sectionOnOffZones].newEndSectionNum = j;
                        sectionOnOffZones++;

                        if (!section[j].isMappingOn)
                        {
                            if (triStrip[j].isDrawing) triStrip[j].TurnMappingOff();
                        }
                        else
                        {
                            triStrip[j].currentStartSectionNum = triStrip[j].newStartSectionNum;
                            triStrip[j].currentEndSectionNum = triStrip[j].newEndSectionNum;
                            triStrip[j].TurnMappingOn(j);
                        }
                    }
                }

                lastSectionNumber = number;
            }

            // ---- 4) Bytes de sección a los módulos (P254/P239/P229) ----
            SectionCalculator.BuildMachineByte();

            // ---- 5) Bajar cobertura a disco cada tanto ----
            // Va acá porque es el único lugar que corre una vez por fix con lote
            // abierto. Sin esto la cobertura solo vive en RAM y se pierde al
            // cerrar el lote o el proceso.
            TickGuardadoCobertura();
        }
    }
}
