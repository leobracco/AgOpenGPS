// INodoRegistryService — descubre y mantiene una tabla de nodos AgroParallel
// vivos en la LAN MQTT. Reemplaza la lógica embebida en FormNodos.cs.

using System;
using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface INodoRegistryService
    {
        /// <summary>Conectar al broker y arrancar la suscripción a announcements + status_live.</summary>
        void Start(string brokerAddress, int brokerPort);

        /// <summary>Detener la suscripción y cerrar conexión.</summary>
        void Stop();

        /// <summary>Snapshot tipado del registro actual (ordenado: online primero).</summary>
        IReadOnlyList<NodoStatus> GetAll();

        /// <summary>Dispara cuando el registro cambia (alta, baja, transición online/offline).</summary>
        event EventHandler Changed;
    }
}
