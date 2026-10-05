// ============================================================================
// GuidanceEngineHost.CeroWas.cs — alimenta el "Cero automático del WAS" en
// cada fix.
//
// Detrás del setting setAS_ceroWasAuto (default false): apagado esto NO hace
// nada, ni siquiera mide. Prendido solo junta muestras en
// CeroWasEstadistico (AgOpenGPS.Core, pura y con tests); proponer/aplicar/
// deshacer vive en EngineCeroWasService (PilotX.GuidanceEngine) y el offset
// cambia ÚNICAMENTE cuando el operario toca Aplicar.
//
// Aditivo y compartido con Android (PilotX.GuidanceEngine.Core): el head
// Android no inyecta el servicio, así que con el setting apagado (siempre en
// Android) el tick vuelve en la primera línea.
//
// Un fallo acá NUNCA voltea el pipeline de fix: se loguea con freno y se sigue.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgLibrary.Logging;
using AgOpenGPS.SteerCal;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        /// <summary>Radio mínimo para tratar una curva como recta: con 3 m entre
        /// ejes, 1000 m de radio son 0,17° de rueda, la mitad del umbral de
        /// "el cero está bien".</summary>
        private const double CeroWasRadioRectaM = 1000.0;

        /// <summary>El medidor. Leerlo/escribirlo SIEMPRE con <see cref="CeroWasLock"/>:
        /// el fix lo alimenta desde su hilo y la pantalla lo consulta por HTTP.</summary>
        public readonly CeroWasEstadistico CeroWasMedidor = new CeroWasEstadistico();
        public readonly object CeroWasLock = new object();

        private readonly Stopwatch _relojCeroWas = Stopwatch.StartNew();
        private DateTime _ultimoLogCeroWasUtc = DateTime.MinValue;
        private bool _ceroWasEstabaPrendido;

        private static bool CeroWasAutoPedido
            => global::AgOpenGPS.Properties.Settings.Default.setAS_ceroWasAuto;

        /// <summary>Config del WAS vigente (lo que la placa usa para reportar el ángulo).</summary>
        public static ConfigWasCeroAuto ConfigWasActual()
        {
            var s = global::AgOpenGPS.Properties.Settings.Default;
            return new ConfigWasCeroAuto
            {
                WasOffset = s.setAS_wasOffset,
                CuentasPorGrado = s.setAS_countsPerDegree,
                AckermanPct = s.setAS_ackerman,
                InvertWas = (s.setArdSteer_setting0 & 1) != 0,
            };
        }

        /// <summary>Un fix → quizá una muestra para el cero automático.</summary>
        private void TickCeroWas()
        {
            if (!CeroWasAutoPedido)
            {
                // Apagaron la función: lo medido no sirve para la próxima vez.
                if (_ceroWasEstabaPrendido)
                {
                    lock (CeroWasLock) CeroWasMedidor.Reiniciar();
                    _ceroWasEstabaPrendido = false;
                }
                return;
            }
            _ceroWasEstabaPrendido = true;

            try
            {
                var e = new EntradaCeroWas
                {
                    T = _relojCeroWas.Elapsed.TotalSeconds,
                    AnguloReal = AnguloWasFresco(),
                    PilotoEnganchado = isBtnAutoSteerOn,
                    ManejoLibre = Vehicle != null && Vehicle.isInFreeDriveMode,
                    GuiaRecta = GuiaRectaParaCeroWas(),
                    VelKmh = avgSpeed,
                    MarchaAtras = isReverse,
                    UTurn = Yt != null && Yt.isYouTurnTriggered,
                    // 32000 = sin guía (lo pone CAutoSteerUpdater antes de calcular).
                    ErrorLateralM = Math.Abs((int)guidanceLineDistanceOff) < 29000
                        ? guidanceLineDistanceOff / 1000.0 : double.NaN,
                    RumboRad = fixHeading,
                    RolidoGrados = Ahrs.imuRoll,
                };
                var cfg = ConfigWasActual();
                lock (CeroWasLock) CeroWasMedidor.Evaluar(e, cfg);
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _ultimoLogCeroWasUtc).TotalSeconds > 60)
                {
                    _ultimoLogCeroWasUtc = DateTime.UtcNow;
                    Log.EventWriter("Cero automatico del WAS: error en el tick: " + ex.Message);
                }
            }
        }

        /// <summary>Ángulo del PGN 253 si llegó hace menos de medio segundo; si no, NaN.</summary>
        private double AnguloWasFresco()
        {
            long t = ultimoPgn253Ticks;
            if (t == 0 || Mc == null) return double.NaN;
            double edad = (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
            return edad < 0.5 ? Mc.actualSteerAngleDegrees : double.NaN;
        }

        /// <summary>
        /// AB = recta. Curva = recta solo si, ±5 m alrededor del tractor, el
        /// radio es de más de 1000 m. Contorno o sin guía = no.
        /// </summary>
        private bool GuiaRectaParaCeroWas()
        {
            var trk = Trk;
            if (trk == null || trk.gArr == null || trk.idx < 0 || trk.idx >= trk.gArr.Count) return false;
            var modo = trk.gArr[trk.idx].mode;
            if (modo == TrackMode.AB) return true;
            if (modo != TrackMode.Curve) return false;

            var curva = CurveField;
            if (curva == null) return false;
            List<vec3> lista = curva.curList;          // copia local: se republica desde otro hilo
            int n = lista != null ? lista.Count : 0;
            int idx = curva.currentLocationIndex;
            if (n < 3 || idx < 0 || idx >= n) return false;

            int a = idx, b = idx;
            double atras = 0, adelante = 0;
            while (a > 0 && atras < 5.0) { atras += Dist(lista[a], lista[a - 1]); a--; }
            while (b < n - 1 && adelante < 5.0) { adelante += Dist(lista[b], lista[b + 1]); b++; }
            double largo = atras + adelante;
            if (largo < 4.0) return false;

            double dh = lista[b].heading - lista[a].heading;
            while (dh > Math.PI) dh -= 2 * Math.PI;
            while (dh < -Math.PI) dh += 2 * Math.PI;
            double curvatura = Math.Abs(dh) / largo;
            return curvatura < 1.0 / CeroWasRadioRectaM;
        }

        private static double Dist(vec3 p, vec3 q)
        {
            double dx = p.easting - q.easting, dn = p.northing - q.northing;
            return Math.Sqrt(dx * dx + dn * dn);
        }
    }
}
