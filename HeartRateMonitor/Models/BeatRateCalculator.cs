namespace HeartRateMonitor.Models;

/// <summary>
/// Works out heart rate from the beat-to-beat (RR) intervals instead of trusting the strap's own number,
/// which some straps get badly wrong (e.g. when the pads aren't making good contact).
/// </summary>
public sealed class BeatRateCalculator
{
    /// <summary>Averaging a few beats smooths out normal beat-to-beat variation.</summary>
    private const int BeatsToAverage = 8;
    /// <summary>No beats for this long and the old ones are thrown away rather than averaged with new ones.</summary>
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(5);
    /// <summary>Intervals outside 30–240 bpm are noise, not heartbeats.</summary>
    private const double MinIntervalMs = 250, MaxIntervalMs = 2000;

    private readonly Queue<double> _intervals = new();
    private DateTime _lastBeat = DateTime.MinValue;

    /// <summary>Takes in a sample's RR intervals and returns the heart rate they give, or null if there's no recent beat data.</summary>
    public int? Add(HeartRateSample sample)
    {
        if (sample.Time - _lastBeat > ForgetAfter)
            _intervals.Clear();

        foreach (double rr in sample.RrIntervalsMs)
        {
            if (rr is < MinIntervalMs or > MaxIntervalMs)
                continue;
            _intervals.Enqueue(rr);
            if (_intervals.Count > BeatsToAverage)
                _intervals.Dequeue();
            _lastBeat = sample.Time;
        }

        // At low heart rates some packets carry no interval; keep the last value until the beats go stale.
        if (_intervals.Count == 0 || sample.Time - _lastBeat > ForgetAfter)
            return null;
        return (int)Math.Round(60000 / _intervals.Average());
    }

    public void Reset()
    {
        _intervals.Clear();
        _lastBeat = DateTime.MinValue;
    }
}
