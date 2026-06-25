using System;

namespace AgroParallel.QuantiX
{
    /// <summary>
    /// Resuelve la dosis efectiva (kg/ha o L/ha) de un motor QuantiX.
    /// Cascada (spec N motores): Manual > Mapa > Fija.
    ///   1) ManualMode → ManualDosis (override total del widget en pantalla).
    ///   2) Mapa: si CampoDosis está seteado, el valor del shapefile (DBF) de
    ///      ese campo; si no, el mapa global del tick (mapaGlobal).
    ///   3) Si el mapa no aporta dosis (&lt;= 0) → DosisFija (default fuera de mapa).
    /// </summary>
    public static class QxDoseResolver
    {
        /// <param name="campoLookup">
        /// Función que devuelve la dosis del campo DBF dado su nombre
        /// (típicamente IAogStateProvider.GetShapeFieldDose). Solo se invoca
        /// cuando CampoDosis no está vacío.
        /// </param>
        public static double Resolve(
            bool manualMode,
            double manualDosis,
            double dosisFija,
            string campoDosis,
            double mapaGlobal,
            Func<string, double> campoLookup)
        {
            if (manualMode)
                return manualDosis;

            // Mapa manda: campo específico del motor, o mapa global del tick.
            double mapa = !string.IsNullOrEmpty(campoDosis)
                ? (campoLookup != null ? campoLookup(campoDosis) : 0)
                : mapaGlobal;

            if (mapa > 0)
                return mapa;

            // Sin mapa → cae a la dosis fija configurada.
            return dosisFija;
        }
    }
}
