using System.Windows.Threading;

namespace HeartRateMonitor.Events;

/// <summary>
/// Picks up event files other programs drop into watched folders: one JSON object per <c>.json</c> file,
/// processed in filename order and deleted once read. Polls rather than using FileSystemWatcher, because
/// writers like StepMania's Lua create the file and fill it in separate steps, and polling also picks up
/// anything left over from before the app started.
/// </summary>
public sealed class EventInbox : IDisposable
{
    /// <summary>A file that still doesn't parse after this long (writer crashed mid-write, bad JSON) is moved aside.</summary>
    private static readonly TimeSpan BadFileGrace = TimeSpan.FromSeconds(10);

    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, DateTime> _firstFailure = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _undeletable = new(StringComparer.OrdinalIgnoreCase);

    public event Action<AppEvent>? EventReceived;

    /// <param name="folders">Asked on every poll, so settings changes apply right away.</param>
    public EventInbox(Func<IReadOnlyList<string>> folders)
    {
        _folders = folders;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        foreach (var folder in _folders())
            Log.Info($"Watching for events in {folder}{(Directory.Exists(folder) ? "" : " (doesn't exist yet)")}");
        Poll();
        _timer.Start();
    }

    /// <summary>Reads every waiting event file once. Public so tests and snapshots can drive it.</summary>
    public void Poll()
    {
        foreach (var folder in _folders())
        {
            string[] files;
            try
            {
                if (!Directory.Exists(folder))
                    continue;
                files = Directory.GetFiles(folder, "*.json");
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't list event folder {folder}: {ex.Message}");
                continue;
            }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
                ReadFile(file);
        }
    }

    private void ReadFile(string path)
    {
        if (_undeletable.Contains(path))
            return;
        string text;
        DateTime created;
        try
        {
            text = File.ReadAllText(path);
            created = File.GetCreationTime(path);
        }
        catch (IOException)
        {
            return; // Still being written; try again next poll.
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var e = AppEvent.Parse(text, created, out string? error);
        if (e is null)
        {
            // An empty or half-written file looks like bad JSON for a moment; give the writer time to finish.
            var first = _firstFailure.TryGetValue(path, out var t) ? t : _firstFailure[path] = DateTime.Now;
            if (DateTime.Now - first < BadFileGrace)
                return;
            _firstFailure.Remove(path);
            MoveToBad(path, error);
            return;
        }
        _firstFailure.Remove(path);

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            // Not deleted means it would be read again next poll, so skip it rather than doubling up.
            Log.Warn($"Couldn't delete event file {path}, skipping it: {ex.Message}");
            _undeletable.Add(path);
            return;
        }

        Log.Info($"Event {e.Kind.ToString().ToLowerInvariant()} {e.Type} \"{e.Label}\" from {e.Source ?? "?"} ({Path.GetFileName(path)})");
        try
        {
            EventReceived?.Invoke(e);
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't apply event from {Path.GetFileName(path)}", ex);
        }
    }

    private static void MoveToBad(string path, string? error)
    {
        try
        {
            string bad = Path.Combine(Path.GetDirectoryName(path)!, "bad");
            Directory.CreateDirectory(bad);
            File.Move(path, Path.Combine(bad, Path.GetFileName(path)), overwrite: true);
            Log.Warn($"Event file {path} isn't a valid event ({error}); moved it to {bad}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Event file {path} isn't a valid event ({error}) and couldn't be moved aside: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Stop();
}
