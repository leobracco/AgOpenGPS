// ============================================================================
// EngineCoverageService.cs — adaptador ICoverageService sobre GuidanceEngineHost.
// Gemelo headless de FormGpsCoverageService (GPS/AgroParallel/Common): misma
// lectura plana de triStrip -> CoverageSnapshot, pero contra el host net9.0 en
// vez de FormGPS. El único cambio real es el nombre del campo (triStrip ->
// TriStripField) y la fuente del directorio de lote.
//
// El header patchList[k][0] (contador + color de la sección) se saltea igual
// que en FormGPS: incluirlo dibujaba la "diagonal" desde ~origen hasta el
// implemento. La geometría real arranca en [1].
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineCoverageService : ICoverageService
    {
        private readonly GuidanceEngineHost _host;

        public EngineCoverageService(GuidanceEngineHost host) { _host = host; }

        public CoverageSnapshot GetSnapshot()
        {
            var snap = new CoverageSnapshot
            {
                FieldDirectory = _host != null ? _host.currentFieldDirectory : null,
                Sections = new List<CoverageSection>()
            };
            if (_host == null || _host.TriStripField == null) return snap;

            long rev = 0;
            for (int j = 0; j < _host.TriStripField.Count; j++)
            {
                var strip = _host.TriStripField[j];
                var sec = new CoverageSection
                {
                    Index = j,
                    Enabled = strip != null && strip.isDrawing,
                    Strips = new List<CoverageStrip>()
                };
                if (strip != null && strip.patchList != null)
                {
                    for (int k = 0; k < strip.patchList.Count; k++)
                    {
                        var tri = strip.patchList[k];
                        rev += tri?.Count ?? 0;
                        // patchList[k][0] es HEADER (contador + color), no coordenada.
                        // Necesitamos header + >= 3 vértices reales para un triángulo.
                        // Los parches chicos van como strip VACÍA (no se saltean):
                        // así el índice de strip del cliente == índice de patchList
                        // y el cursor del protocolo incremental alinea 1:1.
                        if (tri == null || tri.Count < 4)
                        {
                            sec.Strips.Add(new CoverageStrip { Vertices = new List<CoverageVertex>() });
                            continue;
                        }
                        var cs = new CoverageStrip
                        {
                            Vertices = new List<CoverageVertex>(tri.Count - 1)
                        };
                        for (int v = 1; v < tri.Count; v++)
                        {
                            cs.Vertices.Add(new CoverageVertex(tri[v].easting, tri[v].northing));
                        }
                        sec.Strips.Add(cs);
                    }
                }
                snap.Sections.Add(sec);
            }
            snap.Revision = rev;
            return snap;
        }

        /// <summary>
        /// Snapshot INCREMENTAL para pintado fluido: el cliente manda hasta
        /// dónde tiene ("j:p:v" por sección) y acá se responde solo lo nuevo.
        /// La cobertura es append-only (los parches viejos están cerrados, solo
        /// crece el último), así que el diff es barato: continuar el último
        /// parche del cliente + parches nuevos. Ante cualquier mismatch
        /// (lote cerrado, reset, cursor corrupto) se cae al snapshot completo
        /// con Full=true y el cliente reemplaza todo.
        /// </summary>
        public CoverageSnapshot GetSnapshot(string cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return GetSnapshot();
            var cursores = ParseCursor(cursor);
            if (cursores == null) return GetSnapshot();
            if (_host == null || _host.TriStripField == null) return GetSnapshot();

            var snap = new CoverageSnapshot
            {
                FieldDirectory = _host.currentFieldDirectory,
                Full = false,
                Sections = new List<CoverageSection>()
            };

            long rev = 0;
            for (int j = 0; j < _host.TriStripField.Count; j++)
            {
                var strip = _host.TriStripField[j];
                var parches = strip?.patchList;
                int p = 0, v = 0;
                bool conocida = cursores.TryGetValue(j, out var cur);
                if (conocida) { p = cur.Item1; v = cur.Item2; }

                // Revision global igual que el snapshot completo (para el gate
                // del cliente): suma de counts de TODOS los parches.
                if (parches != null)
                    for (int k = 0; k < parches.Count; k++) rev += parches[k]?.Count ?? 0;

                if (parches == null || parches.Count == 0)
                {
                    // El cliente dice tener parches pero acá no hay nada:
                    // se limpió la cobertura → snapshot completo.
                    if (conocida && (p > 0 || v > 0)) return GetSnapshot();
                    continue;
                }

                // Mismatch: la lista se achicó o el parche del cliente tiene
                // menos vértices que los que él dice → reset del lado engine.
                if (p >= parches.Count) return GetSnapshot();
                var parcheP = parches[p];
                int vertsP = parcheP != null ? Math.Max(0, parcheP.Count - 1) : 0;
                if (v > vertsP) return GetSnapshot();

                bool hayNuevoEnP = vertsP > v;
                bool hayParchesNuevos = parches.Count - 1 > p;
                if (!hayNuevoEnP && !hayParchesNuevos) continue;   // sin novedades

                var sec = new CoverageSection
                {
                    Index = j,
                    Enabled = strip != null && strip.isDrawing,
                    PatchBase = p,
                    Strips = new List<CoverageStrip>()
                };

                // Continuación del parche p: SOLO los vértices nuevos (desde v).
                // Ojo el header en [0]: el vértice i del cliente es tri[i+1].
                var cont = new CoverageStrip { Vertices = new List<CoverageVertex>() };
                if (parcheP != null)
                    for (int i = v + 1; i < parcheP.Count; i++)
                        cont.Vertices.Add(new CoverageVertex(parcheP[i].easting, parcheP[i].northing));
                sec.Strips.Add(cont);

                // Parches completos posteriores.
                for (int k = p + 1; k < parches.Count; k++)
                {
                    var tri = parches[k];
                    if (tri == null || tri.Count < 4) { sec.Strips.Add(new CoverageStrip { Vertices = new List<CoverageVertex>() }); continue; }
                    var cs = new CoverageStrip { Vertices = new List<CoverageVertex>(tri.Count - 1) };
                    for (int i = 1; i < tri.Count; i++)
                        cs.Vertices.Add(new CoverageVertex(tri[i].easting, tri[i].northing));
                    sec.Strips.Add(cs);
                }

                snap.Sections.Add(sec);
            }

            // Secciones nuevas que el cliente no conoce ya quedaron cubiertas
            // arriba (cursor ausente → p=0,v=0 → viaja completa).
            snap.Revision = rev;
            return snap;
        }

        // "j:p:v;j:p:v" → dict sección → (parche, vértices). null = corrupto.
        private static Dictionary<int, Tuple<int, int>> ParseCursor(string cursor)
        {
            try
            {
                var d = new Dictionary<int, Tuple<int, int>>();
                foreach (var parte in cursor.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var c = parte.Split(':');
                    if (c.Length != 3) return null;
                    d[int.Parse(c[0])] = Tuple.Create(int.Parse(c[1]), int.Parse(c[2]));
                }
                return d;
            }
            catch { return null; }
        }

        public void Reset()
        {
            if (_host == null || _host.TriStripField == null) return;
            for (int j = 0; j < _host.TriStripField.Count; j++)
            {
                _host.TriStripField[j]?.patchList?.Clear();
            }
        }
    }
}
