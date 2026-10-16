using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using HeartRateMonitor.Models;
using HeartRateMonitor.Recording;
using HeartRateMonitor.Sensors;

namespace HeartRateMonitor;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>No data for this long and the display falls back to "--".</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _clock;
    private IHeartRateSource? _source;
    private SessionRecorder? _recorder;
    private DateTime _lastSampleTime = DateTime.MinValue;

    public AppSettings Settings { get; }
    public bool IsDummy { get; }
    public ObservableCollection<HeartRateSample> Samples { get; } = [];

    public MainViewModel(AppSettings settings, bool dummy)
    {
        Settings = settings;
        IsDummy = dummy;
        Settings.PropertyChanged += OnSettingsChanged;
        Settings.Zones.CollectionChanged += OnZonesCollectionChanged;
        foreach (var zone in Settings.Zones)
            zone.PropertyChanged += OnZoneChanged;

        // Dummy data never touches the real daily stats on disk.
        if (!IsDummy)
            _today = DailyStats.Load(_today.Date);
        UpdateZoneTimes();

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), _dispatcher);
    }

    // --- Live display ---
    private readonly BeatRateCalculator _beatRate = new();
    private int? _strapBpm, _calculatedBpm;
    private bool _isStale = true;

    public bool IsStale { get => _isStale; private set => SetField(ref _isStale, value); }
    /// <summary>The heart rate the strap reports.</summary>
    public BpmReading Strap { get; } = new();
    /// <summary>The heart rate worked out from the beat-to-beat intervals.</summary>
    public BpmReading Calculated { get; } = new();
    /// <summary>The reading the main window's zone name follows: the calculated one if that's all it shows, else the strap's.</summary>
    public BpmReading MainLead => Settings.MainDisplay == HeartRateDisplay.Calculated ? Calculated : Strap;
    /// <summary>The reading the overlay's heart and zone name follow.</summary>
    public BpmReading OverlayLead => Settings.OverlayDisplay == HeartRateDisplay.Calculated ? Calculated : Strap;

    // --- Connection ---
    private SourceState _state;
    private string _statusText = "";
    private int? _batteryLevel;

    public SourceState State { get => _state; private set => SetField(ref _state, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public int? BatteryLevel { get => _batteryLevel; private set { if (SetField(ref _batteryLevel, value)) OnPropertyChanged(nameof(BatteryText)); } }
    public string BatteryText => BatteryLevel is { } b ? $"{b}%" : "–";

    // --- Session stats (since app start or last reset) ---
    private string _statsText = "";
    public string StatsText { get => _statsText; private set => SetField(ref _statsText, value); }

    // --- Today's time in each zone ---
    /// <summary>How often today's stats are written to disk while data is coming in (they're also saved on exit).</summary>
    private static readonly TimeSpan StatsSaveInterval = TimeSpan.FromSeconds(30);
    private DailyStats _today = new() { Date = DateOnly.FromDateTime(DateTime.Now) };
    private HeartRateSample? _lastTrackedSample;
    private DateTime _lastStatsSave = DateTime.Now;
    private IReadOnlyList<ZoneTime> _zoneTimes = [];
    private string _todayHeader = "";
    private string _todaySummary = "";

    public IReadOnlyList<ZoneTime> ZoneTimes { get => _zoneTimes; private set => SetField(ref _zoneTimes, value); }
    public string TodayHeader { get => _todayHeader; private set => SetField(ref _todayHeader, value); }
    public string TodaySummary { get => _todaySummary; private set => SetField(ref _todaySummary, value); }

    // --- Recording ---
    private string _recordingText = "Not recording";
    public bool IsRecording => _recorder is not null;
    public string RecordingText { get => _recordingText; private set => SetField(ref _recordingText, value); }

    public void Start()
    {
        StartSource(force: false);
        _clock.Start();
        Tick();
    }

    private void StartSource(bool force)
    {
        _source?.Dispose();
        _strapBpm = _calculatedBpm = null;
        _beatRate.Reset();
        RefreshReadings();
        BatteryLevel = null;

        if (IsDummy)
        {
            _source = new DummyHeartRateSource();
        }
        else
        {
            var ble = new BleHeartRateSource(Settings.DeviceAddress);
            ble.DeviceConnected += (address, name) => _dispatcher.BeginInvoke(() =>
            {
                Settings.DeviceAddress = address;
                Settings.DeviceName = name;
            });
            _source = ble;
        }

        var source = _source;
        // Events from a source we've since replaced are ignored.
        source.SampleReceived += s => _dispatcher.BeginInvoke(() => { if (_source == source) OnSample(s); });
        source.StateChanged += (st, text) => _dispatcher.BeginInvoke(() => { if (_source == source) { State = st; StatusText = text; } });
        source.BatteryLevelChanged += b => _dispatcher.BeginInvoke(() => { if (_source == source) BatteryLevel = b; });

        if (force || IsDummy || Settings.AutoConnect)
            source.Start();
        else
            (State, StatusText) = (SourceState.Idle, "Auto-connect is off");
    }

    /// <summary>Drop the current connection and try again (also used when auto-connect is off).</summary>
    public void Reconnect()
    {
        Log.Info("User: reconnect");
        StartSource(force: true);
    }

    /// <summary>Forget the remembered strap; the next strap found gets remembered instead.</summary>
    public void ForgetDevice()
    {
        Log.Info($"User: forget strap {Settings.DeviceName}");
        Settings.DeviceAddress = null;
        Settings.DeviceName = null;
        Reconnect();
    }

    internal void OnSample(HeartRateSample sample)
    {
        _lastSampleTime = DateTime.Now;
        sample = sample with { CalculatedBpm = _beatRate.Add(sample) };
        Samples.Add(sample);
        _recorder?.Add(sample);
        _strapBpm = sample.Bpm;
        _calculatedBpm = sample.CalculatedBpm;
        IsStale = false;
        RefreshReadings();
        UpdateStats();
        TrackDaily(sample);
    }

    private void Tick()
    {
        IsStale = DateTime.Now - _lastSampleTime > StaleAfter;
        RefreshReadings();
        if (_recorder is not null)
            RecordingText = $"Recording {DateTime.Now - _recorder.Start:hh\\:mm\\:ss}";

        // Roll over at midnight even if no data is coming in.
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _today.Date)
            SwitchDay(today);
        if (_today.IsDirty && DateTime.Now - _lastStatsSave >= StatsSaveInterval)
            SaveDailyStats();
    }

    /// <summary>
    /// Credits the time since the previous sample to this sample's BPM. Gaps longer than
    /// <see cref="StaleAfter"/> (strap off, out of range, app closed) aren't counted.
    /// </summary>
    private void TrackDaily(HeartRateSample sample)
    {
        var previous = _lastTrackedSample;
        // No skin contact means the BPM is meaningless.
        if (sample.SensorContact == false || sample.Bpm <= 0)
        {
            _lastTrackedSample = null;
            return;
        }
        _lastTrackedSample = sample;
        if (previous is null)
            return;

        double seconds = (sample.Time - previous.Time).TotalSeconds;
        if (seconds <= 0 || seconds > StaleAfter.TotalSeconds)
            return;

        var day = DateOnly.FromDateTime(sample.Time);
        if (day != _today.Date)
            SwitchDay(day);
        _today.Add(sample.Bpm, seconds);
        UpdateZoneTimes();
    }

    private void SwitchDay(DateOnly day)
    {
        SaveDailyStats();
        Log.Info($"Daily stats: new day {day:yyyy-MM-dd}");
        _today = IsDummy ? new DailyStats { Date = day } : DailyStats.Load(day);
        UpdateZoneTimes();
    }

    private void SaveDailyStats()
    {
        _lastStatsSave = DateTime.Now;
        if (IsDummy || !_today.IsDirty)
            return;
        try
        {
            _today.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save daily stats", ex);
        }
    }

    /// <summary>Sorts today's time-per-BPM into the current zones.</summary>
    private void UpdateZoneTimes()
    {
        TodayHeader = $"Today, {_today.Date:dddd d MMMM}";
        var zones = Settings.Zones.OrderBy(z => z.MinBpm).ToList();
        var seconds = new double[zones.Count + 1]; // [0] = below the lowest zone
        double total = 0, weightedBpm = 0;
        foreach (var (bpm, s) in _today.SecondsByBpm)
        {
            int zone = zones.FindLastIndex(z => z.MinBpm <= bpm);
            seconds[zone + 1] += s;
            total += s;
            weightedBpm += bpm * s;
        }

        var rows = new List<ZoneTime>();
        double Fraction(double s) => total > 0 ? s / total : 0;
        // Only show "below zones" when there's time in it, so the list doesn't open with an empty row.
        if (seconds[0] > 0 || zones.Count == 0)
            rows.Add(new ZoneTime(zones.Count > 0 ? "Below zones" : "No zones", Settings.FixedColor,
                zones.Count > 0 ? $"< {zones[0].MinBpm}" : "", TimeSpan.FromSeconds(seconds[0]), Fraction(seconds[0])));
        for (int i = 0; i < zones.Count; i++)
        {
            string range = i + 1 < zones.Count ? $"{zones[i].MinBpm}–{zones[i + 1].MinBpm - 1}" : $"{zones[i].MinBpm}+";
            rows.Add(new ZoneTime(zones[i].Name, zones[i].Color, range, TimeSpan.FromSeconds(seconds[i + 1]), Fraction(seconds[i + 1])));
        }
        ZoneTimes = rows;

        TodaySummary = total > 0
            ? $"Tracked {ZoneTime.FormatDuration(TimeSpan.FromSeconds(total))}   Avg {weightedBpm / total:0}   Max {_today.SecondsByBpm.Keys.Max()}"
            : "No heart rate data yet today";
    }

    private void RefreshReadings()
    {
        Strap.Update(_strapBpm, IsStale, Settings);
        Calculated.Update(_calculatedBpm, IsStale, Settings);
    }

    private void UpdateStats()
    {
        if (Samples.Count == 0)
        {
            StatsText = "No data yet";
            return;
        }
        var duration = Samples[^1].Time - Samples[0].Time;
        StatsText = $"Avg {Samples.Average(s => s.Bpm):0}   Min {Samples.Min(s => s.Bpm)}   Max {Samples.Max(s => s.Bpm)}   Time {duration:hh\\:mm\\:ss}";
    }

    public void ResetSession()
    {
        Samples.Clear();
        UpdateStats();
    }

    public void StartRecording()
    {
        if (_recorder is not null)
            return;
        _recorder = new SessionRecorder(Settings.RecordingFolder);
        OnPropertyChanged(nameof(IsRecording));
        Tick();
    }

    /// <summary>Returns the files written.</summary>
    public IReadOnlyList<string> StopRecording()
    {
        if (_recorder is null)
            return [];
        var files = _recorder.Stop(Settings.WriteTcx);
        _recorder = null;
        OnPropertyChanged(nameof(IsRecording));
        RecordingText = $"Saved {Path.GetFileName(files[0])}";
        return files;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ColorMode) or nameof(AppSettings.FixedColor))
        {
            RefreshReadings();
            UpdateZoneTimes();
        }
        else if (e.PropertyName is nameof(AppSettings.MainDisplay))
            OnPropertyChanged(nameof(MainLead));
        else if (e.PropertyName is nameof(AppSettings.OverlayDisplay))
            OnPropertyChanged(nameof(OverlayLead));
        else if (e.PropertyName is nameof(AppSettings.AutoConnect) && Settings.AutoConnect && State == SourceState.Idle)
            Reconnect();
    }

    private void OnZonesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (HeartRateZone zone in e.OldItems ?? Array.Empty<HeartRateZone>())
            zone.PropertyChanged -= OnZoneChanged;
        foreach (HeartRateZone zone in e.NewItems ?? Array.Empty<HeartRateZone>())
            zone.PropertyChanged += OnZoneChanged;
        RefreshReadings();
        UpdateZoneTimes();
    }

    private void OnZoneChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshReadings();
        UpdateZoneTimes();
    }

    public void Dispose()
    {
        _clock.Stop();
        SaveDailyStats();
        StopRecording();
        _source?.Dispose();
    }
}
