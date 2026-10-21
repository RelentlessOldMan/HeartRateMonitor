namespace HeartRateMonitor.Models;

/// <summary>Which heart rate number(s) to show.</summary>
public enum HeartRateDisplay { Strap, Calculated, Both }

/// <summary>One live heart rate number as displayed: the strap's own, or the one calculated from beat timings.</summary>
public sealed class BpmReading : ObservableObject
{
    private int? _bpm;
    private string _text = "--";
    private string _color = "#808080";
    private string _zoneName = "";
    private bool _isFrozen;

    /// <summary>The BPM, or null when there's no current value (no data, or the data has gone stale).</summary>
    public int? Bpm { get => _bpm; private set => SetField(ref _bpm, value); }
    public string Text { get => _text; private set => SetField(ref _text, value); }
    /// <summary>The color the number should be right now.</summary>
    public string Color { get => _color; private set => SetField(ref _color, value); }
    public string ZoneName { get => _zoneName; private set => SetField(ref _zoneName, value); }
    /// <summary>
    /// Still arriving but not measured: the strap has sent no beats for a while and keeps repeating its last number.
    /// It's shown greyed out rather than hidden, as the last thing the strap knew.
    /// </summary>
    public bool IsFrozen { get => _isFrozen; private set => SetField(ref _isFrozen, value); }

    internal void Update(int? bpm, bool stale, bool frozen, AppSettings settings)
    {
        int? shown = stale ? null : bpm;
        Bpm = shown;
        IsFrozen = shown is not null && frozen;
        Text = shown?.ToString() ?? "--";
        var zone = shown is { } b ? HeartRateZone.Find(settings.Zones, b) : null;
        ZoneName = zone?.Name ?? "";
        Color = shown is null || IsFrozen
            ? "#808080"
            : settings.ColorMode == BpmColorMode.Zone && zone is not null ? zone.Color : settings.FixedColor;
    }
}
