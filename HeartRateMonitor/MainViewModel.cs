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

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), _dispatcher);
    }

    // --- Live display ---
    private int? _currentBpm;
    private bool _isStale = true;
    private HeartRateZone? _currentZone;
    private string _currentColor = "#FFFFFF";

    public int? CurrentBpm { get => _currentBpm; private set { if (SetField(ref _currentBpm, value)) OnPropertyChanged(nameof(DisplayText)); } }
    public bool IsStale { get => _isStale; private set { if (SetField(ref _isStale, value)) OnPropertyChanged(nameof(DisplayText)); } }
    public string DisplayText => IsStale || CurrentBpm is null ? "--" : CurrentBpm.Value.ToString();
    public HeartRateZone? CurrentZone { get => _currentZone; private set { if (SetField(ref _currentZone, value)) OnPropertyChanged(nameof(ZoneName)); } }
    public string ZoneName => IsStale ? "" : CurrentZone?.Name ?? "";
    /// <summary>The color the BPM number should be right now.</summary>
    public string CurrentColor { get => _currentColor; private set => SetField(ref _currentColor, value); }

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
        CurrentBpm = null;
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
        Samples.Add(sample);
        _recorder?.Add(sample);
        CurrentBpm = sample.Bpm;
        IsStale = false;
        RefreshColor();
        UpdateStats();
    }

    private void Tick()
    {
        IsStale = DateTime.Now - _lastSampleTime > StaleAfter;
        RefreshColor();
        if (_recorder is not null)
            RecordingText = $"Recording {DateTime.Now - _recorder.Start:hh\\:mm\\:ss}";
    }

    private void RefreshColor()
    {
        CurrentZone = CurrentBpm is { } bpm ? HeartRateZone.Find(Settings.Zones, bpm) : null;
        OnPropertyChanged(nameof(ZoneName));
        CurrentColor = IsStale
            ? "#808080"
            : Settings.ColorMode == BpmColorMode.Zone && CurrentZone is not null ? CurrentZone.Color : Settings.FixedColor;
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
            RefreshColor();
        else if (e.PropertyName is nameof(AppSettings.AutoConnect) && Settings.AutoConnect && State == SourceState.Idle)
            Reconnect();
    }

    private void OnZonesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (HeartRateZone zone in e.OldItems ?? Array.Empty<HeartRateZone>())
            zone.PropertyChanged -= OnZoneChanged;
        foreach (HeartRateZone zone in e.NewItems ?? Array.Empty<HeartRateZone>())
            zone.PropertyChanged += OnZoneChanged;
        RefreshColor();
    }

    private void OnZoneChanged(object? sender, PropertyChangedEventArgs e) => RefreshColor();

    public void Dispose()
    {
        _clock.Stop();
        StopRecording();
        _source?.Dispose();
    }
}
