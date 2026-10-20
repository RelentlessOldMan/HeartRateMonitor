using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using HeartRateMonitor.Events;
using HeartRateMonitor.Models;

namespace HeartRateMonitor;

/// <summary>
/// One run's heart rate samples and events, with the spans (songs, …) and stats worked out from them.
/// The live session and a recording opened in the viewer are both one of these, shown by the same controls;
/// spans are always rebuilt from the events, never stored separately.
/// </summary>
public sealed class Session : ObservableObject
{
    private SpanTracker _tracker;
    private readonly List<AppEvent> _events = [];
    private string _strapStatsText = "", _calculatedStatsText = "", _timeText = "", _selectionText = "";
    private DateTime? _selectionStart, _selectionEnd;
    private EventSpan? _selectedSpan;

    public Session(AppSettings settings)
    {
        Settings = settings;
        _tracker = new SpanTracker(Spans);
    }

    public AppSettings Settings { get; }
    public ObservableCollection<HeartRateSample> Samples { get; } = [];
    /// <summary>Songs, courses, … the events describe, in tree order.</summary>
    public ObservableCollection<EventSpan> Spans { get; } = [];
    /// <summary>Every event, as received, in order.</summary>
    public IReadOnlyList<AppEvent> Events => _events;

    public string StrapStatsText { get => _strapStatsText; private set => SetField(ref _strapStatsText, value); }
    public string CalculatedStatsText { get => _calculatedStatsText; private set => SetField(ref _calculatedStatsText, value); }
    public string TimeText { get => _timeText; private set => SetField(ref _timeText, value); }

    public void AddSample(HeartRateSample sample)
    {
        Samples.Add(sample);
        UpdateStats();
        foreach (var span in Spans)
            if (span.Start <= sample.Time && (span.End ?? DateTime.MaxValue) >= sample.Time)
                span.UpdateStats(Samples);
    }

    public void AddEvent(AppEvent e)
    {
        _events.Add(e);
        var span = _tracker.Apply(e);
        // Closing a span can close others (its children, or a stale one), and a begin can add segments,
        // so refresh every span that ends from here on.
        foreach (var s in Spans)
            if (s == span || s.End >= e.Time)
                s.UpdateStats(Samples);
    }

    /// <summary>Replaces everything with the given samples and events (opening a recording, or after an edit).</summary>
    public void Load(IEnumerable<HeartRateSample> samples, IEnumerable<AppEvent> events)
    {
        var sampleList = samples.ToList();
        var eventList = events.ToList();
        ClearSelection();
        Samples.Clear();
        foreach (var sample in sampleList)
            Samples.Add(sample);
        _events.Clear();
        NewTracker();
        foreach (var e in eventList)
        {
            _events.Add(e);
            _tracker.Apply(e);
        }
        foreach (var span in Spans)
            span.UpdateStats(Samples);
        UpdateStats();
    }

    public void Clear() => Load([], []);

    /// <summary>Spans that overlap the given time range, for writing alongside a recording of it.</summary>
    public List<EventSpan> SpansBetween(DateTime from, DateTime to) =>
        Spans.Where(s => s.Start < to && (s.End ?? DateTime.MaxValue) >= from).ToList();

    /// <summary>
    /// Removes the samples and events in a time range. Times after it stay as they were (real clock time),
    /// so the graph shows a gap. Spans whose begin was cut go too, along with everything that refers to them,
    /// except ones that carry on past the cut (a course whose first song is cut): those now begin where it ends.
    /// </summary>
    public void Cut(DateTime from, DateTime to)
    {
        static bool Inside(DateTime t, DateTime from, DateTime to) => t >= from && t <= to;
        var endsAfter = _events.Where(e => e.Kind == EventKind.End && e.Id is not null && e.Time > to).Select(e => e.Id!).ToHashSet();
        // Recordings keep times to the millisecond, so this is the first moment after the cut that survives a save.
        var resume = to.AddMilliseconds(1);
        var events = new List<AppEvent>();
        var cutIds = new HashSet<string>();
        foreach (var e in _events)
        {
            if (!Inside(e.Time, from, to))
                events.Add(e);
            else if (e.Kind == EventKind.Begin && e.Id is not null && endsAfter.Contains(e.Id))
                events.Add(Moved(e, resume));
            else if (e.Kind == EventKind.Begin && e.Id is not null)
                cutIds.Add(e.Id);   // An end or mark left behind for it would otherwise attach to some other span.
        }
        while (cutIds.Count > 0)
        {
            var orphans = events.Where(e => (e.Kind == EventKind.End && e.Id is not null && cutIds.Contains(e.Id))
                                            || (e.Parent is not null && cutIds.Contains(e.Parent))).ToList();
            if (orphans.Count == 0)
                break;
            events.RemoveAll(orphans.Contains);
            // A song inside a cut course goes too, and so do its own ends and marks.
            cutIds.UnionWith(orphans.Where(e => e.Kind == EventKind.Begin && e.Id is not null).Select(e => e.Id!));
        }
        Load(Samples.Where(s => !Inside(s.Time, from, to)), events);
    }

