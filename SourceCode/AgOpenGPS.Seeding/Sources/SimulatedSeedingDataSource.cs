using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenGPS.Seeding.Sources
{
    public sealed class SimulatedSeedingDataSource : ISeedingDataSource, IDisposable
    {
        private readonly Random _rng;
        private readonly SeedingTargets _targets;
        private readonly TimeSpan _samplePeriod;
        private readonly double _doubleSeedProbability;
        private readonly double _skipProbability;

        private CancellationTokenSource? _cts;
        private Task? _loop;
        private double _hectaresAccumulator;
        private double _meterRpm = 35.0;

        public SimulatedSeedingDataSource(
            int rowCount = 24,
            SeedingTargets? targets = null,
            int sampleHz = 10,
            double doubleSeedProbability = 0.005,
            double skipProbability = 0.005,
            int? randomSeed = null)
        {
            if (rowCount <= 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
            if (sampleHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleHz));

            RowCount = rowCount;
            _targets = targets ?? new SeedingTargets(targetSeedsPerHectare: 80000, rowSpacingCm: 52.5, boomWidthMeters: rowCount * 0.525);
            _samplePeriod = TimeSpan.FromMilliseconds(1000.0 / sampleHz);
            _doubleSeedProbability = doubleSeedProbability;
            _skipProbability = skipProbability;
            _rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        }

        public event EventHandler<SeedingSnapshot>? SnapshotReceived;
        public event EventHandler<RowAlert>? AlertReceived;

        public int RowCount { get; }

        public bool IsRunning => _loop is { IsCompleted: false };

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (IsRunning) return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_cts is null) return;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try
            {
                if (_loop is not null) await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch { /* best effort */ }
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            double speedKmh = 7.5;
            double targetPerMeter = _targets.TargetSeedsPerMeterPerRow > 0 ? _targets.TargetSeedsPerMeterPerRow : 5.2;

            while (!token.IsCancellationRequested)
            {
                speedKmh = Clamp(speedKmh + (_rng.NextDouble() - 0.5) * 0.4, 5.0, 11.0);
                double speedMs = speedKmh / 3.6;
                _meterRpm = Clamp(_meterRpm + (_rng.NextDouble() - 0.5) * 1.5, 25.0, 55.0);

                double dt = _samplePeriod.TotalSeconds;
                double swathArea = _targets.BoomWidthMeters * speedMs * dt;
                _hectaresAccumulator += swathArea / 10000.0;

                var rows = new List<RowMetric>(RowCount);
                double sumPerMeter = 0;
                var now = DateTimeOffset.UtcNow;

                for (int i = 0; i < RowCount; i++)
                {
                    double jitter = (_rng.NextDouble() - 0.5) * 0.6;
                    double seedsPerMeter = Math.Max(0, targetPerMeter + jitter);
                    double seedsPerSecond = seedsPerMeter * speedMs;

                    double doubles = _rng.NextDouble() < _doubleSeedProbability ? _rng.NextDouble() * 8 : _rng.NextDouble() * 1.2;
                    double skips = _rng.NextDouble() < _skipProbability ? _rng.NextDouble() * 6 : _rng.NextDouble() * 0.8;
                    double singulation = Math.Max(0, 100 - doubles - skips);

                    rows.Add(new RowMetric(
                        rowIndex: i,
                        seedsPerSecond: seedsPerSecond,
                        seedsPerMeter: seedsPerMeter,
                        singulationPercent: singulation,
                        doublesPercent: doubles,
                        skipsPercent: skips,
                        meterRpm: _meterRpm,
                        timestamp: now));
                    sumPerMeter += seedsPerMeter;

                    if (doubles > 5)
                    {
                        AlertReceived?.Invoke(this, new RowAlert(i, RowAlertSeverity.Warning, RowAlertCode.DoubleSeed,
                            $"Semilla doble alta en surco {i + 1}", now));
                    }
                    else if (skips > 4)
                    {
                        AlertReceived?.Invoke(this, new RowAlert(i, RowAlertSeverity.Critical, RowAlertCode.NoFlow,
                            $"Falla de flujo en surco {i + 1}", now));
                    }
                }

                double avg = rows.Count > 0 ? sumPerMeter / rows.Count : 0;
                double variation = avg > 0 ? (avg - targetPerMeter) / targetPerMeter * 100.0 : 0;

                var snapshot = new SeedingSnapshot(
                    rows: rows,
                    targetSeedsPerMeter: targetPerMeter,
                    averageSeedsPerMeter: avg,
                    variationPercent: variation,
                    meterShaftRpm: _meterRpm,
                    hectaresWorked: _hectaresAccumulator,
                    speedKmh: speedKmh,
                    timestamp: now);

                SnapshotReceived?.Invoke(this, snapshot);

                try { await Task.Delay(_samplePeriod, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
    }
}
