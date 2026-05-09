using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using AgOpenGPS.AgroParallel.Core.Bridge;
using AgOpenGPS.AgroParallel.Core.Commands;
using AgOpenGPS.AgroParallel.Core.Runtime;

namespace AgOpenGPS.AgroParallel.Bridge;

/// <summary>
/// First adapter between the current WinForms engine and the new Agro Parallel UI/Core contracts.
///
/// This bridge is intentionally reflection-based at this stage so the Core split can start without
/// changing access modifiers or touching legacy calculation, communication, drawing or safety code.
/// Once the engine is gradually extracted, reflection reads should be replaced by typed services.
/// </summary>
public sealed class FormGpsAgpEngineBridge : IAgpEngineBridge
{
    private readonly FormGPS _form;

    public FormGpsAgpEngineBridge(FormGPS form)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));
    }

    public AgpRuntimeState GetSnapshot()
    {
        var pn = ReadObject(_form, "pn");
        var ahrs = ReadObject(_form, "ahrs");
        var mc = ReadObject(_form, "mc");
        var fd = ReadObject(_form, "fd");
        var tool = ReadObject(_form, "tool");

        return new AgpRuntimeState
        {
            Timestamp = DateTimeOffset.UtcNow,
            Gps = new AgpGpsState
            {
                IsPositionInitialized = ReadBool(_form, "isGPSPositionInitialized"),
                FixQuality = ReadString(_form, "FixQuality"),
                AgeSeconds = ReadDouble(pn, "age"),
                Satellites = ReadInt(pn, "satellitesTracked"),
                Latitude = ReadNullableDouble(pn, "latitude"),
                Longitude = ReadNullableDouble(pn, "longitude")
            },
            Machine = new AgpMachineState
            {
                SpeedKph = ReadDouble(pn, "speed"),
                HeadingDegrees = ReadDouble(_form, "fixHeading"),
                RollDegrees = ReadDouble(ahrs, "rollX16") / 16.0,
                IsReverseDetected = ReadBool(mc, "isReverse")
            },
            Guidance = new AgpGuidanceState
            {
                Mode = ReadBool(_form, "isStanleyUsed") ? "Stanley" : "PurePursuit",
                CurrentLineName = GetCurrentLineName(),
                CrossTrackErrorCm = ReadDouble(_form, "distanceFromCurrentLine") * 100.0,
                SteerSetpointDegrees = ReadDouble(_form, "guidanceLineSteerAngle"),
                ActualSteerDegrees = ReadDouble(mc, "actualSteerAngleDegrees"),
                IsAutoSteerOn = ReadBool(_form, "isBtnAutoSteerOn"),
                IsAutoSteerAvailable = ReadButtonEnabled("btnAutoSteer"),
                IsUturnAvailable = ReadButtonEnabled("btnAutoYouTurn"),
                IsUturnOn = ReadBool(ReadObject(_form, "yt"), "isYouTurnTriggered")
            },
            Field = new AgpFieldState
            {
                IsJobStarted = ReadBool(_form, "isJobStarted"),
                FieldName = ReadString(_form, "displayFieldName"),
                WorkedHa = ReadDouble(fd, "workedAreaTotal"),
                ActualWorkedHa = ReadDouble(fd, "workedAreaActual"),
                IsOutOfBounds = ReadBool(mc, "isOutOfBounds")
            },
            Sections = new AgpSectionsState
            {
                Total = ReadInt(tool, "numOfSections"),
                Active = GetActiveSections(),
                Mode = GetSectionMode()
            },
            Connectivity = new AgpConnectivityState
            {
                IsCoreConnected = true,
                IsAgIoConnected = ReadBool(_form, "isAgIOConnected"),
                HardwareMessage = ReadControlText("lblHardwareMessage")
            },
            Alerts = BuildAlerts()
        };
    }

    public AgpCommandResult Execute(AgpCommand command)
    {
        try
        {
            switch (command.Type)
            {
                case AgpCommandType.ToggleAutoSteer:
                    return PerformButtonCommand(command, "btnAutoSteer", "Autosteer toggled.", "Autosteer is not available.");

                case AgpCommandType.StartHeadlandTurn:
                    return PerformButtonCommand(command, "btnAutoYouTurn", "Headland turn requested.", "Headland turn is not available.");

                case AgpCommandType.CenterMap:
                    return InvokeFormMethod(command, "SetZoom", "Map centered.");

                case AgpCommandType.ZoomIn:
                    return InvokeFormMethod(command, "ZoomIn", "Zoom in requested.");

                case AgpCommandType.ZoomOut:
                    return InvokeFormMethod(command, "ZoomOut", "Zoom out requested.");

                case AgpCommandType.CycleLineNext:
                    return PerformButtonCommand(command, "btnCycleLines", "Next guidance line requested.", "Next line button is not available.");

                case AgpCommandType.CycleLinePrevious:
                    return PerformButtonCommand(command, "btnCycleLinesBk", "Previous guidance line requested.", "Previous line button is not available.");

                default:
                    return AgpCommandResult.RejectedResult(command.CommandId, $"Command {command.Type} is not implemented by the legacy bridge yet.");
            }
        }
        catch (Exception ex)
        {
            return AgpCommandResult.RejectedResult(command.CommandId, ex.Message);
        }
    }

    private AgpCommandResult PerformButtonCommand(AgpCommand command, string buttonName, string acceptedMessage, string rejectedMessage)
    {
        var button = ReadFieldOrProperty(_form, buttonName) as Button;
        if (button is null || !button.Enabled)
            return AgpCommandResult.RejectedResult(command.CommandId, rejectedMessage);

        button.PerformClick();
        return AgpCommandResult.AcceptedResult(command.CommandId, acceptedMessage);
    }

    private AgpCommandResult InvokeFormMethod(AgpCommand command, string methodName, string acceptedMessage)
    {
        var method = _form.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method is null)
            return AgpCommandResult.RejectedResult(command.CommandId, $"Method {methodName} was not found.");

        method.Invoke(_form, Array.Empty<object>());
        return AgpCommandResult.AcceptedResult(command.CommandId, acceptedMessage);
    }

    private string GetCurrentLineName()
    {
        var trk = ReadObject(_form, "trk");
        var idx = ReadInt(trk, "idx", -1);
        var gArr = ReadFieldOrProperty(trk, "gArr") as System.Collections.IList;

        if (gArr is not null && idx > -1 && gArr.Count > idx)
        {
            var track = gArr[idx];
            var name = ReadString(track, "name");
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        if (ReadBool(ReadObject(_form, "ABLine"), "isABLineSet"))
            return "AB Line";

        if (ReadBool(ReadObject(_form, "curve"), "isCurveSet"))
            return "AB Curve";

        if (ReadBool(ReadObject(_form, "ct"), "isContourBtnOn"))
            return "Contour";

        return string.Empty;
    }

    private int GetActiveSections()
    {
        var sectionArray = ReadFieldOrProperty(_form, "section") as Array;
        if (sectionArray is null)
            return 0;

        var total = ReadInt(ReadObject(_form, "tool"), "numOfSections", sectionArray.Length);
        total = Math.Min(total, sectionArray.Length);

        var active = 0;
        for (var i = 0; i < total; i++)
        {
            if (ReadBool(sectionArray.GetValue(i), "isSectionOn"))
                active++;
        }

        return active;
    }

    private string GetSectionMode()
    {
        var value = ReadFieldOrProperty(_form, "autoBtnState");
        return value?.ToString() ?? string.Empty;
    }

    private IReadOnlyList<AgpAlert> BuildAlerts()
    {
        var alerts = new List<AgpAlert>();

        if (!ReadBool(_form, "isGPSPositionInitialized"))
        {
            alerts.Add(new AgpAlert
            {
                Severity = AgpAlertSeverity.Warning,
                Code = "GPS_NOT_INITIALIZED",
                Message = "GPS position is not initialized."
            });
        }

        if (ReadBool(ReadObject(_form, "mc"), "isOutOfBounds"))
        {
            alerts.Add(new AgpAlert
            {
                Severity = AgpAlertSeverity.Warning,
                Code = "OUT_OF_BOUNDS",
                Message = "Machine is out of boundary."
            });
        }

        return alerts;
    }

    private bool ReadButtonEnabled(string name)
    {
        return (ReadFieldOrProperty(_form, name) as Button)?.Enabled ?? false;
    }

    private string ReadControlText(string name)
    {
        return (ReadFieldOrProperty(_form, name) as Control)?.Text ?? string.Empty;
    }

    private static object? ReadObject(object? source, string name) => ReadFieldOrProperty(source, name);

    private static bool ReadBool(object? source, string name, bool fallback = false)
    {
        var value = ReadFieldOrProperty(source, name);
        return value is bool boolValue ? boolValue : fallback;
    }

    private static int ReadInt(object? source, string name, int fallback = 0)
    {
        var value = ReadFieldOrProperty(source, name);
        if (value is null) return fallback;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static double ReadDouble(object? source, string name, double fallback = 0.0)
    {
        var value = ReadFieldOrProperty(source, name);
        if (value is null) return fallback;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static double? ReadNullableDouble(object? source, string name)
    {
        var value = ReadFieldOrProperty(source, name);
        if (value is null) return null;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static string ReadString(object? source, string name, string fallback = "")
    {
        return ReadFieldOrProperty(source, name)?.ToString() ?? fallback;
    }

    private static object? ReadFieldOrProperty(object? source, string name)
    {
        if (source is null || string.IsNullOrWhiteSpace(name))
            return null;

        var type = source.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var property = type.GetProperty(name, flags);
        if (property is not null)
            return property.GetValue(source);

        var field = type.GetField(name, flags);
        return field?.GetValue(source);
    }
}
