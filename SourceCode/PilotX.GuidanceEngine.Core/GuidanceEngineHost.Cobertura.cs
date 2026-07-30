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
    }
}
