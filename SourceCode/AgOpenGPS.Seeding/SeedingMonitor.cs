using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AgOpenGPS.Seeding
{
    public sealed class SeedingMonitor
    {
        private readonly List<PlanterRow> _rows;
        private SeedingSnapshot? _lastSnapshot;

        public SeedingMonitor(IEnumerable<PlanterRow> rows, SeedingTargets targets)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            _rows = new List<PlanterRow>(rows);
            Rows = new ReadOnlyCollection<PlanterRow>(_rows);
            Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        public IReadOnlyList<PlanterRow> Rows { get; }
        public SeedingTargets Targets { get; }
        public SeedingSnapshot? LastSnapshot => _lastSnapshot;

        public event EventHandler<SeedingSnapshot>? SnapshotPublished;
        public event EventHandler<RowAlert>? AlertRaised;

        public void Bind(ISeedingDataSource source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            source.SnapshotReceived += OnSnapshot;
            source.AlertReceived += OnAlert;
        }

        public void Unbind(ISeedingDataSource source)
        {
            if (source is null) return;
            source.SnapshotReceived -= OnSnapshot;
            source.AlertReceived -= OnAlert;
        }

        private void OnSnapshot(object? sender, SeedingSnapshot snapshot)
        {
            _lastSnapshot = snapshot;
            SnapshotPublished?.Invoke(this, snapshot);
        }

        private void OnAlert(object? sender, RowAlert alert)
        {
            AlertRaised?.Invoke(this, alert);
        }
    }
}
