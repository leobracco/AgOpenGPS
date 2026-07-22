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
                        // patchList[k][0] es HEADER (contador + color), no coordenada.
                        // Necesitamos header + >= 3 vértices reales para un triángulo.
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
