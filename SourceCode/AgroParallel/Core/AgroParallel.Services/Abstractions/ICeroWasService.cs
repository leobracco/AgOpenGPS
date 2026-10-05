// ICeroWasService — "Cero automático del WAS" (opcional, apagado de fábrica).
//
// La implementación vive en el motor (PilotX.GuidanceEngine/EngineCeroWasService)
// porque necesita el PGN 253 y el estado del guiado en cada fix. Si el host no
// lo inyecta (Android, WinForms viejo) el controller contesta
// service-unavailable y la pantalla no muestra la función.
//
// Criterio: el cero NUNCA se autocorrige. Prendida, la función mide y propone;
// el offset cambia solo con Aplicar (y queda Deshacer).
//
// Archivo nuevo, aditivo.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ICeroWasService
    {
        /// <summary>Estado de la medición y la propuesta.</summary>
        CeroWasEstadoDto Estado();

        /// <summary>Prende/apaga la función (persiste setAS_ceroWasAuto). Apagar
        /// borra la medición; no toca el offset.</summary>
        CeroWasEstadoDto Activar(bool on);

        /// <summary>Aplica el offset propuesto: UNA escritura (PGN 252/251 →
        /// EEPROM). Solo con el piloto desenganchado y sin manejo libre.</summary>
        CeroWasEstadoDto Aplicar();

        /// <summary>Vuelve al offset previo al último Aplicar (una escritura).</summary>
        CeroWasEstadoDto Deshacer();

        /// <summary>Descarta lo medido y empieza de nuevo.</summary>
        CeroWasEstadoDto Reiniciar();
    }
}
