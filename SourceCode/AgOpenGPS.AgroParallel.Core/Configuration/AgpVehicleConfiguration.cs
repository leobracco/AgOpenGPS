namespace AgOpenGPS.AgroParallel.Core.Configuration;

public sealed record AgpVehicleConfiguration
{
    public string ProfileName { get; init; } = string.Empty;
    public string VehicleType { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public double WheelBaseCm { get; init; }
    public double TrackCm { get; init; }
    public double MinimumTurnRadiusCm { get; init; }
    public double AntennaDistanceToPivotCm { get; init; }
    public double AntennaHeightCm { get; init; }
    public double AntennaOffsetCm { get; init; }
}

public sealed record AgpGpsConfiguration
{
    public string AntennaType { get; init; } = string.Empty;
    public bool RtkAlarmEnabled { get; init; }
    public bool KillAutosteerOnRtkLoss { get; init; }
    public double FixTriggerDistanceM { get; init; }
    public double StartSpeedKph { get; init; }
    public double ForwardDetectionDistanceM { get; init; }
    public double ReverseDetectionDistanceM { get; init; }
    public double HeadingFilterPercent { get; init; }
    public bool ReverseDetectionEnabled { get; init; }
    public double DualHeadingOffsetDegrees { get; init; }
    public bool DualAsImuEnabled { get; init; }
}

public sealed record AgpImuConfiguration
{
    public double RollZeroDegrees { get; init; }
    public double RollFilterPercent { get; init; }
    public bool InvertRoll { get; init; }
    public string ImuPort { get; init; } = string.Empty;
    public int BaudRate { get; init; }
}

public sealed record AgpImplementConfiguration
{
    public string AttachmentStyle { get; init; } = string.Empty;
    public double WidthCm { get; init; }
    public double OffsetCm { get; init; }
    public double OverlapGapCm { get; init; }
    public int SectionCount { get; init; }
    public double DefaultSectionWidthCm { get; init; }
    public double MinimumSectionSpeedKph { get; init; }
}
