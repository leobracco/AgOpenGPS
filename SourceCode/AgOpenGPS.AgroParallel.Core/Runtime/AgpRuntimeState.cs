namespace AgOpenGPS.AgroParallel.Core.Runtime;

/// <summary>
/// Snapshot read model for the visual layer.
/// This object is intentionally UI-agnostic and should not contain WinForms, OpenGL,
/// serial-port, or hardware references.
/// </summary>
public sealed record AgpRuntimeState
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public AgpGpsState Gps { get; init; } = new();
    public AgpMachineState Machine { get; init; } = new();
    public AgpGuidanceState Guidance { get; init; } = new();
    public AgpFieldState Field { get; init; } = new();
    public AgpSectionsState Sections { get; init; } = new();
    public AgpConnectivityState Connectivity { get; init; } = new();
    public IReadOnlyList<AgpAlert> Alerts { get; init; } = Array.Empty<AgpAlert>();
}

public sealed record AgpGpsState
{
    public bool IsPositionInitialized { get; init; }
    public string FixQuality { get; init; } = string.Empty;
    public double AgeSeconds { get; init; }
    public int Satellites { get; init; }
    public double? AccuracyCm { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

public sealed record AgpMachineState
{
    public double SpeedKph { get; init; }
    public double HeadingDegrees { get; init; }
    public double RollDegrees { get; init; }
    public bool IsReverseDetected { get; init; }
}

public sealed record AgpGuidanceState
{
    public string Mode { get; init; } = string.Empty;
    public string CurrentLineName { get; init; } = string.Empty;
    public double CrossTrackErrorCm { get; init; }
    public double SteerSetpointDegrees { get; init; }
    public double ActualSteerDegrees { get; init; }
    public bool IsAutoSteerOn { get; init; }
    public bool IsAutoSteerAvailable { get; init; }
    public bool IsUturnAvailable { get; init; }
    public bool IsUturnOn { get; init; }
}

public sealed record AgpFieldState
{
    public bool IsJobStarted { get; init; }
    public string FieldName { get; init; } = string.Empty;
    public double WorkedHa { get; init; }
    public double ActualWorkedHa { get; init; }
    public double CoveragePercent { get; init; }
    public bool IsOutOfBounds { get; init; }
}

public sealed record AgpSectionsState
{
    public int Total { get; init; }
    public int Active { get; init; }
    public string Mode { get; init; } = string.Empty;
}

public sealed record AgpConnectivityState
{
    public bool IsCoreConnected { get; init; }
    public bool IsAgIoConnected { get; init; }
    public bool IsOrbitXConnected { get; init; }
    public string HardwareMessage { get; init; } = string.Empty;
}

public sealed record AgpAlert
{
    public AgpAlertSeverity Severity { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public enum AgpAlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}
