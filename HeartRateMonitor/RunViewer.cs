using HeartRateMonitor.Events;
using HeartRateMonitor.Models;
using HeartRateMonitor.Recording;

namespace HeartRateMonitor;

/// <summary>A saved recording in the Recordings tab, by file.</summary>
public sealed record RunFile(string CsvPath)
{
    public string Name => Path.GetFileNameWithoutExtension(CsvPath);
    public override string ToString() => Name;
}

/// <summary>
/// The Recordings tab: opens a saved recording into a <see cref="Session"/> (shown like the live one),
/// cuts time out of it, and saves it back over the original, keeping the first original in a backup folder.
/// </summary>
public sealed class RunViewer : ObservableObject
{
    private readonly string _scratchFolder;
    private readonly Stack<(List<HeartRateSample> Samples, List<AppEvent> Events)> _undo = new();
    private string? _csvPath;
    private DateTime _start;
    private bool _isDirty;
    private IReadOnlyList<RunFile> _runs = [];

    public RunViewer(AppSettings settings, string scratchFolder)
    {
        Session = new Session(settings);
        _scratchFolder = scratchFolder;
    }

    public Session Session { get; }
    public AppSettings Settings => Session.Settings;

    public string? CsvPath
    {
        get => _csvPath;
        private set
        {
            if (SetField(ref _csvPath, value))
            {
                OnPropertyChanged(nameof(IsOpen));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsOpen => CsvPath is not null;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetField(ref _isDirty, value))
                OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanUndo));
        }
    }
    public bool CanUndo => _undo.Count > 0;

    public string StatusText => CsvPath is null
        ? "Pick a recording to open it."
        : $"{Path.GetFileName(CsvPath)}: " +
          (IsDirty ? "edited, not saved yet" : $"{Session.Spans.Count(s => !s.IsSegment)} events, {Session.Samples.Count} samples");

    /// <summary>The recordings in the recordings folder, newest first.</summary>
    public IReadOnlyList<RunFile> Runs { get => _runs; private set => SetField(ref _runs, value); }

    public void RefreshRuns()
    {
        try
        {
            Runs = Directory.Exists(Settings.RecordingFolder)
                ? Directory.GetFiles(Settings.RecordingFolder, "*.csv")
                    .Where(f => !f.EndsWith(".spans.csv", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .Select(f => new RunFile(f)).ToList()
                : [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't list recordings in {Settings.RecordingFolder}: {ex.Message}");
            Runs = [];
        }
    }

    public void Open(string csvPath)
    {
        var (start, samples, events) = SessionRecorder.Load(csvPath);
        Log.Info($"Opened recording {csvPath}: {samples.Count} samples, {events.Count} events");
        _start = start;
        _undo.Clear();
        Session.Load(samples, events);
        CsvPath = csvPath;
        IsDirty = false;
        OnPropertyChanged(nameof(StatusText));
    }

    public void Close()
    {
        _undo.Clear();
        Session.Clear();
        CsvPath = null;
        IsDirty = false;
    }

    /// <summary>Removes the selected time range (and any song etc. that began in it).</summary>
    public void CutSelection()
    {
        if (!Session.HasSelection)
            return;
        _undo.Push((Session.Samples.ToList(), Session.Events.ToList()));
        Log.Info($"Cut {Session.SelectionStart:HH:mm:ss.fff}–{Session.SelectionEnd:HH:mm:ss.fff} from {CsvPath}");
        Session.Cut(Session.SelectionStart!.Value, Session.SelectionEnd!.Value);
        IsDirty = true;
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var state))
            return;
        Session.Load(state.Samples, state.Events);
        IsDirty = _undo.Count > 0;
    }

    /// <summary>
    /// Writes the edited recording over the original files. The first time a recording is edited its original files
    /// are copied into a "backup" folder next to it; later saves leave that backup alone.
    /// </summary>
    public IReadOnlyList<string> Save()
    {
        if (CsvPath is not { } csvPath)
            return [];
        string folder = Path.GetDirectoryName(csvPath)!;
        string name = Path.GetFileNameWithoutExtension(csvPath);
        var originals = SessionRecorder.OwnFiles(csvPath);
        bool writeTcx = Settings.WriteTcx || originals.Any(f => f.EndsWith(".tcx", StringComparison.OrdinalIgnoreCase));

        string backup = Path.Combine(folder, "backup");
        Directory.CreateDirectory(backup);
        foreach (var file in originals)
        {
            string target = Path.Combine(backup, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Copy(file, target);
        }

        var saved = SessionRecorder.Write(Session, _scratchFolder, folder, _start, name, writeTcx);
        // A sidecar the edit no longer has (e.g. every song was cut) mustn't be left describing the old version.
        foreach (var file in originals.Where(f => !saved.Contains(f, StringComparer.OrdinalIgnoreCase)))
            File.Delete(file);
        Log.Info($"Saved edited recording {csvPath} (original kept in {backup})");
        _undo.Clear();
        IsDirty = false;
        return saved;
    }
}
