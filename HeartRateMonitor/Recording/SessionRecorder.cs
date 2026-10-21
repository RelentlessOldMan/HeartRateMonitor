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
/// The one place that knows the file formats: <see cref="Load"/> reads them back, and an edited
/// recording is written by feeding it through a recorder again (<see cref="Write"/>).
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly StreamWriter _csv;
    private StreamWriter? _events;
    private readonly List<HeartRateSample> _samples = [];
    private readonly DateTime _start;
    private bool _closed;

    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    public string CsvPath { get; }
    public DateTime Start => _start;
    public int SampleCount => _samples.Count;

    /// <param name="start">When the recording began (elapsed times count from it); now if not given.</param>
    /// <param name="name">File name without extension; named after <paramref name="start"/> if not given.</param>
    public SessionRecorder(string folder, DateTime? start = null, string? name = null)
    {
        Directory.CreateDirectory(folder);
        _start = start ?? DateTime.Now;
        CsvPath = Path.Combine(folder, (name ?? $"HeartRate_{_start:yyyy-MM-dd_HH-mm-ss}") + ".csv");
        // Leftovers from an earlier write under the same name; the events file is appended to.
        foreach (var file in OwnFiles(CsvPath))
            File.Delete(file);
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
        _csv.WriteLine(string.Create(inv, $"{sample.Time.ToString(TimeFormat, inv)},{elapsed:0.0},{sample.Bpm},{rr},{contact},{sample.CalculatedBpm}"));
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
        line["received"] = e.Time.ToString(TimeFormat, CultureInfo.InvariantCulture);
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
                s.Start.ToString(TimeFormat, inv),
                s.End?.ToString(TimeFormat, inv) ?? "",
                duration?.ToString("0.0", inv) ?? "",
                Csv(s.Result ?? (s.IsOpen ? "in progress" : null)),
                s.AvgBpm?.ToString(inv) ?? "", s.MinBpm?.ToString(inv) ?? "", s.MaxBpm?.ToString(inv) ?? "",
                Csv(DataField(s, "title")), Csv(DataField(s, "artist")), Csv(DataField(s, "pack")),
                Csv(PlayerField(s, "difficulty")), Csv(PlayerField(s, "meter")),
                Csv(s.Data?.ToJsonString())));
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

    /// <summary>
    /// Writes a whole session out at once, the same way a live recording is written, and moves the files into
    /// <paramref name="folder"/> (replacing any of the same name). Returns the files' paths.
    /// </summary>
    public static IReadOnlyList<string> Write(Session session, string scratchFolder, string folder, DateTime start, string name, bool writeTcx)
    {
        using var recorder = new SessionRecorder(scratchFolder, start, name);
        foreach (var sample in session.Samples)
            recorder.Add(sample);
        foreach (var e in session.Events)
            recorder.AddEvent(e);
        recorder.WriteSpans(session.Spans);
        return recorder.Save(folder, writeTcx);
    }

    /// <summary>Reads a recording back: its samples and events, and when it started.</summary>
    public static (DateTime Start, List<HeartRateSample> Samples, List<AppEvent> Events) Load(string csvPath)
    {
        var inv = CultureInfo.InvariantCulture;
        static DateTime Time(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).LocalDateTime;
        var samples = new List<HeartRateSample>();
        DateTime? start = null;
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            // timestamp,elapsed_s,bpm,rr_intervals_ms,sensor_contact,calculated_bpm
            var f = line.Split(',');
            if (f.Length < 6 || !int.TryParse(f[2], inv, out int bpm))
                continue;
            var time = Time(f[0]);
            start ??= time.AddSeconds(-double.Parse(f[1], inv));
            var rr = f[3].Length == 0 ? [] : f[3].Split(';').Select(r => double.Parse(r, inv)).ToList();
            bool? contact = f[4] switch { "1" => true, "0" => false, _ => null };
            samples.Add(new HeartRateSample(time, bpm, rr, contact)
            {
                CalculatedBpm = int.TryParse(f[5], inv, out int calculated) ? calculated : null,
            });
        }

        var events = new List<AppEvent>();
        var eventsPath = Path.ChangeExtension(csvPath, ".events.jsonl");
        if (File.Exists(eventsPath))
        {
            foreach (var line in File.ReadLines(eventsPath))
            {
                // A crash can leave a half-written last line; skip it like the CSV skips its bad lines.
                if (TryParse(line) is not JsonObject json || json["received"]?.ToString() is not { } received)
                    continue;
                json.Remove("received");
                if (AppEvent.Parse(json.ToJsonString(), Time(received), out _) is { } e)
                    events.Add(e);
            }
        }
        return (start ?? File.GetCreationTime(csvPath), samples, events);

        static JsonNode? TryParse(string line)
        {
            try
            {
                return JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
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
        static bool Valid(HeartRateSample s) => s.Bpm > 0 && s.SensorContact != false;
        var valid = _samples.Where(Valid).ToList();
        // Element order matters: TCX is validated against an XSD sequence.
        var lap = new XElement(ns + "Lap", new XAttribute("StartTime", Utc(_start)),
            new XElement(ns + "TotalTimeSeconds", ((end - _start).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture)),
            new XElement(ns + "DistanceMeters", 0),
            new XElement(ns + "Calories", 0),
            valid.Count == 0 ? null : Bpm(ns, "AverageHeartRateBpm", (int)Math.Round(valid.Average(s => s.Bpm))),
            valid.Count == 0 ? null : Bpm(ns, "MaximumHeartRateBpm", valid.Max(s => s.Bpm)),
            new XElement(ns + "Intensity", "Active"),
            new XElement(ns + "TriggerMethod", "Manual"),
            new XElement(ns + "Track",
                _samples.Select(s => new XElement(ns + "Trackpoint",
                    new XElement(ns + "Time", Utc(s.Time)),
                    Valid(s) ? Bpm(ns, "HeartRateBpm", s.Bpm) : null))));

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
