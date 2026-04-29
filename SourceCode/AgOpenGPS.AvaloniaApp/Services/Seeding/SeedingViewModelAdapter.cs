using System;
using System.Linq;
using AgOpenGPS.AvaloniaApp.ViewModels;
using AgOpenGPS.AvaloniaApp.ViewModels.Items;
using AgOpenGPS.Seeding;
using Avalonia.Threading;

namespace AgOpenGPS.AvaloniaApp.Services.Seeding
{
    public sealed class SeedingViewModelAdapter : IDisposable
    {
        private readonly SeedingMonitor _monitor;
        private readonly HeaderBarViewModel _header;
        private readonly LeftPorSurcoViewModel _leftPorSurco;
        private readonly RightControlViewModel _rightControl;
        private readonly BottomAlertsViewModel _bottomAlerts;
        private DateTimeOffset _lastUiUpdate = DateTimeOffset.MinValue;

        public SeedingViewModelAdapter(
            SeedingMonitor monitor,
            HeaderBarViewModel header,
            LeftPorSurcoViewModel leftPorSurco,
            RightControlViewModel rightControl,
            BottomAlertsViewModel bottomAlerts)
        {
            _monitor = monitor;
            _header = header;
            _leftPorSurco = leftPorSurco;
            _rightControl = rightControl;
            _bottomAlerts = bottomAlerts;

            // Seed the UI with one row VM per planter row.
            for (int i = 0; i < monitor.Rows.Count; i++)
            {
                _leftPorSurco.Rows.Add(new RowStatusItemViewModel(i + 1));
            }

            _monitor.SnapshotPublished += OnSnapshot;
            _monitor.AlertRaised += OnAlert;
        }

        public void Dispose()
        {
            _monitor.SnapshotPublished -= OnSnapshot;
            _monitor.AlertRaised -= OnAlert;
        }

        private void OnSnapshot(object? sender, SeedingSnapshot snapshot)
        {
            // Throttle to ~10 Hz visually.
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastUiUpdate).TotalMilliseconds < 95) return;
            _lastUiUpdate = now;

            Dispatcher.UIThread.Post(() => Apply(snapshot));
        }

        private void Apply(SeedingSnapshot snapshot)
        {
            _header.SpeedTile.Value = snapshot.SpeedKmh.ToString("F1");
            _header.RpmTile.Value = ((int)snapshot.MeterShaftRpm).ToString();
            _header.HectaresTile.Value = snapshot.HectaresWorked.ToString("F1");
            _header.GpsTile.Value = "RTK";
            _header.GpsTile.Badge = "FIX";

            _rightControl.TargetSeedsPerMeter = snapshot.TargetSeedsPerMeter;
            _rightControl.VariationPercent = snapshot.VariationPercent;
            _rightControl.DoserStatus = "OK";

            for (int i = 0; i < snapshot.Rows.Count && i < _leftPorSurco.Rows.Count; i++)
            {
                var metric = snapshot.Rows[i];
                var vm = _leftPorSurco.Rows[i];
                vm.SeedsPerMeter = metric.SeedsPerMeter;

                // Severity heuristic from singulation health when no explicit alert is present.
                if (metric.SkipsPercent > 4) vm.Severity = RowAlertSeverity.Critical;
                else if (metric.DoublesPercent > 5) vm.Severity = RowAlertSeverity.Warning;
                else vm.Severity = RowAlertSeverity.Ok;
            }
        }

        private void OnAlert(object? sender, RowAlert alert)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // Cap visible alerts to 4 to match the mockup's bottom bar.
                while (_bottomAlerts.Alerts.Count >= 4) _bottomAlerts.Alerts.RemoveAt(0);
                _bottomAlerts.Alerts.Add(new AlertBannerViewModel(
                    title: $"Surco {alert.RowIndex + 1}",
                    detail: alert.Message,
                    severity: alert.Severity));

                if (alert.RowIndex >= 0 && alert.RowIndex < _leftPorSurco.Rows.Count)
                {
                    var vm = _leftPorSurco.Rows[alert.RowIndex];
                    if (alert.Severity > vm.Severity) vm.Severity = alert.Severity;
                }
            });
        }
    }
}
