// ============================================================================
// GuidanceEngineHost.Deriva.cs — corrimiento de deriva GPS ("Corregir
// posición") y punto de referencia contra la deriva (idea del T-Wave de Sensor).
//
// Un solo mecanismo: SharedFieldProperties.DriftCompensation (m, norte/este),
// que se SUMA a la posición del GPS al entrar al plano local
// (LocalPlane.ConvertWgs84ToFixGeoCoord). Lo escriben:
//   · el panel "Corregir posición" a mano: shift_north_<cm> / shift_east_<cm>
//     (valor ABSOLUTO, como siempre mandó el panel) y shift_zero;
//   · el punto de referencia: ref_marcar guarda el pivote en
//     <lote>/ReferenciaDeriva.txt; ref_volver (tractor sobre la bandera) suma
//     referencia − pivote a la deriva vigente (ReferenciaDeriva.CalcularVolver).
//
// La deriva NO se guarda con el lote ni se restaura al abrirlo: aplicarle a la
// máquina el corrimiento de ayer sin que nadie lo pida sería mover el guiado a
// ciegas. Lo que persiste es el PUNTO (la bandera); el operario vuelve a él y
// decide. Al cerrar el lote la deriva vuelve a 0, salvo "Mantener corrimiento"
// (offsets_on), igual que el JobClose de FormGPS con isKeepOffsetsOn.
//
// Cambiar la deriva es un SALTO del fix. Para que no lo lea como movimiento
// (rumbo falso por fix-a-fix, una tira pintada atravesada, reversa), se
// traslada con él todo el historial de posiciones: es la misma máquina, en el
// mismo lugar, vista desde un plano corrido.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        /// <summary>Mismo límite que el panel (±9999 cm).</summary>
        private const int LimiteCorrimientoCm = 9999;

        /// <summary>Velocidad a partir de la cual "Volver a la referencia" no se
        /// aplica: el tractor tiene que estar parado SOBRE la bandera.</summary>
        private const double MaxVelocidadReferenciaKmh = 1.0;

        /// <summary>"Mantener corrimiento": la deriva sobrevive al cerrar el lote.</summary>
        public bool mantenerCorrimiento;

        /// <summary>Punto de referencia del lote abierto (null = no hay).</summary>
        public PuntoReferencia referenciaDeriva;

        /// <summary>Último resultado de ref_marcar/ref_volver, para que la cabina
        /// diga qué pasó (ExecuteCommand solo devuelve true/false).</summary>
        public string referenciaMensaje = "";
        public bool referenciaMensajeOk;

        /// <summary>Deriva vigente en m (norte/este).</summary>
        public GeoDelta DerivaActual => AppModelField.SharedFieldProperties.DriftCompensation;

        /// <summary>Atiende los comandos de deriva. false = no es de este grupo.</summary>
        private bool TryComandoDeriva(string cmd, out bool ok)
        {
            ok = false;
            if (cmd.StartsWith("shift_north_") || cmd.StartsWith("shift_east_"))
            {
                bool norte = cmd.StartsWith("shift_north_");
                string num = cmd.Substring(norte ? "shift_north_".Length : "shift_east_".Length);
                if (!int.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int cm)
                    || Math.Abs(cm) > LimiteCorrimientoCm)
                    return true;
                GeoDelta d = DerivaActual;
                ok = AplicarDerivaManual(norte ? new GeoDelta(cm / 100.0, d.EastingDelta)
                                               : new GeoDelta(d.NorthingDelta, cm / 100.0));
                return true;
            }
            switch (cmd)
            {
                case "shift_zero":
                    ok = AplicarDerivaManual(new GeoDelta(0, 0));
                    return true;
                case "offsets_on":
                    mantenerCorrimiento = true;
                    ok = true; return true;
                case "offsets_off":
                    mantenerCorrimiento = false;
                    ok = true; return true;
                case "ref_marcar":
                    ok = MarcarReferencia(); return true;
                case "ref_volver":
                    ok = VolverAReferencia(); return true;
            }
            return false;
        }

        /// <summary>
        /// Corrimiento a mano (±cm, "Poner en cero"). Con el piloto enganchado
        /// solo deja saltos chicos (CorrimientoManual): un salto grande corre la
        /// línea bajo el piloto y es un volantazo. El motivo queda en
        /// referenciaMensaje para que la pantalla lo diga.
        /// </summary>
        private bool AplicarDerivaManual(GeoDelta nueva)
        {
            GeoDelta vieja = DerivaActual;
            string motivo = CorrimientoManual.PorQueNoSePuede(
                nueva.NorthingDelta - vieja.NorthingDelta,
                nueva.EastingDelta - vieja.EastingDelta,
                isBtnAutoSteerOn);
            if (motivo != null) return ResultadoReferencia(false, motivo);

            AplicarDeriva(nueva);
            return true;
        }

        /// <summary>Hay fix vivo (mismo criterio de 3 s que el state provider).</summary>
        private bool HayGpsVivo()
        {
            return isGPSPositionInitialized
                && lastFixUtc != default(DateTime)
                && (DateTime.UtcNow - lastFixUtc).TotalSeconds <= 3;
        }

        private bool ResultadoReferencia(bool ok, string mensaje)
        {
            referenciaMensajeOk = ok;
            referenciaMensaje = mensaje ?? "";
            Log.EventWriter("GuidanceEngine: referencia de deriva — " + referenciaMensaje);
            return ok;
        }

        public bool MarcarReferencia()
        {
            if (!IsJobStarted)
                return ResultadoReferencia(false, "Abrí un lote primero. La referencia se guarda adentro del lote.");
            if (!HayGpsVivo())
                return ResultadoReferencia(false, "Sin señal de GPS. La referencia se mide con la posición del tractor.");

            var pivote = new GeoCoord(pivotAxlePos.northing, pivotAxlePos.easting);
            Wgs84 ll;
            try { ll = AppModelField.LocalPlane.ConvertGeoCoordToWgs84(pivote); }
            catch { ll = new Wgs84(0, 0); }

            var p = new PuntoReferencia(pivote.Northing, pivote.Easting, ll.Latitude, ll.Longitude, DateTime.UtcNow);
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                File.WriteAllText(Path.Combine(dir, ReferenciaDeriva.NombreArchivo), ReferenciaDeriva.Serializar(p));
            }
            catch (Exception ex)
            {
                return ResultadoReferencia(false, "No se pudo guardar la referencia en el lote: " + ex.Message);
            }
            referenciaDeriva = p;
            return ResultadoReferencia(true,
                "Referencia marcada. Dejá la bandera clavada: al volver, poné el tractor sobre ella y tocá \"Volver a la referencia\".");
        }

        public bool VolverAReferencia()
        {
            if (!IsJobStarted)
                return ResultadoReferencia(false, "Abrí un lote primero. La referencia se guarda adentro del lote.");
            if (referenciaDeriva == null)
                return ResultadoReferencia(false, "Este lote no tiene referencia marcada. Marcala antes de cortar.");
            if (!HayGpsVivo())
                return ResultadoReferencia(false, "Sin señal de GPS. La referencia se mide con la posición del tractor.");
            if (isBtnAutoSteerOn)
                return ResultadoReferencia(false, "Desenganchá el piloto antes de volver a la referencia: mueve todo el mapa.");
            if (Math.Abs(avgSpeed) > MaxVelocidadReferenciaKmh)
                return ResultadoReferencia(false, "Pará el tractor sobre la bandera antes de volver a la referencia.");
            if (HaySeccionPintando())
                return ResultadoReferencia(false, "Apagá las secciones antes de volver a la referencia: mueve todo el mapa.");

            var actual = new GeoCoord(pivotAxlePos.northing, pivotAxlePos.easting);
            var r = ReferenciaDeriva.CalcularVolver(referenciaDeriva.Coord, actual, DerivaActual);
            if (!r.Aplicable) return ResultadoReferencia(false, r.Motivo);

            AplicarDeriva(r.NuevaDeriva);
            return ResultadoReferencia(true,
                "Posición corregida " + ReferenciaDeriva.DescribirCorreccion(r.Correccion) + ".");
        }

        private bool HaySeccionPintando()
        {
            for (int j = 0; j < TriStripField.Count; j++)
                if (TriStripField[j] != null && TriStripField[j].isDrawing) return true;
            return false;
        }

        /// <summary>
        /// Escribe la deriva y traslada TODO el historial de posiciones por la
        /// diferencia, para que el salto no se lea como movimiento.
        /// </summary>
        public void AplicarDeriva(GeoDelta nueva)
        {
            GeoDelta vieja = DerivaActual;
            AppModelField.SharedFieldProperties.DriftCompensation = nueva;
            double dn = nueva.NorthingDelta - vieja.NorthingDelta;
            double de = nueva.EastingDelta - vieja.EastingDelta;
            if (dn == 0 && de == 0) return;

            Trasladar(ref Pn.fix, de, dn);
            Trasladar(ref pivotAxlePos, de, dn);
            Trasladar(ref steerAxlePos, de, dn);
            Trasladar(ref toolPivotPos, de, dn);
            Trasladar(ref toolPos, de, dn);
            Trasladar(ref tankPos, de, dn);
            Trasladar(ref hitchPos, de, dn);
            Trasladar(ref prevFix, de, dn);
            Trasladar(ref prevDistFix, de, dn);
            Trasladar(ref lastReverseFix, de, dn);
            Trasladar(ref lastGps, de, dn);
            Trasladar(ref guidanceLookPos, de, dn);
            Trasladar(ref prevBoundaryPos, de, dn);
            Trasladar(ref prevContourPos, de, dn);
            Trasladar(ref prevSectionPos, de, dn);
            Trasladar(ref prevGridPos, de, dn);
            for (int i = 0; i < stepFixPts.Length; i++)
            {
                if (stepFixPts[i].isSet == 0) continue;
                stepFixPts[i].easting += de;
                stepFixPts[i].northing += dn;
            }
            Log.EventWriter(string.Format(CultureInfo.InvariantCulture,
                "GuidanceEngine: deriva GPS = N {0:0.000} m / E {1:0.000} m (salto N {2:0.000} / E {3:0.000})",
                nueva.NorthingDelta, nueva.EastingDelta, dn, de));
        }

        private static void Trasladar(ref vec2 v, double de, double dn) { v.easting += de; v.northing += dn; }
        private static void Trasladar(ref vec3 v, double de, double dn) { v.easting += de; v.northing += dn; }

        /// <summary>Al abrir el lote: trae su referencia (si tiene).</summary>
        private void CargarReferenciaDeriva(string dir)
        {
            referenciaDeriva = null;
            referenciaMensaje = "";
            referenciaMensajeOk = false;
            try
            {
                string path = Path.Combine(dir, ReferenciaDeriva.NombreArchivo);
                if (!File.Exists(path)) return;
                if (ReferenciaDeriva.TryParsear(File.ReadAllText(path), out var p)) referenciaDeriva = p;
                else Log.EventWriter("GuidanceEngine: " + ReferenciaDeriva.NombreArchivo + " ilegible, se ignora");
            }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: " + ReferenciaDeriva.NombreArchivo + ": " + ex.Message); }
        }

        /// <summary>Al cerrar el lote: suelta la referencia y, salvo "Mantener
        /// corrimiento", vuelve la deriva a 0.</summary>
        private void SoltarDerivaDelLote()
        {
            referenciaDeriva = null;
            referenciaMensaje = "";
            referenciaMensajeOk = false;
            if (!mantenerCorrimiento) AplicarDeriva(new GeoDelta(0, 0));
        }
    }
}
