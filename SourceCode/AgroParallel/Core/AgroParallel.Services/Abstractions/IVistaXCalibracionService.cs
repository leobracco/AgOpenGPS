// ============================================================================
// IVistaXCalibracionService.cs — ventana de captura "Detectar densidad".
// Conceptualmente independiente del live service: lee el snapshot del live
// cada N ms, promedia los sem/m de los surcos tipo "semilla" durante una
// ventana fija y, al cerrar, deja un valor listo para persistir en el
// catálogo de insumos.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IVistaXCalibracionService
    {
        /// <summary>Arranca una ventana nueva. Si ya había una corriendo la cancela.
        /// Devuelve false si no hay insumo válido al cual escribir.</summary>
        bool Start(VistaXCalibracionStartDto req);

        /// <summary>Estado actual (running / valor live / segundos restantes / listo).</summary>
        VistaXCalibracionStateDto GetState();

        /// <summary>Persiste el resultado al catálogo de insumos.
        /// Si <paramref name="req"/>.Aceptar == false, cancela.
        /// Devuelve true si escribió al insumo.</summary>
        bool Apply(VistaXCalibracionApplyDto req);

        /// <summary>Cancela la ventana en curso sin persistir.</summary>
        void Cancel();
    }
}
