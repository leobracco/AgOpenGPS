namespace AgOpenGPS.AgroParallel.Core.Commands;

/// <summary>
/// Command envelope accepted from external visual layers.
/// Visual clients should send commands, never mutate engine state directly.
/// </summary>
public sealed record AgpCommand
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public AgpCommandType Type { get; init; }
    public Dictionary<string, string> Payload { get; init; } = new();
}

public enum AgpCommandType
{
    ToggleAutoSteer,
    ToggleSectionMaster,
    StartHeadlandTurn,
    CenterMap,
    ZoomIn,
    ZoomOut,
    NudgeLeft,
    NudgeRight,
    CycleLineNext,
    CycleLinePrevious,
    OpenJobMenu,
    OpenSettings,
    SetDisplayMode,
    SaveVehicleConfiguration,
    SaveImplementConfiguration,
    SaveGpsConfiguration,
    SaveImuConfiguration
}

public sealed record AgpCommandResult
{
    public Guid CommandId { get; init; }
    public bool Accepted { get; init; }
    public string Message { get; init; } = string.Empty;

    public static AgpCommandResult AcceptedResult(Guid commandId, string message = "") =>
        new() { CommandId = commandId, Accepted = true, Message = message };

    public static AgpCommandResult RejectedResult(Guid commandId, string message) =>
        new() { CommandId = commandId, Accepted = false, Message = message };
}
