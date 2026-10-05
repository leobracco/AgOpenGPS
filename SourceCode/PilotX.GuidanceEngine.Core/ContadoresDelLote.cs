// ============================================================================
// ContadoresDelLote.cs — pone en cero el área y la distancia del lote.
//
// Lo llama el host al CERRAR el lote y al ABRIR uno (antes de cargar su
// cobertura). Sin esto, Fd.workedAreaTotal y compañía solo se recalculaban si
// el lote nuevo tenía Sections.txt (CargarCobertura): un lote nuevo arrancaba
// mostrando las HA del anterior, y la grilla neta seguía llena, así que el
// "Neta" resucitaba con el área del otro lote al primer cuadrilátero pintado.
// AOG lo hacía en JobClose (fd.workedAreaTotal = 0 +
// UpdateFieldBoundaryGUIAreas con la lista de linderos ya vacía) y en
// FileOpenField (workedAreaTotal/distanceUser = 0 antes de sumar Sections.txt).
//
// Las Tareas no se rompen: escuchan AntesDeCerrarLote, que corre ANTES de
// esto con el área todavía vigente; al reabrir el lote CargarCobertura
// reconstruye el mismo número y Reanudar toma esa base.
//
// Cálculo puro (sin host): se compila linkeado en AgOpenGPS.Core.Tests.
// ============================================================================

namespace AgOpenGPS
{
    public static class ContadoresDelLote
    {
        public static void Reiniciar(CFieldData fd, CoberturaNeta neta)
        {
            neta?.Reset();
            if (fd == null) return;

            fd.workedAreaTotal = 0;
            fd.workedAreaTotalUser = 0;
            fd.distanceUser = 0;
            fd.actualAreaCovered = 0;
            fd.overlapPercent = 0;
            fd.barPercent = 0;
            // Sin lote no hay lindero; al abrir, BuildTurnLines los recalcula.
            fd.areaOuterBoundary = 0;
            fd.areaBoundaryOuterLessInner = 0;
        }
    }
}
