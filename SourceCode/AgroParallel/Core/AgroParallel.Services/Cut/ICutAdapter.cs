// ============================================================================
// ICutAdapter.cs - Adapter de corte por producto.
//
// Cada producto de corte on/off (SectionX/relays, LineX/servo-embrague, futuros)
// implementa este contrato. El adapter NO conoce MQTT ni dedup: solo traduce el
// estado de corte de PilotX (AogStateSnapshot.SectionOnRequest) al payload de su
// producto, leyendo su propio config (sectionX.json / lineX.json). El transporte,
// el timing y la deduplicación viven en el CutDispatcher.
//
// Esto mantiene aislado lo específico de cada producto (mapeo salida→sección,
// topic, formato de payload) y comparte el transporte una sola vez.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Common;
using AgroParallel.Models;

namespace AgroParallel.Cut
{
    public interface ICutAdapter
    {
        /// <summary>Clave del producto: "sectionx", "linex". Usada por el dispatcher
        /// para enrutar status/debug/test.</summary>
        string Product { get; }

        /// <summary>Cantidad de nodos habilitados en el config actual. Alimenta el
        /// chip de estado de la UI.</summary>
        int NodeCount { get; }

        /// <summary>Recarga el config de disco. El dispatcher lo llama periódicamente
        /// (y on-demand vía ReloadNow) para capturar cambios guardados desde la UI.</summary>
        void Reload();

        /// <summary>Por cada nodo habilitado, devuelve la intención de corte de este
        /// tick. NO publica ni deduplica: el dispatcher decide. <paramref name="hist"/>
        /// permite reproducir el patrón de secciones de N metros atrás (tren trasero).</summary>
        IEnumerable<CutCommand> ComputePublishes(AogStateSnapshot snap, PositionHistory hist);

        /// <summary>Comandos de apagado seguro (todas las secciones en cero), usados
        /// por el dispatcher en Stop antes de desconectar.</summary>
        IEnumerable<CutCommand> OffCommands();
    }
}
