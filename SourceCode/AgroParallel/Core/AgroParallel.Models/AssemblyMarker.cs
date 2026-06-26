// AgroParallel.Models — marker class para que el assembly nunca quede vacío
// mientras se migran los POCOs desde GPS/AgroParallel/. Las configs
// (QuantiXConfig, VistaXConfig, etc.) se irán moviendo acá manteniendo sus
// namespaces originales (AgroParallel.QuantiX, etc.) para no romper consumers.

namespace AgroParallel.Models
{
    internal static class AssemblyMarker { }
}
