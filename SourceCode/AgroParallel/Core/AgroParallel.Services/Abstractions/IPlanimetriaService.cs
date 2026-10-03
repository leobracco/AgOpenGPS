// IPlanimetriaService — planimetría fase 3 en la cabina (opcional, APAGADA de
// fábrica): mapa de alturas del lote abierto, ambientes → prescripción y guía
// por curva de nivel.
//
// La implementación vive en el motor (PilotX.GuidanceEngine/
// EnginePlanimetriaService) porque necesita el Elevation.txt del lote, el
// plano local y la lista de guías. Si el host no la inyecta (Android, WinForms
// viejo) el controller contesta service-unavailable y la pantalla no muestra
// la función.
//
// Nada de esto toca el guiado salvo "Crear guía", que es una acción explícita
// del operario (igual que grabar una AB + Curva).
//
// Archivo nuevo, aditivo.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IPlanimetriaService
    {
        /// <summary>Estado, estadísticas y configuración.</summary>
        PlanimetriaEstadoDto Estado();

        /// <summary>Capa del mapa (plano local). ok=false si no hay mapa calculado
        /// o la función/la capa están apagadas.</summary>
        PlanimetriaCapaDto Capa();

        /// <summary>Cambios parciales de configuración (persisten).</summary>
        PlanimetriaEstadoDto Configurar(PlanimetriaConfigRequest req);

        /// <summary>(Re)calcula el mapa del lote abierto EN SEGUNDO PLANO.</summary>
        PlanimetriaEstadoDto Calcular();

        /// <summary>Crea y activa una guía curva sobre la curva de nivel pedida.</summary>
        PlanimetriaAccionDto CrearGuia(PlanimetriaGuiaRequest req);

        /// <summary>Escribe la prescripción por ambientes con las dosis
        /// cargadas y la activa en el lote abierto.</summary>
        PlanimetriaAccionDto CrearPrescripcion();
    }
}
