using AgLibrary.Logging;

namespace AgOpenGPS
{
    /// <summary>
    /// Stop crítico por boundary (fuera de cerca/turn area) + orquestación de
    /// creación y disparo del youturn (Dubins AB/curva, suavizado, alarmas de
    /// sonido). Vivía embebido en UpdateFixPosition (Position.designer.cs):
    /// se movió a Core porque es lógica de decisión pura sobre objetos ya
    /// portados (bnd/yt/mc) — traspaso portabilidad, bloque 9 matriz Android
    /// (2026-07-20). Reusa IYouTurnHost (ya existía para CYouTurn desde
    /// 2026-07-17) en vez de una interfaz nueva. Los sonidos (CSound, nativo)
    /// cruzan como método.
    /// </summary>
    public class CYouTurnUpdater
    {
        private readonly IYouTurnHost mf;

        public CYouTurnUpdater(IYouTurnHost host)
        {
            mf = host;
        }

        public void UpdateYouTurnState()
        {
            CBoundary bnd = mf.Bnd;
            CYouTurn yt = mf.Yt;
            CModuleComm mc = mf.Mc;

            //if an outer boundary is set, then apply critical stop logic
            if (bnd.bndList != null && bnd.bndList.Count > 0)
            {
                //check if inside all fence
                if (!yt.isYouTurnBtnOn)
                {
                    mc.isOutOfBounds = !bnd.IsPointInsideFenceArea(mf.PivotAxlePos);
                }
                else //Youturn is on
                {
                    bool isInTurnBounds = bnd.IsPointInsideTurnArea(mf.PivotAxlePos) != -1;
                    //Are we inside outer and outside inner all turn boundaries, no turn creation problems
                    //if we are too much off track > 1.3m, kill the diagnostic creation, start again
                    if (isInTurnBounds)
                    {
                        mc.isOutOfBounds = false;
                        //now check to make sure we are not in an inner turn boundary - drive thru is ok
                        if (yt.youTurnPhase != 10)
                        {
                            if (mf.CrossTrackError > 1000)
                            {
                                yt.ResetCreatedYouTurn();
                            }
                            else
                            {
                                if (mf.Tracks[mf.TrackIdx].mode == TrackMode.AB)
                                {
                                    yt.BuildABLineDubinsYouTurn();
                                }
                                else yt.BuildCurveDubinsYouTurn();
                            }

                            if (yt.uTurnStyle == 0 && yt.youTurnPhase == 10)
                            {
                                yt.SmoothYouTurn(6);
                            }

                            if (yt.isTurnCreationTooClose && !yt.turnTooCloseTrigger)
                            {
                                yt.turnTooCloseTrigger = true;
                                if (mf.IsTurnSoundOn)
                                {
                                    mf.PlayTurnTooCloseSound();
                                    Log.EventWriter("U Turn Creation Failure");
                                }
                            }
                        }
                        else if (yt.ytList.Count > 5)//wait to trigger the actual turn since its made and waiting
                        {
                            //distance from current pivot to first point of youturn pattern
                            mf.DistancePivotToTurnLine = glm.Distance(yt.ytList[2], mf.PivotAxlePos);

                            if ((mf.DistancePivotToTurnLine <= 20.0) && (mf.DistancePivotToTurnLine >= 18.0) && !yt.isYouTurnTriggered)

                                if (!mf.IsBoundAlarming)
                                {
                                    if (mf.IsTurnSoundOn) mf.PlayBoundaryAlarmSound();
                                    mf.IsBoundAlarming = true;
                                }

                            //if we are close enough to pattern, trigger.
                            if ((mf.DistancePivotToTurnLine <= 1.0) && (mf.DistancePivotToTurnLine >= 0) && !yt.isYouTurnTriggered)
                            {
                                yt.YouTurnTrigger();
                                mf.IsBoundAlarming = false;
                            }
                        }
                    }
                    else
                    {
                        if (!yt.isYouTurnTriggered)
                        {
                            yt.ResetCreatedYouTurn();
                            mc.isOutOfBounds = !bnd.IsPointInsideFenceArea(mf.PivotAxlePos);
                        }
                    }
                }
            }
            else
            {
                mc.isOutOfBounds = false;
            }
        }
    }
}
