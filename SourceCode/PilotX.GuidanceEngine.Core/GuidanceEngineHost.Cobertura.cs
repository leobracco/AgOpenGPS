// ============================================================================
// GuidanceEngineHost.Cobertura.cs — persistir el area trabajada.
//
// El motor headless NO guardaba ni leia cobertura: cero referencias a
// Sections.txt en todo PilotX.GuidanceEngine. Se comprobo en el lote real —
// "Test 11" estuvo trabajando dos dias y Sections.txt no existia. La cobertura
// vivia solo en RAM.
//
// Lo que eso costaba:
//   · cerrar el lote o reiniciar el motor borraba TODA el area trabajada
//   · reabrir un lote mostraba 0 ha aunque estuviera sembrado
//   · si PilotX se cerraba a mitad de jornada, el operario perdia el mapa de
//     lo hecho
//   · y lo peor: el anti-solape no servia entre sesiones. Sin historia de
//     cobertura al reabrir, sembraria de nuevo todo lo ya sembrado, que es
//     exactamente el error que existe para evitar.
//
// El WinForms si lo hacia (SaveOpen.Designer.cs). Esto porta el mismo flujo
// contra los mismos streamers portables de AgOpenGPS.IO, sin UI.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.IO;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        /// <summary>Parches leidos del disco al abrir el lote. Diagnostico.</summary>
        public int ParchesCargados { get; private set; }

        // Cada cuantos fixes se baja a disco lo pendiente. A 10 Hz son ~30 s.
        // No se guarda en cada fix: seria escribir el archivo 10 veces por
        // segundo para agregar unos pocos triangulos. Tampoco se espera al
        // cierre: si se corta la luz en la cabina, lo que no bajo se pierde.
        private const int FixesEntreGuardadas = 300;
        private int _fixesDesdeGuardada;

        /// <summary>
        /// Lee Sections.txt y reconstruye los parches. Mismo criterio que
        /// FormGPS.FileOpenField: todo entra en la tira 0 y se recalcula el area
        /// trabajada desde los triangulos.
        /// </summary>
        private void CargarCobertura(string dir)
        {
            ParchesCargados = 0;
            try
            {
                var parches = SectionsFiles.Load(dir);
                if (parches == null || parches.Count == 0) return;
                if (TriStripField.Count == 0 || TriStripField[0] == null) return;

                var tira = TriStripField[0];
                tira.patchList = new List<List<vec3>>();
                Fd.workedAreaTotal = 0;

                foreach (var parche in parches)
                {
                    if (parche == null || parche.Count < 4) continue;
                    tira.triangleList = new List<vec3>(parche);
                    tira.patchList.Add(tira.triangleList);

                    // parche[0] es el header de color, la geometria arranca en [1].
                    int verts = parche.Count - 2;
                    for (int j = 1; j < verts; j++)
                    {
                        double t = parche[j].easting * (parche[j + 1].northing - parche[j + 2].northing)
                                 + parche[j + 1].easting * (parche[j + 2].northing - parche[j].northing)
                                 + parche[j + 2].easting * (parche[j].northing - parche[j + 1].northing);
                        Fd.workedAreaTotal += Math.Abs(t * 0.5);
                    }
                    ParchesCargados++;
                }

                // Los parches que se acaban de leer YA estan en disco: si
                // quedaran en la cola de guardado se escribirian de nuevo y el
                // area trabajada se duplicaria en cada apertura.
                patchSaveList?.Clear();
                _fixesDesdeGuardada = 0;

                Log.EventWriter($"GuidanceEngine: cobertura cargada ({ParchesCargados} parches, {Fd.workedAreaTotal:F0} m2)");
            }
            catch (Exception ex)
            {
                // Un Sections.txt roto no puede impedir abrir el lote: se pierde
                // la pintura vieja, no la jornada.
                Log.EventWriter("GuidanceEngine: Sections.txt no se pudo leer: " + ex.Message);
            }
        }

        /// <summary>
        /// Baja a disco los parches cerrados pendientes. Se llama cada tanto
        /// desde el tick y al cerrar el lote. Idempotente: si no hay nada
        /// pendiente no toca el archivo.
        /// </summary>
        public void GuardarCoberturaPendiente()
        {
            try
            {
                if (patchSaveList == null || patchSaveList.Count == 0) return;
                if (string.IsNullOrEmpty(currentFieldDirectory)) return;

                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                if (!Directory.Exists(dir)) return;

                int n = patchSaveList.Count;
                SectionsFiles.Append(dir, patchSaveList);
                patchSaveList.Clear();
                Log.EventWriter($"GuidanceEngine: cobertura guardada ({n} parches)");
            }
            catch (Exception ex)
            {
                // No se limpia patchSaveList si fallo: se reintenta en la
                // proxima vuelta en vez de perder el trabajo.
                Log.EventWriter("GuidanceEngine: no se pudo guardar cobertura: " + ex.Message);
            }
        }

        /// <summary>
        /// Contador del guardado periodico. Se llama una vez por fix con lote
        /// abierto.
        /// </summary>
        private void TickGuardadoCobertura()
        {
            if (!IsJobStarted) return;
            if (++_fixesDesdeGuardada < FixesEntreGuardadas) return;
            _fixesDesdeGuardada = 0;
            GuardarCoberturaPendiente();
        }

        // ====================================================================
        // Resto de los datos del lote: cabecera, tram y camino grabado.
        //
        // Misma clase de hueco que la cobertura: el WinForms los guarda y lee,
        // el motor headless no los tocaba. Al reabrir el lote aparecian vacios
        // y el operario tenia que rehacerlos. La cabecera es la peor de las
        // tres: sin ella el corte automatico en cabecera deja de funcionar.
        //
        // A diferencia de la cobertura, estos NO van en el guardado periodico:
        // se configuran una vez y no crecen mientras se trabaja, asi que
        // reescribir sus archivos cada 30 s solo desgastaria la memoria de la
        // pantalla. Se guardan al cerrar el lote. La contra: un corte de luz
        // pierde lo que se haya configurado en esa sesion.
        //
        // Quedan afuera todavia: Flags.txt (el motor no tiene lista de
        // banderas), Headlines.txt (sin contenedor de CHeadPath) y Contour.txt
        // (falta la lista de parches pendientes que usa el WinForms).
        // ====================================================================

        // ---- banderas ------------------------------------------------------
        //
        // Son dato del OPERARIO: marca una piedra, un pozo, un alambrado caído.
        // Perderlas no es perder una config, es perder lo que vio en el lote.
        // Por eso se guardan en el acto ante cada cambio y no cada 30 s como la
        // cobertura: son pocas y cada una costó una vuelta.

        /// <summary>Banderas del lote abierto.</summary>
        public readonly List<CFlag> FlagPts = new List<CFlag>();

        /// <summary>Bandera seleccionada (1-based, 0 = ninguna).</summary>
        public int FlagPicked { get; set; }

        public void GuardarBanderas()
        {
            if (string.IsNullOrEmpty(currentFieldDirectory)) return;
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                if (Directory.Exists(dir)) FlagsFiles.Save(dir, FlagPts);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: no se pudo guardar Flags.txt: " + ex.Message);
            }
        }

        // ---- linderos ------------------------------------------------------
        //
        // El motor CARGABA linderos (BoundaryFiles.Load en Job.cs) pero no
        // existia una sola llamada a BoundaryFiles.Save en todo el engine: se
        // podia grabar un contorno manejando la vuelta entera del lote y al
        // cerrar no quedaba nada. Misma clase de hueco que la cobertura.
        //
        // Se guarda EN EL ACTO ante cada cambio, no cada 30 s: recorrer el
        // perimetro cuesta una vuelta completa al lote. Que un corte de luz se
        // lleve eso no va.

        // ---- cabecera ------------------------------------------------------
        //
        // El motor leia Headland.txt (HeadlandFiles.AttachLoad) pero no tenia
        // como construir ni guardar una cabecera: el editor vivia dentro de
        // FormGPS. Con HeadlandEditor extraido a Core, el motor necesita las dos
        // piezas que el editor le pide: un CHeadLine de trabajo y el guardado.

        /// <summary>Lista de trabajo del editor de cabecera (desList/idx).
        /// Equivale al `hdl` de FormGPS.</summary>
        public readonly CHeadLine Hdl = new CHeadLine();

        /// <summary>
        /// Baja la cabecera a Headland.txt. Se llama en el acto ante cada cambio
        /// del editor: construir la cabecera es trabajo del operario sobre el
        /// mapa y no se pierde por cerrar mal.
        /// </summary>
        public void GuardarCabecera()
        {
            if (string.IsNullOrEmpty(currentFieldDirectory)) return;
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                if (Directory.Exists(dir)) HeadlandFiles.Save(dir, Bnd.bndList);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: no se pudo guardar Headland.txt: " + ex.Message);
            }
        }

        /// <summary>
        /// Baja las líneas de cabecera a Headlines.txt. Son las líneas A/B que
        /// el operario marcó sobre el borde; la cabecera se arma con los cruces
        /// entre ellas, así que sin esto se pierde el trabajo de trazarlas.
        /// </summary>
        public void GuardarLineasDeCabecera()
        {
            if (string.IsNullOrEmpty(currentFieldDirectory)) return;
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                if (Directory.Exists(dir)) HeadlinesFiles.Save(dir, Hdl.tracksArr);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: no se pudo guardar Headlines.txt: " + ex.Message);
            }
        }

        /// <summary>Lee Headlines.txt. Un archivo faltante no es error: el lote
        /// puede no tener líneas trazadas todavía.</summary>
        public void CargarLineasDeCabecera()
        {
            Hdl.tracksArr.Clear();
            if (string.IsNullOrEmpty(currentFieldDirectory)) return;
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                var lineas = HeadlinesFiles.Load(dir);
                if (lineas != null) Hdl.tracksArr.AddRange(lineas);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: Headlines.txt: " + ex.Message);
            }
        }

        /// <summary>
        /// Baja las tramlines a Tram.txt. El editor las guarda en el acto: se
        /// arman una vez por lote y rehacerlas cuesta volver a marcar pasadas.
        /// </summary>
        public void GuardarTram()
        {
            if (string.IsNullOrEmpty(currentFieldDirectory)) return;
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                if (Directory.Exists(dir))
                    TramFiles.Save(dir, Tram.tramBndOuterArr, Tram.tramBndInnerArr, Tram.tramList);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: no se pudo guardar Tram.txt: " + ex.Message);
            }
        }

        /// <summary>
        /// Diagonal aproximada del lote. Las tramlines se dibujan más largas que
        /// el lote y después se recortan contra el contorno, así que si esto sale
        /// corto las líneas no llegan al lindero.
        ///
        /// Se calcula del bounding box del contorno; sin contorno se devuelve un
        /// valor amplio en vez de cero, que dejaría las tramlines en nada.
        /// </summary>
        public double MaxDistanciaLote
        {
            get
            {
                try
                {
                    if (Bnd?.bndList == null || Bnd.bndList.Count == 0) return 2000.0;
                    var f = Bnd.bndList[0].fenceLine;
                    if (f == null || f.Count == 0) return 2000.0;

                    double minE = double.MaxValue, maxE = double.MinValue;
                    double minN = double.MaxValue, maxN = double.MinValue;
                    for (int i = 0; i < f.Count; i++)
                    {
                        if (f[i].easting < minE) minE = f[i].easting;
                        if (f[i].easting > maxE) maxE = f[i].easting;
                        if (f[i].northing < minN) minN = f[i].northing;
                        if (f[i].northing > maxN) maxN = f[i].northing;
                    }
                    double dx = maxE - minE, dy = maxN - minN;
                    double diag = Math.Sqrt(dx * dx + dy * dy);
                    return diag > 1 ? diag : 2000.0;
                }
                catch { return 2000.0; }
            }
        }

        public void GuardarLinderos()
        {
            if (string.IsNullOrEmpty(currentFieldDirectory)) return;
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                if (Directory.Exists(dir)) BoundaryFiles.Save(dir, Bnd.bndList);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: no se pudo guardar Boundary.txt: " + ex.Message);
            }
        }

        private void CargarRestoDelLote(string dir)
        {
            FlagPicked = 0;
            FlagPts.Clear();
            try
            {
                var banderas = FlagsFiles.Load(dir);
                if (banderas != null) FlagPts.AddRange(banderas);
            }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: Flags.txt: " + ex.Message); }

            // Cabecera: se engancha sobre los linderos ya cargados, asi que
            // tiene que ir DESPUES de BoundaryFiles.Load.
            try { HeadlandFiles.AttachLoad(dir, Bnd.bndList); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: Headland.txt: " + ex.Message); }

            // Lineas de cabecera (las A/B trazadas sobre el borde).
            CargarLineasDeCabecera();

            try
            {
                var t = TramFiles.Load(dir);
                if (t != null)
                {
                    Tram.tramBndOuterArr.Clear();
                    Tram.tramBndOuterArr.AddRange(t.Outer);
                    Tram.tramBndInnerArr.Clear();
                    Tram.tramBndInnerArr.AddRange(t.Inner);
                    Tram.tramList.Clear();
                    Tram.tramList.AddRange(t.Lines);
                }
            }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: Tram.txt: " + ex.Message); }

            try
            {
                var rec = RecPathFiles.Load(dir);
                RecPath.recList.Clear();
                if (rec != null) RecPath.recList.AddRange(rec);
            }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: RecPath.txt: " + ex.Message); }

            Log.EventWriter($"GuidanceEngine: lote cargado (cabecera en {Bnd.bndList.Count} linderos, " +
                            $"tram={Tram.tramList.Count}, recpath={RecPath.recList.Count}, banderas={FlagPts.Count})");
        }

        /// <summary>Baja cabecera, tram y camino grabado. Se llama al cerrar.</summary>
        public void GuardarRestoDelLote()
        {
            if (string.IsNullOrEmpty(currentFieldDirectory)) return;
            string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
            if (!Directory.Exists(dir)) return;

            try { HeadlandFiles.Save(dir, Bnd.bndList); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: no se pudo guardar Headland.txt: " + ex.Message); }

            try { TramFiles.Save(dir, Tram.tramBndOuterArr, Tram.tramBndInnerArr, Tram.tramList); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: no se pudo guardar Tram.txt: " + ex.Message); }

            try { RecPathFiles.Save(dir, RecPath.recList); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: no se pudo guardar RecPath.txt: " + ex.Message); }

            // Banderas y linderos ya se guardan en cada cambio, pero por si
            // acaso: que cerrar el lote nunca sea el momento en que se pierden.
            GuardarBanderas();
            GuardarLinderos();
        }
    }
}
