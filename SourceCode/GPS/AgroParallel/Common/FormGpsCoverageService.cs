// ============================================================================
// FormGpsCoverageService.cs
// Adaptador ICoverageService → PilotX. Lee mf.triStrip[j].patchList y lo traduce
// a CoverageSnapshot. PilotX sigue siendo motor; el view consume vía /api/aog/coverage.
//
// Performance:
//   - Una jornada de 8h × 8km/h × 6m ancho = 384.000 m² ≈ 100k vértices.
//   - JSON serializado: ~3 MB. Para 1 Hz de polling es mucho.
//   - Por eso devolvemos Revision: el client guarda lo que ya recibió y
//     pide /coverage?since=<rev> en el futuro (no implementado en este pase —
//     hoy emite el snapshot completo). Revision viene de patchList.Count
//     sumado, baratísimo.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsCoverageService : ICoverageService
    {
        private readonly FormGPS _form;

        public FormGpsCoverageService(FormGPS form) { _form = form; }

        public CoverageSnapshot GetSnapshot()
        {
            var snap = new CoverageSnapshot
            {
                FieldDirectory = _form != null ? _form.currentFieldDirectory : null,
                Sections = new List<CoverageSection>()
            };
            if (_form == null || _form.triStrip == null) return snap;

            long rev = 0;
            for (int j = 0; j < _form.triStrip.Count; j++)
            {
                var strip = _form.triStrip[j];
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
                        // patchList[k][0] es un HEADER (contador + color de la sección),
                        // NO una coordenada. Incluirlo dibujaba un triángulo desde
                        // ~origen (el color leído como metros) hasta la herramienta —
                        // la "diagonal" que aparecía al togglear una sección. La
                        // geometría real del triangle-strip arranca en [1]. Necesitamos
                        // header + >= 3 vértices reales para un triángulo.
                        if (tri == null || tri.Count < 4) continue;
                        var cs = new CoverageStrip
                        {
                            Vertices = new List<CoverageVertex>(tri.Count - 1)
                        };
                        for (int v = 1; v < tri.Count; v++)
                        {
                            cs.Vertices.Add(new CoverageVertex(tri[v].easting, tri[v].northing));
                        }
                        sec.Strips.Add(cs);
                        rev += tri.Count;
                    }
                }
                snap.Sections.Add(sec);
            }
            snap.Revision = rev;
            return snap;
        }

        public CoverageSnapshot GetSnapshot(string cursor)
        {
            // Sin incremental en el twin WinForms: el head que pinta fluido
            // (PilotX.Desktop) habla con el engine. Contrato cumplido con full.
            return GetSnapshot();
        }

        public void Reset()
        {
            if (_form == null || _form.triStrip == null) return;
            for (int j = 0; j < _form.triStrip.Count; j++)
            {
                _form.triStrip[j]?.patchList?.Clear();
            }
        }
    }
}
