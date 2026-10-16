using System.Globalization;
using System.Text;
using System.Xml.Linq;
using HeartRateMonitor.Models;

namespace HeartRateMonitor.Recording;

/// <summary>
/// Writes a CSV as samples arrive (so a crash loses nothing) and optionally a TCX file
/// (importable by Strava, Garmin Connect, etc.) when the recording stops.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly StreamWriter _csv;
    private readonly List<HeartRateSample> _samples = [];
    private readonly DateTime _start;

    public string CsvPath { get; }
    public DateTime Start => _start;

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

    /// <summary>Finishes the recording. Returns the files written.</summary>
    public IReadOnlyList<string> Stop(bool writeTcx)
    {
        _csv.Dispose();
        var files = new List<string> { CsvPath };
        if (writeTcx && _samples.Count > 0)
        {
            string tcxPath = Path.ChangeExtension(CsvPath, ".tcx");
            WriteTcx(tcxPath);
            files.Add(tcxPath);
        }
        return files;
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

    public void Dispose() => _csv.Dispose();
}
