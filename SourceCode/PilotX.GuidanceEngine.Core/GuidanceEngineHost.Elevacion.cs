// ============================================================================
// GuidanceEngineHost.Elevacion.cs — planimetría fase 1: registrar alturas del
// lote mientras se trabaja, para armar después mapas de altura (fase 2, en la
// nube — acá NO).
//
// Detrás del setting EXISTENTE setDisplay_isLogElevation ("Registrar
// elevación", default false): apagado, esto no hace nada — ni toca disco.
//
// Qué graba y cuándo lo decide RegistroElevacion (AgOpenGPS.Core, pura y con
// tests): solo RTK fijo, un punto cada ≥1 m, nada parado ni en reversa, altura
// del SUELO (antena corregida por rolido) y sin saltos de fix. Dónde lo
// escribe: <lote>/Elevation.txt con el formato de AOG, en append con buffer
// (EscritorElevacion: flush cada 200 filas o 15 s, y siempre al cerrar el
// lote o parar el motor).
//
// Se llama desde IHeadingHost.TheRest(): ahí pn.fix YA viene corregido por
// rolido y por el offset lateral de la antena (CHeadingUpdater), así que no se
// corrige de nuevo. El IPositionHost.IsLogElevation heredado de AOG sigue en
// false a propósito: su camino (CPositionUpdater.TheRest → sbGrid) grababa
// cualquier calidad de fix, solo con secciones prendidas y sin corregir el
// rolido, y nadie volcaba sbGrid a disco en el motor.
//
// Un fallo acá (disco lleno, carpeta borrada) NUNCA voltea el pipeline de fix:
// se loguea con freno (una vez por minuto) y se sigue.
// ============================================================================

