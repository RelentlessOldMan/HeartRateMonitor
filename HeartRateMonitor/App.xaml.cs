using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using HeartRateMonitor.Models;

namespace HeartRateMonitor;

/// <summary>
/// Command line:
///   --dummy              Generate fake heart rate data instead of connecting to a strap.
///   --snapshot [folder]  Render all screens to PNGs off-screen and exit (see <see cref="Snapshot"/>).
/// </summary>
public partial class App : Application
{
    private AppSettings? _settings;
    private MainViewModel? _vm;
    private bool _exiting;
    /// <summary>Settings are saved shortly after any change, so a crash or kill doesn't lose them.</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int snapshotArg = Array.FindIndex(e.Args, a => a.TrimStart('-', '/').Equals("snapshot", StringComparison.OrdinalIgnoreCase));
        if (snapshotArg >= 0)
        {
            string folder = snapshotArg + 1 < e.Args.Length ? e.Args[snapshotArg + 1] : "snapshots";
            Dispatcher.BeginInvoke(async () =>
            {
                await Snapshot.RunAsync(folder);
                Shutdown();
            });
            return;
        }

        bool dummy = e.Args.Any(a => a.TrimStart('-', '/').Equals("dummy", StringComparison.OrdinalIgnoreCase));

        _settings = AppSettings.Load();
        _vm = new MainViewModel(_settings, dummy);

        var main = new MainWindow(_vm);
        var overlay = new OverlayWindow(_vm);
        main.Overlay = overlay;
        MainWindow = main;
        // Closing the main window shuts the app down, which closes the overlay too. Mark that as
        // exiting first, or the overlay's Closing handler below mistakes it for the user hiding it.
        main.Closed += (_, _) => _exiting = true;

        overlay.SettingsRequested += () =>
        {
            if (main.WindowState == WindowState.Minimized)
                main.WindowState = WindowState.Normal;
            main.Activate();
        };
        // Alt+F4 on the overlay just hides it; it only really closes when the app exits.
        overlay.Closing += (_, args) =>
        {
            if (!_exiting)
            {
                args.Cancel = true;
                _settings.OverlayVisible = false;
            }
        };
        _settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppSettings.OverlayVisible))
                SetOverlayVisible(overlay, _settings.OverlayVisible);
        };

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
        _settings.PropertyChanged += (_, _) => ScheduleSave();
        _settings.Zones.CollectionChanged += OnZonesChanged;
        foreach (var zone in _settings.Zones)
            zone.PropertyChanged += OnZoneChanged;

        main.Show();
        SetOverlayVisible(overlay, _settings.OverlayVisible);
        _vm.Start();
    }

    private static void SetOverlayVisible(Window overlay, bool visible)
    {
        if (visible) overlay.Show();
        else overlay.Hide();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnZonesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (HeartRateZone zone in e.OldItems ?? Array.Empty<HeartRateZone>())
            zone.PropertyChanged -= OnZoneChanged;
        foreach (HeartRateZone zone in e.NewItems ?? Array.Empty<HeartRateZone>())
            zone.PropertyChanged += OnZoneChanged;
        ScheduleSave();
    }

    private void OnZoneChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    private void SaveSettings()
    {
        try
        {
            _settings?.Save();
        }
        catch (Exception)
        {
            // Losing settings on a locked/readonly AppData isn't worth crashing over.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _saveTimer.Stop();
        _vm?.Dispose();
        SaveSettings();
        base.OnExit(e);
    }
}
