// ============================================================================
// GuidanceEngineHost.TurnMarks.cs — "Marcar giro": el operario toca un botón
// en cada extremo del lote y esas dos líneas (perpendiculares a la guía)
// pasan a ser el límite del U-turn automático, sin recorrer el lindero.
// Spec: docs/superpowers/specs/2026-08-10-marcar-giro-design.md.
//
// La jugada central es NO tocar CYouTurn/CYouTurnUpdater: las marcas se
// materializan como un CBoundaryList VIRTUAL (isVirtualTurnBoundary) dentro
// de Bnd.bndList — sin lindero real es un rectángulo sintético que hace de
// lindero entero; con lindero real se recorta la turnLine existente con el
// semiplano de cada marca. Para el circuito del giro es un lindero más y los
// tres guards (bndList.Count, IsPointInsideTurnArea, ToggleYouTurn) pasan
// solos. Sin marcas, nada de esto corre: cero cambio de comportamiento.
//
// La geometría vive en CTurnMarks (Core, puro y testeado); acá va solo el
// estado, la persistencia (TurnMarks.txt en la carpeta del lote, así OrbitX
// lo sincroniza como cualquier archivo del lote) y la materialización.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.IO;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        /// <summary>Marcas de giro del lote abierto (0, 1 o 2), ordenadas por
        /// su posición sobre el eje de avance de la guía.</summary>
        public readonly List<TurnMark> TurnMarks = new List<TurnMark>();

        // Medio ancho del rectángulo virtual (m). 500 m para cada lado alcanza
        // para cualquier ancho de lote real; si la pasada se va más lejos que
        // eso de las marcas, el operario está en otro lote.
        private const double MitadAnchoVirtualM = 500.0;

        // Si la marca nueva cae a menos de esto de una existente (proyectadas
        // sobre el eje de la guía), se considera "el mismo extremo" y PISA en
        // vez de agregar. 30 m: más que cualquier corrección fina de posición,
        // menos que el largo de un lote de verdad.
        private const double MismoExtremoM = 30.0;

        // Tolerancia entre el rumbo guardado en la marca y el de la guía
        // activa. Pasado esto la guía es OTRA (no la misma recorrida al
        // revés) y las marcas no aplican: se ignoran sin borrarlas, por si el
        // operario vuelve a la guía original.
        private const double MaxDesvioRumboRad = 20.0 * Math.PI / 180.0;

        /// <summary>
        /// Rumbo de la guía activa (rad). Mismo acceso que BuildManualYouTurn
        /// (CYouTurn.cs): AB usa el heading fijo de la línea; curva usa el
        /// heading del punto de la curva más cercano al pivote, que el pipeline
        /// de guiado mantiene en manualUturnHeading en cada fix.
        /// </summary>
        private double RumboDeGuiaActiva()
        {
            return Trk.gArr[Trk.idx].mode == TrackMode.AB
                ? ABLineField.abHeading
                : CurveField.manualUturnHeading;
        }

        // Proyección de un punto sobre el eje de avance (dot con el versor del
        // rumbo). Es la coordenada "a lo largo de la pasada": con ella se
        // decide a qué extremo pertenece cada marca.
        private static double ProyeccionSobreEje(double easting, double northing, double rumbo)
        {
            return easting * Math.Sin(rumbo) + northing * Math.Cos(rumbo);
        }

        // Diferencia angular MOD π: una AB recorrida al revés (180° exactos)
        // es la MISMA guía, así que el desvío se mide contra la recta, no
        // contra el sentido. Devuelve un valor en [0, π/2].
        private static double DiferenciaDeRumboModPi(double a, double b)
        {
            double d = Math.Abs(a - b) % Math.PI;
            return Math.Min(d, Math.PI - d);
        }

        /// <summary>
        /// Marca un límite de giro en la posición actual del pivote, con el
        /// rumbo de la guía activa. Decide solo a qué extremo va: la primera
        /// marca entra directo, la segunda entra como el otro extremo si cae
        /// lejos de la primera, y re-marcar cerca de una existente la PISA
        /// (así "corregir" es simplemente volver a tocar el botón).
        /// </summary>
        public bool MarcarGiroAca()
        {
            if (!IsJobStarted)
            {
                Log.EventWriter("GuidanceEngine: marcar giro ignorado — no hay lote abierto");
                return false;
            }
            if (Trk.idx < 0 || Trk.idx >= Trk.gArr.Count)
            {
                Log.EventWriter("GuidanceEngine: marcar giro ignorado — no hay guia activa");
                return false;
            }

            double rumbo = RumboDeGuiaActiva();
            var marca = new TurnMark
            {
                easting = pivotAxlePos.easting,
                northing = pivotAxlePos.northing,
                heading = rumbo,
            };

            double proyNueva = ProyeccionSobreEje(marca.easting, marca.northing, rumbo);

            if (TurnMarks.Count == 0)
            {
                TurnMarks.Add(marca);
            }
            else if (TurnMarks.Count == 1)
            {
                double proyExistente = ProyeccionSobreEje(
                    TurnMarks[0].easting, TurnMarks[0].northing, rumbo);
                if (Math.Abs(proyNueva - proyExistente) < MismoExtremoM)
                    TurnMarks[0] = marca;          // mismo extremo: corrige
                else
                    TurnMarks.Add(marca);          // el otro extremo
            }
            else
            {
                // Ya hay dos: pisa la más cercana en proyección. No hay tercera
                // marca posible — un lote tiene dos cabeceras.
                double d0 = Math.Abs(proyNueva - ProyeccionSobreEje(
                    TurnMarks[0].easting, TurnMarks[0].northing, rumbo));
                double d1 = Math.Abs(proyNueva - ProyeccionSobreEje(
                    TurnMarks[1].easting, TurnMarks[1].northing, rumbo));
                TurnMarks[d0 <= d1 ? 0 : 1] = marca;
            }

            // Orden estable por proyección: TurnMarks[0] siempre es el extremo
            // "de atrás" sobre el eje. Todo lo que consume la lista (archivo,
            // snapshot, rectángulo virtual) hereda ese orden.
            TurnMarks.Sort((x, y) =>
                ProyeccionSobreEje(x.easting, x.northing, rumbo)
                    .CompareTo(ProyeccionSobreEje(y.easting, y.northing, rumbo)));

            GuardarMarcasGiro();
            MaterializarMarcasGiro();

            Log.EventWriter(string.Format(CultureInfo.InvariantCulture,
                "GuidanceEngine: marca de giro puesta en ({0:F1}, {1:F1}) rumbo {2:F1}° — total {3}",
                marca.easting, marca.northing, rumbo * 180.0 / Math.PI, TurnMarks.Count));
            return true;
        }

        /// <summary>
        /// Borra las dos marcas: limpia la lista, saca TurnMarks.txt del lote y
        /// re-materializa (lo que desarma el lindero virtual y, si hay lindero
        /// real, le reconstruye la turnLine limpia sin recortes).
        /// </summary>
        public void BorrarMarcasGiro()
        {
            TurnMarks.Clear();

            if (!string.IsNullOrEmpty(currentFieldDirectory))
            {
                try
                {
                    string path = Path.Combine(RegistrySettings.fieldsDirectory,
                        currentFieldDirectory, "TurnMarks.txt");
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    // Un archivo que no se pudo borrar no frena nada: en memoria
                    // ya no hay marcas y al próximo guardado se pisa.
                    Log.EventWriter("GuidanceEngine: no se pudo borrar TurnMarks.txt: " + ex.Message);
                }
            }

            MaterializarMarcasGiro();
            Log.EventWriter("GuidanceEngine: marcas de giro borradas");
        }

        // ---- persistencia ---------------------------------------------------
        //
        // TurnMarks.txt vive junto a Boundary.txt en la carpeta del lote:
        //   $TurnMarks
        //   easting,northing,heading      (una línea por marca, 0..2)
        // Mismo estilo que BoundaryFiles: InvariantCulture, 3 decimales para
        // E/N y 5 para el rumbo. Por estar en la carpeta, EnqueueAOGFiles lo
        // sube a OrbitX sin tocar nada del sync.

        /// <summary>Lee TurnMarks.txt del lote. Archivo ausente no es error:
        /// la mayoría de los lotes no tiene marcas.</summary>
        public void CargarMarcasGiro(string dir)
        {
            TurnMarks.Clear();
            try
            {
                string path = Path.Combine(dir, "TurnMarks.txt");
                if (!File.Exists(path)) return;

                foreach (string linea in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(linea)) continue;
                    if (linea.TrimStart().StartsWith("$")) continue;   // header

                    string[] partes = linea.Split(',');
                    if (partes.Length < 3) continue;
                    if (double.TryParse(partes[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double e) &&
                        double.TryParse(partes[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double n) &&
                        double.TryParse(partes[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                    {
                        TurnMarks.Add(new TurnMark { easting = e, northing = n, heading = h });
                        if (TurnMarks.Count == 2) break;   // más de dos es un archivo editado a mano
                    }
                }

                if (TurnMarks.Count > 0)
                    Log.EventWriter($"GuidanceEngine: {TurnMarks.Count} marca(s) de giro cargadas del lote");
            }
            catch (Exception ex)
            {
                // Un TurnMarks.txt roto no puede impedir abrir el lote: se
                // pierden las marcas, no la jornada. Se re-marcan en un toque.
                TurnMarks.Clear();
                Log.EventWriter("GuidanceEngine: TurnMarks.txt no se pudo leer: " + ex.Message);
            }
        }

        /// <summary>Baja las marcas a TurnMarks.txt. Se llama en el acto ante
        /// cada marca nueva: son dato del operario, no esperan al cierre.</summary>
        public void GuardarMarcasGiro()
        {
            if (string.IsNullOrEmpty(currentFieldDirectory)) return;
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                if (!Directory.Exists(dir)) return;

                using (var writer = new StreamWriter(Path.Combine(dir, "TurnMarks.txt"), false))
                {
                    writer.WriteLine("$TurnMarks");
                    foreach (var m in TurnMarks)
                    {
                        writer.WriteLine(
                            $"{FileIoUtils.FormatDouble(m.easting, 3)},{FileIoUtils.FormatDouble(m.northing, 3)},{FileIoUtils.FormatDouble(m.heading, 5)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: no se pudo guardar TurnMarks.txt: " + ex.Message);
            }
        }

        // ---- materialización ------------------------------------------------

        /// <summary>
        /// Traduce las marcas a lo que el circuito del U-turn ya sabe consumir.
        /// Idempotente: siempre arranca desarmando lo virtual anterior y
        /// reconstruyendo la turnLine real limpia, así se puede llamar tras
        /// marcar, borrar o abrir el lote sin acumular recortes.
        /// </summary>
        private void MaterializarMarcasGiro()
        {
            // 1) Desarmar lo materializado antes. Hacia atrás porque se saca
            // por índice; el virtual siempre se apendea al final, pero no
            // cuesta nada no depender de eso.
            for (int i = Bnd.bndList.Count - 1; i >= 0; i--)
            {
                if (Bnd.bndList[i].isVirtualTurnBoundary) Bnd.bndList.RemoveAt(i);
            }

            bool hayLinderoReal = Bnd.bndList.Count > 0;

            // 2) turnLine real limpia. Deshace cualquier recorte de una
            // materialización anterior; si después las marcas aplican, se
            // vuelve a recortar sobre esta base.
            if (hayLinderoReal) Bnd.BuildTurnLines();

            if (TurnMarks.Count == 0) return;

            // 3) Guard de rumbo: las marcas quedan atadas al rumbo con el que
            // se crearon. Si la guía activa hoy apunta a otro lado (>20°, mod
            // π para que la misma AB recorrida al revés siga valiendo), las
            // marcas se conservan pero NO se materializan — girar contra una
            // cabecera que cruza en diagonal la pasada nueva sería peor que no
            // girar.
            if (Trk.idx >= 0 && Trk.idx < Trk.gArr.Count)
            {
                double rumboGuia = RumboDeGuiaActiva();
                foreach (var m in TurnMarks)
                {
                    if (DiferenciaDeRumboModPi(m.heading, rumboGuia) > MaxDesvioRumboRad)
                    {
                        Log.EventWriter("GuidanceEngine: marcas de giro ignoradas: la guia activa cambio de rumbo");
                        return;
                    }
                }
            }

            if (!hayLinderoReal)
            {
                // Con una sola marca no hay rectángulo que armar: recién con
                // las dos hay lindero virtual. Mientras tanto la marca queda
                // guardada y dibujada, esperando a su par.
                if (TurnMarks.Count < 2) return;

                var ring = CTurnMarks.BuildVirtualFence(TurnMarks[0], TurnMarks[1], MitadAnchoVirtualM);
                if (ring.Count < 4) return;

                // Misma receta que grabar un contorno (EngineContornoService.
                // RecordSave): fenceLine → área → fix → lista → áreas GUI →
                // turnlines. El orden importa: FixFenceLine densifica usando el
                // área que calcula CalculateFenceArea, y BuildTurnLines offsetea
                // los puntos que FixFenceLine dejó.
                var virtualBnd = new CBoundaryList { isVirtualTurnBoundary = true };
                // Sin el punto de cierre duplicado: la convención de fenceLine
                // es anillo abierto (el cierre es implícito).
                int n = ring.Count - 1;
                for (int i = 0; i < n; i++) virtualBnd.fenceLine.Add(ring[i]);

                virtualBnd.CalculateFenceArea(0);
                virtualBnd.FixFenceLine(0);
                Bnd.bndList.Add(virtualBnd);
                Fd.UpdateFieldBoundaryGUIAreas();
                Bnd.BuildTurnLines();

                Log.EventWriter("GuidanceEngine: cabecera virtual armada con las 2 marcas de giro (sin lindero real)");
                return;
            }

            // 4) Con lindero real: la marca solo ACERCA la cabecera. Se recorta
            // la turnLine del lindero 0 con el semiplano de cada marca. El lado
            // a conservar es EL DE ENTRE LAS MARCAS (punto medio): la zona de
            // trabajo es lo que el operario acotó marcando los dos extremos.
            // Con el centroide del lote esto fallaba en el banco (2026-08-10):
            // dos marcas cercanas a un borde dejaban al tractor AFUERA del área
            // de giro recortada — IsPointInsideTurnArea daba -1 y el U-turn no
            // se armaba nunca ("llega a la línea y no dobla"). Con UNA sola
            // marca no hay "entre": ahí sí vale el centroide del fence real
            // (determinista, no depende de dónde esté parado el tractor).
            var fence = Bnd.bndList[0].fenceLine;
            if (fence == null || fence.Count < 3) return;

            vec2 centroLote;
            if (TurnMarks.Count >= 2)
            {
                centroLote = new vec2(
                    (TurnMarks[0].easting + TurnMarks[1].easting) / 2.0,
                    (TurnMarks[0].northing + TurnMarks[1].northing) / 2.0);
            }
            else
            {
                double ce = 0, cn = 0;
                for (int i = 0; i < fence.Count; i++) { ce += fence[i].easting; cn += fence[i].northing; }
                centroLote = new vec2(ce / fence.Count, cn / fence.Count);
            }

            int marcasAplicadas = 0;
            foreach (var m in TurnMarks)
            {
                // La LÍNEA de la marca es perpendicular al rumbo de la guía.
                var recortada = CTurnMarks.ClipRingWithHalfPlane(
                    Bnd.bndList[0].turnLine,
                    new vec3(m.easting, m.northing, 0),
                    m.heading + glm.PIBy2,
                    centroLote);

                if (recortada.Count >= 4)
                {
                    Bnd.bndList[0].turnLine.Clear();
                    Bnd.bndList[0].turnLine.AddRange(recortada);
                    Bnd.bndList[0].CalculateTurnHeadings();
                    marcasAplicadas++;
                }
                else
                {
                    // Marca afuera del lindero (o que vacía el polígono): se
                    // ignora esta marca, las demás y el lindero siguen valiendo.
                    Log.EventWriter(string.Format(CultureInfo.InvariantCulture,
                        "GuidanceEngine: marca de giro en ({0:F1}, {1:F1}) ignorada — el recorte vacia la linea de giro",
                        m.easting, m.northing));
                }
            }

            if (marcasAplicadas > 0)
                Log.EventWriter($"GuidanceEngine: linea de giro del lindero recortada con {marcasAplicadas} marca(s)");

            // Diagnóstico de cabina: si el tractor quedó fuera del área de giro
            // recién recortada, el updater del U-turn (IsPointInsideTurnArea)
            // no va a armar NADA y "llega a la línea y no dobla" sin ninguna
            // pista. Que quede dicho en el log.
            if (marcasAplicadas > 0 && Bnd.IsPointInsideTurnArea(pivotAxlePos) == -1)
                Log.EventWriter("GuidanceEngine: OJO — el tractor esta FUERA del area de giro que quedo tras el recorte; el U-turn no arma hasta entrar entre las marcas");

            // 5) CABECERA desde las marcas (pedido 2026-08-10): lo de AFUERA de
            // las marcas es cabecera. La hdLine base del lote (la que cargó
            // AttachLoad, o el fence si el lote no tiene cabecera armada) se
            // recorta con los mismos semiplanos — así el botón Cabecera ›
            // Activar de siempre corta secciones al pisar la marca. La base se
            // captura UNA vez por lote para que re-marcar no recorte sobre lo
            // ya recortado y Borrar la restaure intacta.
            RecortarCabeceraConMarcas(centroLote);
        }

        // Base prístina de la hdLine del lote (como la dejó AttachLoad / el
        // constructor de cabecera). null = todavía no capturada.
        private List<vec3> _hdLineBaseLote;

        private void ResetCabeceraBaseDeMarcas() => _hdLineBaseLote = null;

        private void RecortarCabeceraConMarcas(vec2 centroLote)
        {
            var bnd0 = Bnd.bndList[0];

            // Capturar la base la primera vez: hdLine real si existe, si no el
            // fence (cabecera "pegada al lindero", el recorte la separa solo en
            // los extremos marcados).
            if (_hdLineBaseLote == null)
            {
                var origen = (bnd0.hdLine != null && bnd0.hdLine.Count > 2)
                    ? bnd0.hdLine : bnd0.fenceLine;
                _hdLineBaseLote = new List<vec3>(origen);
            }

            // Siempre desde la base: idempotente al re-marcar, y con la lista
            // de marcas vacía esto RESTAURA la cabecera original (Borrar).
            var hd = new List<vec3>(_hdLineBaseLote);
            foreach (var m in TurnMarks)
            {
                var recortada = CTurnMarks.ClipRingWithHalfPlane(
                    hd, new vec3(m.easting, m.northing, 0),
                    m.heading + glm.PIBy2, centroLote);
                if (recortada.Count >= 4)
                {
                    // Sin el punto de cierre duplicado: hdLine es anillo abierto
                    // como fenceLine (IsPointInPolygon cierra solo).
                    recortada.RemoveAt(recortada.Count - 1);
                    hd = recortada;
                }
            }

            bnd0.hdLine.Clear();
            bnd0.hdLine.AddRange(hd);
            if (TurnMarks.Count > 0)
                Log.EventWriter($"GuidanceEngine: cabecera del lote recortada con {TurnMarks.Count} marca(s) — activala con Cabecera si queres corte de secciones ahi");
        }
    }
}
