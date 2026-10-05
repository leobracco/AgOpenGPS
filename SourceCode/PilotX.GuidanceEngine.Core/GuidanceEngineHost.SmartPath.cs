// ============================================================================
// GuidanceEngineHost.SmartPath.cs — guía por última pasada.
//
// El operario la prende desde Guías (comando "smartpath_on"), hace la primera
// pasada a mano y gira en la cabecera: al encarar la pasada siguiente, la
// pasada recién hecha queda como guía (CTrk modo Curve, armada EXACTAMENTE
// igual que "AB + Curva": TrkBuilder_RecordCurveB). El seguidor de curva toma
// la paralela más cercana, que es la del lado no trabajado. En cada giro la
// guía se reemplaza por la última pasada.
//
// Es una función NUEVA que solo corre si el operario la elige: apagada no
// cambia nada del guiado. Se apaga sola (y lo deja logueado) si el operario
// elige otra guía, prende el contorno o cierra el lote — nunca pisa una guía
// que el operario puso a mano.
//
// La detección de fin de pasada es pura y está testeada: SmartPathGrabador
// (AgOpenGPS.Core, tests en SmartPathGrabadorTests).
// ============================================================================

using System;
using System.Collections.Generic;
using AgLibrary.Logging;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        private readonly SmartPathGrabador _smartPath = new SmartPathGrabador();
        private volatile bool _smartPathOn;
        private CTrk _smartPathTrk;            // guía que armó SmartPath (null = todavía ninguna)
        private List<vec3> _smartPathPendiente; // pasada terminada esperando que no haya un giro en curso

        /// <summary>Guía por última pasada prendida.</summary>
        public bool SmartPathActivo => _smartPathOn;

        /// <summary>Pasadas que ya se volvieron guía desde que se prendió.</summary>
        public int SmartPathPasadas => _smartPath.PasadasTerminadas;

        /// <summary>Prende la guía por última pasada. false = no hay lote abierto.</summary>
        private bool PrenderSmartPath()
        {
            if (!IsJobStarted) return false;
            if (_smartPathOn) return true;

            ProcesarFixSerializado(() =>
            {
                // Arranca de cero: sin guía activa (la primera pasada es a mano)
                // y sin contorno, que le ganaría al guiado por curva.
                if (Ct.isContourBtnOn) ToggleContour();
                if (isBtnAutoSteerOn) ((IAutoSteerHost)this).PerformAutoSteerClick();
                Yt.isYouTurnBtnOn = false;
                Yt.ResetYouTurn();
                Trk.isAutoTrack = false;
                Trk.idx = -1;
                CurveField.isCurveValid = false;
                ABLineField.isABValid = false;

                _smartPath.Reiniciar();
                _smartPathTrk = null;
                _smartPathPendiente = null;
                _smartPathOn = true;
            });
            Log.EventWriter("GuidanceEngine: guia por ultima pasada ACTIVA — primera pasada a mano");
            return true;
        }

        /// <summary>
        /// Apaga la función. La última guía que armó queda activa como una
        /// curva más: apagar no le saca la guía al operario a mitad de pasada.
        /// </summary>
        private void ApagarSmartPath(string motivo)
        {
            if (!_smartPathOn) return;
            _smartPathOn = false;
            _smartPath.Reiniciar();
            _smartPathTrk = null;
            _smartPathPendiente = null;
            Log.EventWriter("GuidanceEngine: guia por ultima pasada APAGADA (" + motivo + ")");
        }

        /// <summary>Un fix: graba la pasada y, si terminó, la instala como guía.
        /// Corre dentro de UpdateFixPosition (mismo hilo y lock que el guiado).</summary>
        private void TickSmartPath()
        {
            if (!_smartPathOn) return;
            if (!IsJobStarted) { ApagarSmartPath("sin lote"); return; }
            if (Ct.isContourBtnOn) { ApagarSmartPath("se prendio el contorno"); return; }

            // Si la guía activa ya no es la de SmartPath, el operario eligió
            // otra (o apagó las guías): se respeta y la función se apaga.
            int esperado = _smartPathTrk == null ? -1 : Trk.gArr.IndexOf(_smartPathTrk);
            if (Trk.idx != esperado)
            {
                ApagarSmartPath("el operario eligio otra guia");
                return;
            }

            // Marcha atrás o parado: no es pasada (y la marcha atrás parecería un giro de 180°).
            if (!isReverse && avgSpeed > 0.5)
            {
                var pasada = _smartPath.Agregar(pivotAxlePos.easting, pivotAxlePos.northing);
                if (pasada != null) _smartPathPendiente = pasada;
            }

            // Con un giro en U en curso no se cambia la referencia: el giro
            // está armado contra la guía vieja y al terminar mueve la pasada.
            if (_smartPathPendiente != null && !Yt.isYouTurnTriggered)
            {
                var p = _smartPathPendiente;
                _smartPathPendiente = null;
                InstalarGuiaSmartPath(p);
            }
        }

        // Mismo armado que TrkBuilder_RecordCurveB (AB + Curva), que es el
        // camino probado: espaciado 1,6 m, rumbos, extensiones en las puntas y
        // el corrimiento de medio ancho para que la pasada 0 quede donde se manejó.
        private void InstalarGuiaSmartPath(List<vec3> pasada)
        {
            var pts = new List<vec3>(pasada);
            CABCurve.MakePointMinimumSpacing(ref pts, 1.6);
            if (pts.Count < 4) return;
            CABCurve.CalculateHeadings(ref pts);

            double x = 0, y = 0;
            foreach (vec3 pt in pts) { x += Math.Cos(pt.heading); y += Math.Sin(pt.heading); }
            double aveH = Math.Atan2(y / pts.Count, x / pts.Count);
            if (aveH < 0) aveH += glm.twoPI;

            var trk = new CTrk
            {
                mode = TrackMode.Curve,
                ptA = new vec2(pts[0].easting, pts[0].northing),
                ptB = new vec2(pts[pts.Count - 1].easting, pts[pts.Count - 1].northing),
                heading = aveH,
                name = "Última pasada " + _smartPath.PasadasTerminadas,
            };

            CurveField.AddFirstLastPoints(ref pts);
            CABCurve.CalculateHeadings(ref pts);
            trk.curvePts.AddRange(pts);

            // Objeto NUEVO en el lugar de la guía anterior: una construcción de
            // curva que esté corriendo en otro hilo sigue con la vieja intacta.
            int idx = _smartPathTrk == null ? -1 : Trk.gArr.IndexOf(_smartPathTrk);
            if (idx >= 0) Trk.gArr[idx] = trk;
            else { Trk.gArr.Add(trk); idx = Trk.gArr.Count - 1; }
            _smartPathTrk = trk;

            Trk.idx = idx;
            Trk.NudgeRefCurve((Tool.width - Tool.overlap) * 0.5 + Tool.offset);
            CurveField.isCurveValid = false;

            Log.EventWriter("GuidanceEngine: guia por ultima pasada — " + trk.name
                + " (" + pasada.Count + " puntos)");
        }
    }
}
