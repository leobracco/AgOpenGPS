// ============================================================================
// Fase1Stubs.cs
// Implementaciones vacías de los servicios que en Windows respalda FormGPS
// (guiado, lotes, vehículo, cobertura...). En la Fase 1 del port (Hub sin
// guiado) no existen: el tablet es monitor de siembra + config de nodos +
// OTA + sync. Cuando llegue la Fase 2 (guiado Android) se reemplazan por
// implementaciones reales sobre los servicios extraídos del Core.
//
// StubLotesService y StubGuidanceCalculator ya se reemplazaron (bloque 14,
// 2026-07-22) por GuidanceEngineLotesService/GuidanceEngineGuidanceCalculator
// (GuidanceEngineServices.cs), respaldadas por GuidanceEngineHost — sin fix
// GPS real todavía (falta CoreX/serial por USB-OTG), pero abrir/cerrar lote
// y ejecutar comandos de guiado (autosteer/uturn/pick) ya funcionan de verdad.
// ============================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.Droid
{
    internal sealed class StubAogStateProvider : IAogStateProvider
    {
        public AogStateSnapshot GetSnapshot() => new AogStateSnapshot();
        public AllSettingsSnapshot GetAllSettings() => new AllSettingsSnapshot();
        public EventLogSnapshot GetEventLog() => new EventLogSnapshot();
        public XteGraphSample GetXteGraphSample() => new XteGraphSample();
        public HeadingGraphSample GetHeadingGraphSample() => new HeadingGraphSample();
        public SteerGraphSample GetSteerGraphSample() => new SteerGraphSample();
        public CorrectionGraphSample GetCorrectionGraphSample() => new CorrectionGraphSample();
        public ShiftPosSnapshot GetShiftPos() => new ShiftPosSnapshot();
        public SimCoordsSnapshot GetSimCoords() => new SimCoordsSnapshot();
        public SectionColorsSnapshot GetSectionColors() => new SectionColorsSnapshot();
        public DisplayColorsSnapshot GetDisplayColors() => new DisplayColorsSnapshot();
        public double GetShapeFieldDose(string fieldName) => 0;
        public ShapeSnapshot GetShape() => null;
        public ShapeFieldsSnapshot GetShapeFields() => new ShapeFieldsSnapshot();
    }

    internal sealed class StubVehicleToolService : IVehicleToolService
    {
        public VehicleConfigDto GetVehicle() => new VehicleConfigDto();
        public ToolConfigDto GetTool() => new ToolConfigDto();
        public VehicleToolBundleDto GetBundle() => new VehicleToolBundleDto();
        public bool SaveVehicle(VehicleConfigDto cfg) => false;
        public bool SaveTool(ToolConfigDto cfg) => false;
        public string GetVehiculoCustom() => "";
        public bool SetVehiculoCustom(string archivo) => false;
        public ImuConfigDto GetImu() => new ImuConfigDto();
        public bool SaveImu(ImuConfigDto cfg) => false;
        public ImuLiveDto GetImuLive() => new ImuLiveDto();
        public bool ZeroRoll() => false;
        public bool AdjustRollZero(double delta) => false;
        public bool RemoveRollZero() => false;
        public bool ResetImu() => false;
    }

    internal sealed class StubShapefileService : IShapefileService
    {
        public Task<ShapefileUploadResult> UploadAsync(IReadOnlyList<ShapefileUploadFile> files)
            => Task.FromResult(new ShapefileUploadResult());
        public Task<bool> RemoveAsync() => Task.FromResult(false);
    }

    internal sealed class StubCoverageService : ICoverageService
    {
        public CoverageSnapshot GetSnapshot() => new CoverageSnapshot();
        public void Reset() { }
    }

    internal sealed class StubSectionControlService : ISectionControlService
    {
        public SectionControlSnapshot GetSnapshot() => new SectionControlSnapshot();
    }

    internal sealed class StubQuantiXRuntimeService : IQuantiXRuntimeService
    {
        public QuantiXRuntimeSnapshot GetSnapshot() => new QuantiXRuntimeSnapshot();
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
