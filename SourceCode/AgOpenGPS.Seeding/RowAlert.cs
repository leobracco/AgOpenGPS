using System;

namespace AgOpenGPS.Seeding
{
    public enum RowAlertSeverity
    {
        Ok = 0,
        Warning = 1,
        Critical = 2,
    }

    public enum RowAlertCode
    {
        None = 0,
        NoFlow,
        PartialFlow,
        DoubleSeed,
        Skip,
        LowDensity,
        OutOfRangeSpeed,
        SensorFault,
    }

    public sealed class RowAlert
    {
        public RowAlert(int rowIndex, RowAlertSeverity severity, RowAlertCode code, string? message, DateTimeOffset raisedAt)
        {
            RowIndex = rowIndex;
            Severity = severity;
            Code = code;
            Message = message ?? string.Empty;
            RaisedAt = raisedAt;
        }

        public int RowIndex { get; }
        public RowAlertSeverity Severity { get; }
        public RowAlertCode Code { get; }
        public string Message { get; }
        public DateTimeOffset RaisedAt { get; }
    }
}
