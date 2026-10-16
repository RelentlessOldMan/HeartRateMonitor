namespace HeartRateMonitor.Models;

/// <summary>
/// Works out heart rate from the beat-to-beat (RR) intervals instead of trusting the strap's own number,
/// which some straps get badly wrong (e.g. when the pads aren't making good contact).
/// </summary>
/// <remarks>
/// Moving the strap makes it see extra or missed "beats", giving intervals far shorter or longer than the real
/// rhythm. Real heart rate doesn't change by much from one beat to the next, so an interval that differs from the
/// recent ones by more than <see cref="MaxJump"/> is treated as noise and dropped; the old value is kept for up to
/// <see cref="ForgetAfter"/>, then none is shown. Beats close to the last trusted rate are taken back straight away.
/// A rhythm far from it has to hold steady for <see cref="TimeToLockJump"/> first, since a real heart can't jump that
/// far that fast but a jostled strap can produce steady-looking junk for a few seconds. With no recent trusted rate
/// (just connected, or the beats were lost for a while) any rhythm that holds for <see cref="TimeToLock"/> is believed.
/// </remarks>
public sealed class BeatRateCalculator
{
    /// <summary>The median of this many beats smooths out normal beat-to-beat variation and any noise that slips through.</summary>
    private const int BeatsToAverage = 8;
    /// <summary>No believable beats for this long and the value is dropped rather than shown stale.</summary>
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(5);
    /// <summary>How long a new rhythm has to hold steady before it's believed, when there's no recent rate to compare it with.</summary>
    private static readonly TimeSpan TimeToLock = TimeSpan.FromSeconds(5);
    /// <summary>How long a rhythm far from the last trusted rate has to hold steady before it's believed.</summary>
    private static readonly TimeSpan TimeToLockJump = TimeSpan.FromSeconds(15);
    /// <summary>How long the last trusted rate is used to judge new beats against.</summary>
    private static readonly TimeSpan RememberFor = TimeSpan.FromSeconds(30);
    /// <summary>Beats near the last trusted rate are believed once this many agree.</summary>
    private const int BeatsToResume = 3;
    /// <summary>Intervals outside 30–220 bpm are noise, not heartbeats.</summary>
    private const double MinIntervalMs = 60000.0 / 220, MaxIntervalMs = 2000;
    /// <summary>How far an interval may be from the recent median before it's treated as noise.</summary>
    private const double MaxJump = 0.2;

    /// <summary>The beats currently believed; empty when there's no trusted rhythm.</summary>
    private readonly List<double> _intervals = new();
    private DateTime _lastBeat = DateTime.MinValue;
    /// <summary>The believed rhythm as of <see cref="_lastBeat"/>, kept after the beats are lost.</summary>
    private double _lastTrustedMs;

    /// <summary>A possible new rhythm: beats that didn't fit, as long as they agree with each other.</summary>
    private readonly List<double> _candidate = new();
    private DateTime _candidateStart, _lastCandidate = DateTime.MinValue;

    /// <summary>Takes in a sample's RR intervals and returns the heart rate they give, or null if there's no recent beat data.</summary>
    public int? Add(HeartRateSample sample)
    {
        if (sample.Time - _lastBeat > ForgetAfter)
            _intervals.Clear();
        if (sample.Time - _lastCandidate > ForgetAfter)
            _candidate.Clear();

        // The strap says its pads aren't touching skin: whatever it measured isn't a heartbeat.
        if (sample.SensorContact != false)
        {
            foreach (double rr in sample.RrIntervalsMs)
            {
                if (rr is >= MinIntervalMs and <= MaxIntervalMs)
                    AddInterval(rr, sample.Time);
            }
        }

        // At low heart rates some packets carry no interval; keep the last value until the beats go stale.
        if (_intervals.Count == 0)
            return null;
        return (int)Math.Round(60000 / Median(_intervals));
    }

    private void AddInterval(double rr, DateTime time)
    {
        if (_intervals.Count > 0 && Near(rr, Median(_intervals)))
        {
            _intervals.Add(rr);
            if (_intervals.Count > BeatsToAverage)
                _intervals.RemoveAt(0);
            _lastBeat = time;
            _lastTrustedMs = Median(_intervals);
            _candidate.Clear();
            return;
        }

        // Doesn't fit (or there's nothing to fit yet): see whether it's part of a steady new rhythm.
        if (_candidate.Count > 0 && !Near(rr, Median(_candidate)))
            _candidate.Clear();
        if (_candidate.Count == 0)
            _candidateStart = time;
        _candidate.Add(rr);
        if (_candidate.Count > BeatsToAverage)
            _candidate.RemoveAt(0);
        _lastCandidate = time;

        bool remembered = time - _lastBeat <= RememberFor;
        bool believable = remembered && Near(Median(_candidate), _lastTrustedMs)
            ? _candidate.Count >= BeatsToResume
            : time - _candidateStart >= (remembered ? TimeToLockJump : TimeToLock);
        if (believable)
        {
            _intervals.Clear();
            _intervals.AddRange(_candidate);
            _lastBeat = time;
            _lastTrustedMs = Median(_intervals);
            _candidate.Clear();
        }
    }

    private static bool Near(double value, double reference) => Math.Abs(value - reference) <= reference * MaxJump;

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public void Reset()
    {
        _intervals.Clear();
        _candidate.Clear();
        _lastBeat = _lastCandidate = DateTime.MinValue;
    }
}
