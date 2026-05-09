using System;
using System.Collections.Generic;
using AgOpenGPS.AgroParallel.Core.Bridge;
using AgOpenGPS.AgroParallel.Core.Commands;
using AgOpenGPS.AgroParallel.Core.Runtime;

namespace AgOpenGPS.AgroParallel.Bridge;

/// <summary>
/// First adapter between the current WinForms engine and the new Agro Parallel UI/Core contracts.
/// This class must remain thin: read state, dispatch safe commands, and avoid changing calculation logic.
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
        return new AgpRuntimeState
        {
            Timestamp = DateTimeOffset.UtcNow,
            Gps = new AgpGpsState
            {
                IsPositionInitialized = Safe(() => _form.isGPSPositionInitialized),
                FixQuality = Safe(() => _form.FixQuality, string.Empty),
                AgeSeconds = Safe(() => _form.pn.age),
                Satellites = Safe(() => _form.pn.satellitesTracked),
                Latitude = SafeNullable(() => _form.pn.latitude),
                Longitude = SafeNullable(() => _form.pn.longitude)
            },
            Machine = new AgpMachineState
            {
                SpeedKph = Safe(() => _form.pn.speed),
                HeadingDegrees = Safe(() => _form.fixHeading),
                RollDegrees = Safe(() => _form.ahrs.rollX16 / 16.0),
                IsReverseDetected = Safe(() => _form.mc.isReverse)
            },
            Guidance = new AgpGuidanceState
            {
                Mode = Safe(() => _form.isStanleyUsed ? "Stanley" : "PurePursuit", string.Empty),
                CurrentLineName = Safe(GetCurrentLineName, string.Empty),
                CrossTrackErrorCm = Safe(() => _form.distanceFromCurrentLine * 100.0),
                SteerSetpointDegrees = Safe(() => _form.guidanceLineSteerAngle),
                ActualSteerDegrees = Safe(() => _form.mc.actualSteerAngleDegrees),
                IsAutoSteerOn = Safe(() => _form.isBtnAutoSteerOn),
                IsAutoSteerAvailable = Safe(() => _form.btnAutoSteer.Enabled),
                IsUturnAvailable = Safe(() => _form.btnAutoYouTurn.Enabled),
                IsUturnOn = Safe(() => _form.yt.isYouTurnTriggered)
            },
            Field = new AgpFieldState
            {
                IsJobStarted = Safe(() => _form.isJobStarted),
                FieldName = Safe(() => _form.displayFieldName, string.Empty),
                WorkedHa = Safe(() => _form.fd.workedAreaTotal),
                ActualWorkedHa = Safe(() => _form.fd.workedAreaActual),
                IsOutOfBounds = Safe(() => _form.mc.isOutOfBounds)
            },
            Sections = new AgpSectionsState
            {
                Total = Safe(() => _form.tool.numOfSections),
                Active = Safe(GetActiveSections),
                Mode = Safe(GetSectionMode, string.Empty)
            },
            Connectivity = new AgpConnectivityState
            {
                IsCoreConnected = true,
                IsAgIoConnected = Safe(() => _form.isAgIOConnected),
                HardwareMessage = Safe(() => _form.lblHardwareMessage.Text, string.Empty)
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
                    if (!_form.btnAutoSteer.Enabled)
                        return AgpCommandResult.RejectedResult(command.CommandId, "Autosteer is not available.");

                    _form.btnAutoSteer.PerformClick();
                    return AgpCommandResult.AcceptedResult(command.CommandId, "Autosteer toggled.");

                case AgpCommandType.StartHeadlandTurn:
                    if (!_form.btnAutoYouTurn.Enabled)
                        return AgpCommandResult.RejectedResult(command.CommandId, "Headland turn is not available.");

                    _form.btnAutoYouTurn.PerformClick();
                    return AgpCommandResult.AcceptedResult(command.CommandId, "Headland turn requested.");

                case AgpCommandType.CenterMap:
                    _form.SetZoom();
                    return AgpCommandResult.AcceptedResult(command.CommandId, "Map centered.");

                case AgpCommandType.ZoomIn:
                    _form.ZoomIn();
                    return AgpCommandResult.AcceptedResult(command.CommandId, "Zoom in requested.");

                case AgpCommandType.ZoomOut:
                    _form.ZoomOut();
                    return AgpCommandResult.AcceptedResult(command.CommandId, "Zoom out requested.");

                case AgpCommandType.CycleLineNext:
                    _form.btnCycleLines.PerformClick();
                    return AgpCommandResult.AcceptedResult(command.CommandId, "Next guidance line requested.");

                case AgpCommandType.CycleLinePrevious:
                    _form.btnCycleLinesBk.PerformClick();
                    return AgpCommandResult.AcceptedResult(command.CommandId, "Previous guidance line requested.");

                default:
                    return AgpCommandResult.RejectedResult(command.CommandId, $"Command {command.Type} is not implemented by the legacy bridge yet.");
            }
        }
        catch (Exception ex)
        {
            return AgpCommandResult.RejectedResult(command.CommandId, ex.Message);
        }
    }

    private string GetCurrentLineName()
    {
        if (_form.trk.idx > -1 && _form.trk.gArr.Count > _form.trk.idx)
            return _form.trk.gArr[_form.trk.idx].name;

        if (_form.ABLine.isABLineSet)
            return "AB Line";

        if (_form.curve.isCurveSet)
            return "AB Curve";

        if (_form.ct.isContourBtnOn)
            return "Contour";

        return string.Empty;
    }

    private int GetActiveSections()
    {
        var active = 0;
        var total = Math.Min(_form.tool.numOfSections, _form.section.Length);

        for (var i = 0; i < total; i++)
        {
            if (_form.section[i].isSectionOn)
                active++;
        }

        return active;
    }

    private string GetSectionMode()
    {
        return _form.autoBtnState switch
        {
            btnStates.Auto => "Auto",
            btnStates.On => "Manual",
            _ => "Off"
        };
    }

    private IReadOnlyList<AgpAlert> BuildAlerts()
    {
        var alerts = new List<AgpAlert>();

        if (!Safe(() => _form.isGPSPositionInitialized))
        {
            alerts.Add(new AgpAlert
            {
                Severity = AgpAlertSeverity.Warning,
                Code = "GPS_NOT_INITIALIZED",
                Message = "GPS position is not initialized."
            });
        }

        if (Safe(() => _form.mc.isOutOfBounds))
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

    private static T Safe<T>(Func<T> read, T fallback = default!)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static double? SafeNullable(Func<double> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
