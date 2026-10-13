namespace HeartRateMonitor.Models;

/// <summary>A heart rate zone starts at <see cref="MinBpm"/> and runs up to the next zone's minimum.</summary>
public sealed class HeartRateZone : ObservableObject
{
    private string _name = "";
    private int _minBpm;
    private string _color = "#FFFFFF";

    public string Name { get => _name; set => SetField(ref _name, value); }
    public int MinBpm { get => _minBpm; set => SetField(ref _minBpm, value); }
    public string Color { get => _color; set => SetField(ref _color, value); }

    /// <summary>Classic 5-zone model at 50/60/70/80/90% of max heart rate.</summary>
    public static List<HeartRateZone> CreateDefaults(int maxHeartRate) =>
    [
        new() { Name = "Z1 Warm up",  MinBpm = (int)Math.Round(maxHeartRate * 0.5), Color = "#9E9E9E" },
        new() { Name = "Z2 Easy",     MinBpm = (int)Math.Round(maxHeartRate * 0.6), Color = "#2196F3" },
        new() { Name = "Z3 Aerobic",  MinBpm = (int)Math.Round(maxHeartRate * 0.7), Color = "#4CAF50" },
        new() { Name = "Z4 Threshold",MinBpm = (int)Math.Round(maxHeartRate * 0.8), Color = "#FF9800" },
        new() { Name = "Z5 Maximum",  MinBpm = (int)Math.Round(maxHeartRate * 0.9), Color = "#F44336" },
    ];

    /// <summary>The highest zone whose minimum is at or below <paramref name="bpm"/>, or null if below all zones.</summary>
    public static HeartRateZone? Find(IEnumerable<HeartRateZone> zones, int bpm) =>
        zones.Where(z => z.MinBpm <= bpm).MaxBy(z => z.MinBpm);
}
