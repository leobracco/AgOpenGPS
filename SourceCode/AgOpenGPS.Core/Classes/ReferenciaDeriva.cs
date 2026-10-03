// ============================================================================
// ReferenciaDeriva.cs — "Punto de referencia contra la deriva" (idea del
// T-Wave de Sensor).
//
// Con GPS sin corrección (señal libre, el modo normal de Spark) la posición
// deriva decenas de cm a metros entre una sesión y otra: después del almuerzo
// el lote, lo pintado y las guías ya no caen sobre el terreno. El flujo:
//   1. Antes de cortar, el operario clava una bandera física y toca "Marcar
//      referencia": se guarda el PIVOTE actual (en el plano del lote, o sea
//      con la deriva que estuviera aplicada) como referencia del lote.
//   2. Al volver pone el tractor sobre la bandera y toca "Volver a la
//      referencia": la diferencia entre la referencia y donde PilotX cree que
//      está ahora es la deriva acumulada, y se SUMA a DriftCompensation.
//
// Convención de DriftCompensation (la de FormShiftPos y del panel "Corregir
// posición"): el corrimiento se SUMA a la posición del GPS.
//     posición_mapa = gps + deriva
// Por eso la corrección es referencia − actual, y la deriva nueva es la vieja
// más esa corrección. Al ser una traslación pura, da lo mismo medir con el
// pivote o con la antena mientras sea siempre el mismo punto.
//
// Todo acá es PURO (sin reloj, sin disco, sin estado): la cuenta que mueve el
// mapa entero se fija con tests (ReferenciaDerivaTests).
// ============================================================================

