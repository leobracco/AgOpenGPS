// ============================================================================
// IVistaXLiveService.cs — agregador de telemetría VistaX en vivo.
// Se suscribe (vía NodoRegistryService) al topic de telemetría configurado
// y mantiene un snapshot consolidado por tren/surco a partir de las lecturas
// de los ESP32. Calcula SPM (semillas/minuto) por surco mediante derivada
// temporal de "valor" y aplica timeout para marcar sensores como "no-data".
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IVistaXLiveService
    {
        /// <summary>Arranca la suscripción y los timers de cálculo.</summary>
        void Start();

        /// <summary>Detiene el agregador.</summary>
        void Stop();

        /// <summary>Snapshot consolidado para enviar al cliente HTTP.</summary>
        VistaXLiveSnapshotDto GetSnapshot();

        /// <summary>True si está consumiendo telemetría activamente.</summary>
        bool IsRunning { get; }

        /// <summary>Recarga config + implemento (llamar tras un PUT).</summary>
        void Reload();

        /// <summary>
        /// Inicio/parada manual del monitoreo (método de inicio "manual" o
        /// stop explícito del operario desde la UI).
        /// </summary>
        void ForzarMonitoreoManual(bool activo);

        /// <summary>
        /// Referencia de la regla de tres de densidad, a mano: "fijar" toma el
        /// flujo ACTUAL como equivalente a la densidad configurada del insumo;
        /// "auto" borra la referencia y deja que se recapture sola en la
        /// próxima pasada estable. Devuelve el spm_ref resultante (0 = queda
        /// pendiente de captura automática).
        /// </summary>
        double AjustarReferenciaDensidad(string accion);

        // ---- Prueba de siembra (conteo sobre una distancia) ----------------
        //
        // Herramienta de calibración: contar semillas de TODOS los surcos a lo
        // largo de N metros y decir cuál está bien y cuál no. Es lo que hoy se
        // hace a mano con una bandeja abajo del cuerpo.

        /// <summary>Arranca una prueba de N metros desde la posición actual.</summary>
        void PruebaIniciar(double distanciaM);

        /// <summary>Pausa la prueba (conserva lo medido; iniciar reanuda).</summary>
        void PruebaCancelar();

        /// <summary>Borra la prueba: metros y contadores a cero.</summary>
        void PruebaReset();

        /// <summary>Estado/resultados de la prueba. Nunca null.</summary>
        VistaXPruebaDto PruebaEstado();
    }
}