    /// <summary>A begin moved later, with its music time moved along so its segments stay where they were.</summary>
    private static AppEvent Moved(AppEvent e, DateTime time)
    {
        var json = (JsonObject)e.Json.DeepClone();
        if (json["data"]?["segments"] is not null)
            json["music_seconds"] = (SpanTracker.Number(json["music_seconds"]) ?? 0) + (time - e.Time).TotalSeconds;
        return e with { Time = time, Json = json };
    }

    // --- Selection: a time range picked on the graph, or a span picked in the list ---

    public DateTime? SelectionStart { get => _selectionStart; set { if (SetField(ref _selectionStart, value)) OnSelectionChanged(); } }
    public DateTime? SelectionEnd { get => _selectionEnd; set { if (SetField(ref _selectionEnd, value)) OnSelectionChanged(); } }
    public bool HasSelection => SelectionStart is { } a && SelectionEnd is { } b && b > a;
    public string SelectionText { get => _selectionText; private set => SetField(ref _selectionText, value); }

    /// <summary>The span whose time range is selected, if the selection is exactly one.</summary>
    public EventSpan? SelectedSpan
    {
        get => _selectedSpan;
        set
        {
            if (value is null)
            {
                if (SetField(ref _selectedSpan, null))
                    ClearSelection();
                return;
            }
            Select(value.Start, value.EndOr(LastSampleTime));
        }
    }

    public void Select(DateTime from, DateTime to)
    {
        if (to < from)
            (from, to) = (to, from);
        _selectionStart = from;
        _selectionEnd = to;
        OnPropertyChanged(nameof(SelectionStart));
        OnPropertyChanged(nameof(SelectionEnd));
        OnSelectionChanged();
    }

    public void ClearSelection()
    {
        _selectionStart = _selectionEnd = null;
        OnPropertyChanged(nameof(SelectionStart));
        OnPropertyChanged(nameof(SelectionEnd));
        OnSelectionChanged();
    }

    private DateTime LastSampleTime => Samples.Count > 0 ? Samples[^1].Time : DateTime.Now;

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        // Keep the list's highlight in step: picking a span on the graph selects its row, dragging a range clears it.
        var match = HasSelection ? Spans.LastOrDefault(s => s.Start == SelectionStart && s.EndOr(LastSampleTime) == SelectionEnd) : null;
        SetField(ref _selectedSpan, match, nameof(SelectedSpan));

        if (!HasSelection)
        {
            SelectionText = "";
            return;
        }
        var (from, to) = (SelectionStart!.Value, SelectionEnd!.Value);
        var bpms = Samples.Where(s => s.Time >= from && s.Time <= to && s.Bpm > 0 && s.SensorContact != false).Select(s => s.Bpm).ToList();
        SelectionText = $"Selected {from:HH:mm:ss}–{to:HH:mm:ss} ({FormatTime(to - from)})" +
                        (bpms.Count > 0 ? $"   Avg {bpms.Average():0}   Min {bpms.Min()}   Max {bpms.Max()}" : "   no heart rate");
    }

    private void NewTracker()
    {
        // The tracker owns the collection it fills; keep handing out the same one so bindings stay put.
        Spans.Clear();
        _tracker = new SpanTracker(Spans);
    }

    private void UpdateStats()
    {
        static string Summary(IReadOnlyCollection<int> bpms) => bpms.Count == 0
            ? "No data yet"
            : $"Avg {bpms.Average():0}   Min {bpms.Min()}   Max {bpms.Max()}";

        StrapStatsText = Summary(Samples.Select(s => s.Bpm).ToList());
        CalculatedStatsText = Summary(Samples.Where(s => s.CalculatedBpm is not null).Select(s => s.CalculatedBpm!.Value).ToList());
        TimeText = Samples.Count == 0 ? "" : $"Time {FormatTime(Samples[^1].Time - Samples[0].Time)}";
    }

    private static string FormatTime(TimeSpan t) => t.ToString(@"hh\:mm\:ss");
}