using System;
using System.Globalization;
using System.Text;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    /// <summary>Punto de referencia guardado con el lote.</summary>
    public sealed class PuntoReferencia
    {
        public PuntoReferencia(double northing, double easting, double latitude, double longitude, DateTime marcadoUtc)
        {
            Northing = northing;
            Easting = easting;
            Latitude = latitude;
            Longitude = longitude;
            MarcadoUtc = marcadoUtc;
        }

        /// <summary>Pivote en el plano local del lote (m), con la deriva que
        /// estaba aplicada al marcar — es el plano en el que está el mapa.</summary>
        public double Northing { get; }
        public double Easting { get; }

        /// <summary>Lat/lon del mismo punto. Solo informativo (para encontrar la
        /// bandera en otro mapa); la cuenta usa northing/easting.</summary>
        public double Latitude { get; }
        public double Longitude { get; }

        public DateTime MarcadoUtc { get; }

        public GeoCoord Coord => new GeoCoord(Northing, Easting);
    }

    /// <summary>Resultado de "Volver a la referencia".</summary>
    public sealed class ResultadoVolverReferencia
    {
        /// <summary>true = se puede aplicar NuevaDeriva.</summary>
        public bool Aplicable { get; internal set; }

        /// <summary>Por qué no se aplica (null si Aplicable).</summary>
        public string Motivo { get; internal set; }

        /// <summary>Cuánto se mueve la posición del tractor: referencia − actual.</summary>
        public GeoDelta Correccion { get; internal set; }

        /// <summary>DriftCompensation a escribir (la previa si no es aplicable).</summary>
        public GeoDelta NuevaDeriva { get; internal set; }
    }

    public static class ReferenciaDeriva
    {
        /// <summary>Nombre del archivo en la carpeta del lote.</summary>
        public const string NombreArchivo = "ReferenciaDeriva.txt";

        /// <summary>Más que esto no es deriva de un GPS libre: es otra bandera,
        /// otro lote o un GPS roto. Se avisa y no se toca nada.</summary>
        public const double MaxCorreccionM = 50.0;

        private static readonly NumberFormatInfo Es = new NumberFormatInfo
        {
            // Coma decimal y punto de miles SIN depender de la cultura "es-AR":
            // PilotX.Desktop corre con InvariantGlobalization (crear es-AR ahí
            // tira CultureNotFoundException y voltea la pantalla). Mismo patrón
            // que TareaFormato.
            NumberDecimalSeparator = ",",
            NumberGroupSeparator = ".",
            NumberGroupSizes = new[] { 3 },
        };
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <param name="referencia">Punto marcado (plano del lote).</param>
        /// <param name="posicionActual">Pivote ahora, tal como lo ve el mapa (ya
        /// con <paramref name="derivaActual"/> aplicada).</param>
        /// <param name="derivaActual">DriftCompensation vigente.</param>
        public static ResultadoVolverReferencia CalcularVolver(
            GeoCoord referencia, GeoCoord posicionActual, GeoDelta derivaActual,
            double maxCorreccionM = MaxCorreccionM)
        {
            var r = new ResultadoVolverReferencia { NuevaDeriva = derivaActual, Correccion = new GeoDelta(0, 0) };

            if (!Finito(referencia.Northing) || !Finito(referencia.Easting) ||
                !Finito(posicionActual.Northing) || !Finito(posicionActual.Easting) ||
                !Finito(derivaActual.NorthingDelta) || !Finito(derivaActual.EastingDelta))
            {
                r.Aplicable = false;
                r.Motivo = "La posición o la referencia no son válidas. No se corrigió nada.";
                return r;
            }

            GeoDelta correccion = referencia - posicionActual;
            double dist = correccion.Length;
            if (dist > maxCorreccionM)
            {
                r.Aplicable = false;
                r.Motivo = "La referencia está a " + dist.ToString("0", Es) + " m del tractor: " +
                           "más de " + maxCorreccionM.ToString("0", Es) + " m no es deriva del GPS. " +
                           "¿Es la bandera correcta? No se corrigió nada.";
                return r;
            }

            r.Aplicable = true;
            r.Correccion = correccion;
            r.NuevaDeriva = new GeoDelta(
                derivaActual.NorthingDelta + correccion.NorthingDelta,
                derivaActual.EastingDelta + correccion.EastingDelta);
            return r;
        }

        /// <summary>"30 cm al sur y 12 cm al este" / "1,25 m al norte" /
        /// "menos de 1 cm". Es cuánto se MOVIÓ la posición del tractor.</summary>
        public static string DescribirCorreccion(GeoDelta d)
        {
            string norte = Eje(d.NorthingDelta, "al norte", "al sur");
            string este = Eje(d.EastingDelta, "al este", "al oeste");
            if (norte == null && este == null) return "menos de 1 cm";
            if (norte == null) return este;
            if (este == null) return norte;
            return norte + " y " + este;
        }

        private static string Eje(double m, string positivo, string negativo)
        {
            double a = Math.Abs(m);
            if (!Finito(a) || a < 0.005) return null;   // redondea a 0 cm
            string dir = m > 0 ? positivo : negativo;
            if (a >= 1.0) return a.ToString("0.00", Es) + " m " + dir;
            return Math.Round(a * 100.0, MidpointRounding.AwayFromZero).ToString("0", Es) + " cm " + dir;
        }

        // ── Archivo del lote ────────────────────────────────────────────────
        //
        // key=value, una por línea, siempre con punto decimal. Texto plano a
        // propósito: si algún día hay que mirarlo en una pantalla, se lee.

        public static string Serializar(PuntoReferencia p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            var sb = new StringBuilder();
            sb.Append("$ReferenciaDeriva\n");
            sb.Append("northing=").Append(p.Northing.ToString("R", Inv)).Append('\n');
            sb.Append("easting=").Append(p.Easting.ToString("R", Inv)).Append('\n');
            sb.Append("latitude=").Append(p.Latitude.ToString("R", Inv)).Append('\n');
            sb.Append("longitude=").Append(p.Longitude.ToString("R", Inv)).Append('\n');
            sb.Append("marcado_utc=").Append(p.MarcadoUtc.ToUniversalTime().ToString("o", Inv)).Append('\n');
            return sb.ToString();
        }

        /// <summary>false (y p = null) si falta northing/easting o no se
        /// pueden leer. Lat/lon y fecha son opcionales.</summary>
        public static bool TryParsear(string texto, out PuntoReferencia p)
        {
            p = null;
            if (string.IsNullOrWhiteSpace(texto)) return false;

            double? n = null, e = null;
            double lat = 0, lon = 0;
            DateTime fecha = default(DateTime);

            foreach (string linea in texto.Split('\n'))
            {
                string l = linea.Trim();
                int i = l.IndexOf('=');
                if (i <= 0) continue;
                string k = l.Substring(0, i).Trim().ToLowerInvariant();
                string v = l.Substring(i + 1).Trim();

                switch (k)
                {
                    case "northing":
                        if (!double.TryParse(v, NumberStyles.Float, Inv, out double nn) || !Finito(nn)) return false;
                        n = nn; break;
                    case "easting":
                        if (!double.TryParse(v, NumberStyles.Float, Inv, out double ee) || !Finito(ee)) return false;
                        e = ee; break;
                    case "latitude":
                        double.TryParse(v, NumberStyles.Float, Inv, out lat); break;
                    case "longitude":
                        double.TryParse(v, NumberStyles.Float, Inv, out lon); break;
                    case "marcado_utc":
                        if (DateTime.TryParse(v, Inv, DateTimeStyles.RoundtripKind, out DateTime f))
                            fecha = f.ToUniversalTime();
                        break;
                }
            }

            if (n == null || e == null) return false;
            p = new PuntoReferencia(n.Value, e.Value, lat, lon, fecha);
            return true;
        }

        private static bool Finito(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
