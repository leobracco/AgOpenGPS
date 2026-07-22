// ============================================================================
// Fase1Stubs.cs
// Implementaciones vacías de los servicios que en Windows respalda FormGPS
// (guiado, lotes, vehículo, cobertura...). En la Fase 1 del port (Hub sin
// guiado) no existen: el tablet es monitor de siembra + config de nodos +
// OTA + sync. Cuando llegue la Fase 2 (guiado Android) se reemplazan por
// implementaciones reales sobre los servicios extraídos del Core.
//
// Ya reemplazados por implementaciones reales respaldadas por
// GuidanceEngineHost (bloque 14, 2026-07-22 — GuidanceEngineServices.cs +
// GuidanceEngineStateServices.cs): StubLotesService, StubGuidanceCalculator,
// StubAogStateProvider, StubSectionControlService, StubVehicleToolService,
// StubCoverageService, StubQuantiXRuntimeService.
//
// Quedan como stub porque son un subsistema distinto, no datos de guiado:
//   - StubShapefileService: necesita parseo de shapefile (capa que
//     GuidanceEngineHost no carga en absoluto todavía).
//   - StubPilotXUpdateService: self-update (bloque 12 de la matriz, 0%,
//     APK vía OrbitX en vez de Updater.exe+ZIP — feature aparte).
//   - StubSistemaService: brillo/apagado son APIs de Android (Settings.System,
//     PowerManager), no algo que GuidanceEngineHost pueda respaldar.
// ============================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.Droid
{
    internal sealed class StubShapefileService : IShapefileService
    {
        public Task<ShapefileUploadResult> UploadAsync(IReadOnlyList<ShapefileUploadFile> files)
            => Task.FromResult(new ShapefileUploadResult());
        public Task<bool> RemoveAsync() => Task.FromResult(false);
    }

    internal sealed class StubPilotXUpdateService : IPilotXUpdateService
    {
        public PilotXUpdateStatus GetStatus() => new PilotXUpdateStatus();
        public Task<PilotXUpdateStatus> CheckAsync() => Task.FromResult(new PilotXUpdateStatus());
        public Task<PilotXUpdateStatus> DownloadAsync() => Task.FromResult(new PilotXUpdateStatus());
        public Task<PilotXUpdateStatus> ApplyAsync() => Task.FromResult(new PilotXUpdateStatus());
    }

    internal sealed class StubSistemaService : ISistemaService
    {
        public int GetBrightness() => -1;
        public bool SetBrightness(int percent) => false;
        public void ExecutePowerAction(PowerAction action) { }
    }
}
