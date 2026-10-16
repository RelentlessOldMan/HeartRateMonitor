using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeartRateMonitor.Models;

/// <summary>
/// Seconds spent at each BPM over one calendar day. Persisted as JSON in %AppData%\HeartRateMonitor\daily,
/// one file per day. Kept per BPM rather than per zone so the zone breakdown is still right after the zones change.
/// </summary>
public sealed class DailyStats
{
    public static string Folder { get; } = Path.Combine(AppSettings.SettingsFolder, "daily");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DateOnly Date { get; set; }
    public Dictionary<int, double> SecondsByBpm { get; set; } = [];

    /// <summary>Changed since the last load or save.</summary>
    [JsonIgnore] public bool IsDirty { get; private set; }

    public void Add(int bpm, double seconds)
    {
        SecondsByBpm[bpm] = SecondsByBpm.GetValueOrDefault(bpm) + seconds;
        IsDirty = true;
    }

    private static string PathFor(DateOnly date) => Path.Combine(Folder, $"{date:yyyy-MM-dd}.json");

    public static DailyStats Load(DateOnly date)
    {
        try
        {
            string path = PathFor(date);
            if (File.Exists(path) && JsonSerializer.Deserialize<DailyStats>(File.ReadAllText(path), JsonOptions) is { } stats)
            {
                stats.Date = date;
                return stats;
            }
        }
        catch (Exception ex)
        {
            // Corrupt file: start the day fresh rather than refusing to start.
            Log.Error($"Couldn't read daily stats for {date:yyyy-MM-dd}", ex);
        }
        return new DailyStats { Date = date };
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string path = PathFor(Date);
        // Write then swap, so a crash mid-write can't leave a half-written file behind.
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
        IsDirty = false;
    }
}

/// <summary>One row of the Stats tab: time spent in a zone (or below all zones).</summary>
public sealed record ZoneTime(string Name, string Color, string Range, TimeSpan Time, double Fraction)
{
    public string TimeText => FormatDuration(Time);
    public string PercentText => $"{Fraction:P0}";

    public static string FormatDuration(TimeSpan t) => $"{(int)t.TotalHours}:{t:mm\\:ss}";
}
