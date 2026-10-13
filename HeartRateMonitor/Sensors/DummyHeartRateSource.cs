using HeartRateMonitor.Models;

namespace HeartRateMonitor.Sensors;

/// <summary>
/// Fakes a dance session: the heart rate wanders toward a target that changes every
/// 20-60 seconds, so it drifts through all the zones like a real workout.
/// </summary>
public sealed class DummyHeartRateSource : IHeartRateSource
{
    private readonly Random _random = new();
    private readonly System.Threading.Timer _timer;
    private double _bpm = 72;
    private double _target = 72;
    private DateTime _nextTargetChange = DateTime.MinValue;

    public event Action<HeartRateSample>? SampleReceived;
    public event Action<SourceState, string>? StateChanged;
    public event Action<int>? BatteryLevelChanged;

    public DummyHeartRateSource() =>
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);

    public void Start()
    {
        StateChanged?.Invoke(SourceState.Connected, "Dummy data");
        BatteryLevelChanged?.Invoke(87);
        _timer.Change(0, 1000);
    }

    private void Tick() => SampleReceived?.Invoke(Next(DateTime.Now));

    /// <summary>The next fake sample, as if taken at <paramref name="now"/>.</summary>
    public HeartRateSample Next(DateTime now)
    {
        if (now >= _nextTargetChange)
        {
            // Mostly working out, sometimes a rest between songs.
            _target = _random.NextDouble() < 0.2 ? _random.Next(75, 100) : _random.Next(110, 185);
            _nextTargetChange = now.AddSeconds(_random.Next(20, 60));
        }

        _bpm += (_target - _bpm) * 0.08 + (_random.NextDouble() - 0.5) * 3;
        int bpm = (int)Math.Round(Math.Clamp(_bpm, 45, 210));

        // One RR interval per beat in the last second, roughly.
        double rr = 60000.0 / bpm;
        var intervals = Enumerable.Range(0, Math.Max(1, bpm / 60))
            .Select(_ => Math.Round(rr + (_random.NextDouble() - 0.5) * 30, 1))
            .ToArray();

        return new HeartRateSample(now, bpm, intervals, true);
    }

    public void Dispose() => _timer.Dispose();
}
