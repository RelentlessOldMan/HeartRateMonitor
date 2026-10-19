using System.Collections.ObjectModel;
using HeartRateMonitor.Models;

namespace HeartRateMonitor.Events;

/// <summary>A stretch of time between a "begin" event and its "end" (a song, a course, …), with heart rate stats for it.</summary>
public sealed class EventSpan : ObservableObject
{
    private DateTime? _end;
    private int? _avgBpm, _minBpm, _maxBpm;
    private string? _result;

    public required string Id { get; init; }
    public string? Parent { get; init; }
    public required string Type { get; init; }
    public string? Source { get; init; }
    public required string Label { get; init; }
    public required DateTime Start { get; init; }
    /// <summary>0 for top-level spans, 1 for spans inside them (songs in a course), …</summary>
    public int Depth { get; init; }
    public required AppEvent BeginEvent { get; init; }
    public AppEvent? EndEvent { get; private set; }
    /// <summary>Marks (pauses, …) that named this span as their parent.</summary>
    public List<AppEvent> Marks { get; } = [];

    public DateTime? End { get => _end; private set { if (SetField(ref _end, value)) OnPropertyChanged(nameof(DurationText)); } }
    public bool IsOpen => End is null;
    public string? Result { get => _result; private set => SetField(ref _result, value); }

    public int? AvgBpm { get => _avgBpm; private set => SetField(ref _avgBpm, value); }
    public int? MinBpm { get => _minBpm; private set => SetField(ref _minBpm, value); }
    public int? MaxBpm { get => _maxBpm; private set => SetField(ref _maxBpm, value); }
    public int SampleCount { get; private set; }

    public string StartText => Start.ToString("HH:mm:ss");
    public string DisplayLabel => new string(' ', Depth * 4) + Label;
    public string DurationText => (End ?? DateTime.Now) - Start is var d && d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    public void Close(DateTime time, AppEvent? endEvent, string? result)
    {
        End = time < Start ? Start : time;
        EndEvent = endEvent;
        Result = result;
        OnPropertyChanged(nameof(IsOpen));
    }

    /// <summary>Recomputes the stats from the samples inside the span. Samples without skin contact are skipped.</summary>
    public void UpdateStats(IList<HeartRateSample> samples)
    {
        var end = End ?? DateTime.MaxValue;
        int count = 0, min = int.MaxValue, max = int.MinValue;
        long sum = 0;
        for (int i = FirstIndexAtOrAfter(samples, Start); i < samples.Count && samples[i].Time <= end; i++)
        {
            var s = samples[i];
            if (s.Bpm <= 0 || s.SensorContact == false)
                continue;
            count++;
            sum += s.Bpm;
            min = Math.Min(min, s.Bpm);
            max = Math.Max(max, s.Bpm);
        }
        SampleCount = count;
        AvgBpm = count > 0 ? (int)Math.Round((double)sum / count) : null;
        MinBpm = count > 0 ? min : null;
        MaxBpm = count > 0 ? max : null;
        if (IsOpen)
            OnPropertyChanged(nameof(DurationText));
    }

    private static int FirstIndexAtOrAfter(IList<HeartRateSample> samples, DateTime time)
    {
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

/// <summary>Pairs begin/end events into <see cref="EventSpan"/>s.</summary>
public sealed class SpanTracker
{
    private int _autoId;

    public ObservableCollection<EventSpan> Spans { get; } = [];

    /// <summary>Applies an event. Returns the span it opened, closed or marked, if any.</summary>
    public EventSpan? Apply(AppEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.Begin:
            {
                // A begin that repeats an open span's id, or a new span of the same type from the same
                // program alongside an open one, means the old one never got its end (game crashed, …).
                foreach (var stale in Spans.Where(s => s.IsOpen &&
                             (s.Id == e.Id || (s.Type == e.Type && s.Source == e.Source && s.Parent == e.Parent))).ToList())
                    stale.Close(e.Time, null, "no end received");

                var parent = e.Parent is null ? null : Spans.LastOrDefault(s => s.Id == e.Parent);
                var span = new EventSpan
                {
                    Id = e.Id ?? $"auto-{++_autoId}",
                    Parent = e.Parent,
                    Type = e.Type,
                    Source = e.Source,
                    Label = e.Label ?? e.Type,
                    Start = e.Time,
                    Depth = parent is null ? 0 : parent.Depth + 1,
                    BeginEvent = e,
                };
                // Children go right after their parent's last descendant, so the list reads as a tree.
                int index = Spans.Count;
                if (parent is not null)
                {
                    index = Spans.IndexOf(parent) + 1;
                    while (index < Spans.Count && Spans[index].Depth > parent.Depth)
                        index++;
                }
                Spans.Insert(index, span);
                return span;
            }
            case EventKind.End:
            {
                var span = (e.Id is null ? null : Spans.LastOrDefault(s => s.IsOpen && s.Id == e.Id))
                           ?? Spans.LastOrDefault(s => s.IsOpen && s.Type == e.Type && s.Source == e.Source);
                if (span is null)
                    return null;
                // Ending a parent ends whatever is still open inside it.
                foreach (var child in Spans.Where(s => s.IsOpen && s.Parent == span.Id).ToList())
                    child.Close(e.Time, null, "no end received");
                string? result = e.Data?["result"]?.ToString();
                var time = e.Time;
                // "late": the sender only noticed the end afterwards (e.g. backed out of a song in a way the game
                // didn't report), so its time is wrong. Best guess: the last pause, else the song's own length.
                if (e.Data?["late"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
                {
                    var lengthS = span.BeginEvent.Data?["length_s"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number
                        ? span.BeginEvent.Data["length_s"]!.GetValue<double>() : (double?)null;
                    time = span.Marks.LastOrDefault()?.Time
                           ?? (lengthS is { } l && span.Start.AddSeconds(l) < time ? span.Start.AddSeconds(l) : time);
                }
                span.Close(time, e, result);
                return span;
            }
            default:
            {
                var span = e.Parent is null ? null : Spans.LastOrDefault(s => s.Id == e.Parent);
                span?.Marks.Add(e);
                return span;
            }
        }
    }

    /// <summary>Closes anything still open, e.g. when a recording is saved.</summary>
    public void CloseAll(DateTime time)
    {
        foreach (var span in Spans.Where(s => s.IsOpen))
            span.Close(time, null, "recording ended");
    }
}