using System;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        private readonly RegistroElevacion _registroElevacion = new RegistroElevacion();
        private readonly EscritorElevacion _escritorElevacion = new EscritorElevacion();
        private DateTime _ultimaMuestraElevacionUtc = DateTime.MinValue;
        private DateTime _ultimoLogElevacionUtc = DateTime.MinValue;

        /// <summary>
        /// Aceptar fixes del simulador (calidad 8) en el registro de alturas.
        /// SOLO pruebas de banco: lo prende `--elevacion-sim` en Program. Con
        /// el simulador la altitud es constante, así que el mapa sale plano.
        /// </summary>
        public bool ElevacionAceptaSimulador
        {
            get => _registroElevacion.AceptarSimulador;
            set => _registroElevacion.AceptarSimulador = value;
        }

        private static bool RegistroElevacionPedido
            => global::AgOpenGPS.Properties.Settings.Default.setDisplay_isLogElevation;

        /// <summary>
        /// Estado para el operario (snake, lo lee la Configuración nativa):
        /// "apagado" (setting off) · "sin_lote" · "grabando" · "sin_rtk"
        /// (en pausa: sin RTK fijo, o sin fixes hace más de 3 s) · "detenido"
        /// (menos de 0,5 km/h) · "marcha_atras".
        /// </summary>
        public string ElevacionEstado
        {
            get
            {
                if (!RegistroElevacionPedido) return "apagado";
                if (!IsJobStarted) return "sin_lote";
                if ((DateTime.UtcNow - _ultimaMuestraElevacionUtc).TotalSeconds > 3) return "sin_rtk";
                switch (_registroElevacion.Estado)
                {
                    case EstadoElevacion.Grabando: return "grabando";
                    case EstadoElevacion.Detenido: return "detenido";
                    case EstadoElevacion.MarchaAtras: return "marcha_atras";
                    default: return "sin_rtk";
                }
            }
        }

        /// <summary>Puntos de Elevation.txt del lote abierto (los que había + los nuevos).
        /// 0 si el registro todavía no tomó el lote (setting apagado o sin fixes).</summary>
        public int ElevacionPuntos => _escritorElevacion.Directorio != null ? _escritorElevacion.Puntos : 0;

        /// <summary>Fixes descartados por salto de altura en este lote (diagnóstico).</summary>
        public int ElevacionDescartados => _registroElevacion.Descartados;

        /// <summary>Último error de disco del registro (null = anda).</summary>
        public string ElevacionError => _escritorElevacion.UltimoError;

        /// <summary>Un fix ya corregido → quizá un punto en Elevation.txt.</summary>
        private void TickRegistroElevacion()
        {
            try
            {
                if (!RegistroElevacionPedido || !IsJobStarted || string.IsNullOrEmpty(currentFieldDirectory))
                {
                    // Apagaron el setting con el lote abierto: lo pendiente a disco ya.
                    if (_escritorElevacion.Directorio != null) CerrarRegistroElevacion();
                    return;
                }

                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                if (!string.Equals(_escritorElevacion.Directorio, dir, StringComparison.OrdinalIgnoreCase))
                {
                    _registroElevacion.Reiniciar();
                    _escritorElevacion.Abrir(dir, AppModelField.LocalPlane.Origin);
                    Log.EventWriter("Registro de alturas: lote " + currentFieldDirectory
                        + " (" + _escritorElevacion.Puntos + " puntos previos)");
                }

                var muestra = new MuestraElevacion
                {
                    Easting = Pn.fix.easting,
                    Northing = Pn.fix.northing,
                    AltitudAntena = Pn.altitude,
                    CalidadFix = Pn.fixQuality,
                    VelocidadKmh = avgSpeed,
                    MarchaAtras = isReverse,
                    RolidoGrados = Ahrs.imuRoll,
                    // El cabeceo no se usa: CAHRS.imuPitch llega crudo del PANDA
                    // (sin escala ni cero confiables) y nadie más lo consume. Con
                    // 5° de cabeceo y 3 m de antena el error es 1 cm.
                    CabeceoGrados = double.NaN,
                    AlturaAntena = Vehicle.VehicleConfig.AntennaHeight,
                };
                _ultimaMuestraElevacionUtc = DateTime.UtcNow;

                if (_registroElevacion.Evaluar(muestra, out PuntoElevacion p))
                {
                    // pn.fix lleva la deriva ("Corregir posición") sumada; la
                    // lat/lon del archivo es la del terreno real, sin deriva.
                    GeoCoord local = new GeoCoord(p.Northing, p.Easting) - AppModelField.SharedFieldProperties.DriftCompensation;
                    Wgs84 ll = AppModelField.LocalPlane.ConvertGeoCoordToWgs84(local);
                    double rolido = Ahrs.imuRoll;
                    if (rolido == RegistroElevacion.CentinelaSinImu || double.IsNaN(rolido)) rolido = 0;
                    _escritorElevacion.Agregar(new FilaElevacion
                    {
                        Latitud = ll.Latitude,
                        Longitud = ll.Longitude,
                        AlturaSuelo = p.AlturaSuelo,
                        Calidad = Pn.fixQuality,
                        Easting = p.Easting,
                        Northing = p.Northing,
                        RumboRad = fixHeading,
                        RolidoGrados = rolido,
                    });
                }
                else
                {
                    _escritorElevacion.FlushSiVencio();
                }

                if (_escritorElevacion.UltimoError != null) LogElevacionConFreno("disco: " + _escritorElevacion.UltimoError);
            }
            catch (Exception ex)
            {
                LogElevacionConFreno(ex.Message);
            }
        }

        /// <summary>Flushea Elevation.txt y suelta el lote. Lo llaman CloseField y Stop.</summary>
        private void CerrarRegistroElevacion()
        {
            try
            {
                _escritorElevacion.Cerrar();
                _registroElevacion.Reiniciar();
                if (_escritorElevacion.UltimoError != null) LogElevacionConFreno("al cerrar: " + _escritorElevacion.UltimoError);
            }
            catch (Exception ex)
            {
                LogElevacionConFreno("al cerrar: " + ex.Message);
            }
        }

        private void LogElevacionConFreno(string msg)
        {
            if ((DateTime.UtcNow - _ultimoLogElevacionUtc).TotalSeconds < 60) return;
            _ultimoLogElevacionUtc = DateTime.UtcNow;
            try { Log.EventWriter("Registro de alturas: " + msg); } catch { }
        }
    }
}
