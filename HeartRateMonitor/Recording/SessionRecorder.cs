using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HeartRateMonitor.Events;
using HeartRateMonitor.Models;

namespace HeartRateMonitor.Recording;

/// <summary>
/// Writes a CSV as samples arrive (so a crash loses nothing) into a scratch folder.
/// <see cref="Save"/> moves it to the recordings folder, optionally with a TCX file
/// (importable by Strava, Garmin Connect, etc.); <see cref="Discard"/> throws it away.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly StreamWriter _csv;
    private StreamWriter? _events;
    private readonly List<HeartRateSample> _samples = [];
    private readonly DateTime _start;
    private bool _closed;

    public string CsvPath { get; }
    public DateTime Start => _start;
    public int SampleCount => _samples.Count;

    public SessionRecorder(string folder)
    {
        Directory.CreateDirectory(folder);
        _start = DateTime.Now;
        CsvPath = Path.Combine(folder, $"HeartRate_{_start:yyyy-MM-dd_HH-mm-ss}.csv");
        _csv = new StreamWriter(CsvPath, append: false, new UTF8Encoding(false));
        _csv.WriteLine("timestamp,elapsed_s,bpm,rr_intervals_ms,sensor_contact,calculated_bpm");
        _csv.Flush();
    }

    public void Add(HeartRateSample sample)
    {
        _samples.Add(sample);
        var inv = CultureInfo.InvariantCulture;
        string rr = string.Join(";", sample.RrIntervalsMs.Select(r => r.ToString("0.#", inv)));
        string contact = sample.SensorContact switch { true => "1", false => "0", null => "" };
        double elapsed = (sample.Time - _start).TotalSeconds;
        _csv.WriteLine(string.Create(inv, $"{sample.Time:yyyy-MM-ddTHH:mm:ss.fffzzz},{elapsed:0.0},{sample.Bpm},{rr},{contact},{sample.CalculatedBpm}"));
        _csv.Flush();
    }

    /// <summary>
    /// Appends an event to the <c>.events.jsonl</c> sidecar: one line per event, the JSON exactly as received
    /// plus <c>"received"</c>, the time the app stamped it with (what its spans are measured against).
    /// </summary>
    public void AddEvent(AppEvent e)
    {
        if (_closed)
            return;
        _events ??= new StreamWriter(Path.ChangeExtension(CsvPath, ".events.jsonl"), append: true, new UTF8Encoding(false));
        var line = (JsonObject)e.Json.DeepClone();
        line["received"] = e.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        _events.WriteLine(line.ToJsonString());
        _events.Flush();
    }

    /// <summary>Writes the <c>.spans.csv</c> summary: one row per song/course/… with heart rate stats.</summary>
    public void WriteSpans(IReadOnlyCollection<EventSpan> spans)
    {
        if (spans.Count == 0)
            return;
        var inv = CultureInfo.InvariantCulture;
        static string Csv(string? s) => s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        string? DataField(EventSpan s, string name) => s.BeginEvent.Data?[name]?.ToString();
        // The first player's chart, if the source sent players (StepMania does); handy for filtering in a spreadsheet.
        string? PlayerField(EventSpan s, string name) => (s.BeginEvent.Data?["players"] as JsonArray)?.FirstOrDefault()?[name]?.ToString();

        var sb = new StringBuilder();
        sb.AppendLine("id,parent,type,source,label,start,end,duration_s,result,avg_bpm,min_bpm,max_bpm,title,artist,pack,difficulty,meter,data");
        foreach (var s in spans.OrderBy(s => s.Start))
        {
            double? duration = s.End is { } end ? (end - s.Start).TotalSeconds : null;
            sb.AppendLine(string.Join(",",
                Csv(s.Id), Csv(s.Parent), Csv(s.Type), Csv(s.Source), Csv(s.Label),
                s.Start.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", inv),
                s.End?.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", inv) ?? "",
                duration?.ToString("0.0", inv) ?? "",
                Csv(s.Result ?? (s.IsOpen ? "in progress" : null)),
                s.AvgBpm?.ToString(inv) ?? "", s.MinBpm?.ToString(inv) ?? "", s.MaxBpm?.ToString(inv) ?? "",
                Csv(DataField(s, "title")), Csv(DataField(s, "artist")), Csv(DataField(s, "pack")),
                Csv(PlayerField(s, "difficulty")), Csv(PlayerField(s, "meter")),
                Csv(s.BeginEvent.Data?.ToJsonString())));
        }
        File.WriteAllText(Path.ChangeExtension(CsvPath, ".spans.csv"), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Finishes the recording and moves its files into <paramref name="folder"/>. Returns the files' new paths.</summary>
    public IReadOnlyList<string> Save(string folder, bool writeTcx)
    {
        Close();
        if (writeTcx && _samples.Count > 0)
            WriteTcx(Path.ChangeExtension(CsvPath, ".tcx"));

        Directory.CreateDirectory(folder);
        var saved = new List<string>();
        foreach (var file in OwnFiles(CsvPath))
        {
            string target = Path.Combine(folder, Path.GetFileName(file));
            File.Move(file, target, overwrite: true);
            saved.Add(target);
        }
        return saved;
    }

    /// <summary>Finishes the recording and deletes its files.</summary>
    public void Discard()
    {
        Close();
        foreach (var file in OwnFiles(CsvPath))
            File.Delete(file);
    }

    /// <summary>The CSV and every sidecar file named after it (.tcx, .events.jsonl, …).</summary>
    public static IEnumerable<string> OwnFiles(string csvPath) =>
        Directory.EnumerateFiles(Path.GetDirectoryName(csvPath)!, Path.GetFileNameWithoutExtension(csvPath) + ".*").ToList();

    private void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _csv.Dispose();
        _events?.Dispose();
    }

    private void WriteTcx(string path)
    {
        XNamespace ns = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
        static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        static XElement Bpm(XNamespace ns, string name, int value) => new(ns + name, new XElement(ns + "Value", value));

        var end = _samples[^1].Time;
        // Element order matters: TCX is validated against an XSD sequence.
        var lap = new XElement(ns + "Lap", new XAttribute("StartTime", Utc(_start)),
            new XElement(ns + "TotalTimeSeconds", ((end - _start).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture)),
            new XElement(ns + "DistanceMeters", 0),
            new XElement(ns + "Calories", 0),
            Bpm(ns, "AverageHeartRateBpm", (int)Math.Round(_samples.Average(s => s.Bpm))),
            Bpm(ns, "MaximumHeartRateBpm", _samples.Max(s => s.Bpm)),
            new XElement(ns + "Intensity", "Active"),
            new XElement(ns + "TriggerMethod", "Manual"),
            new XElement(ns + "Track",
                _samples.Select(s => new XElement(ns + "Trackpoint",
                    new XElement(ns + "Time", Utc(s.Time)),
                    Bpm(ns, "HeartRateBpm", s.Bpm)))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "TrainingCenterDatabase",
                new XElement(ns + "Activities",
                    new XElement(ns + "Activity", new XAttribute("Sport", "Other"),
                        new XElement(ns + "Id", Utc(_start)),
                        lap))));
        doc.Save(path);
    }

    /// <summary>Closes the files but leaves them in the scratch folder, to be recovered on the next start.</summary>
    public void Dispose() => Close();
}
