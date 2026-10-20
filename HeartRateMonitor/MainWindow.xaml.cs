using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using HeartRateMonitor.Models;
using Microsoft.Win32;

namespace HeartRateMonitor;

/// <summary>The "boring" window: connection, settings, zones, recording, full-session graph.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        FontCombo.ItemsSource = Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
        Title += $" v{AppVersion}";
        if (vm.IsDummy)
            Title += " (dummy data)";
    }

    public static string AppVersion { get; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Set by App so the "Reset position" button can reach the overlay.</summary>
    public OverlayWindow? Overlay { get; set; }

    private void Reconnect_Click(object sender, RoutedEventArgs e) => _vm.Reconnect();

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Forget the remembered strap? The next heart rate strap found will be remembered instead.",
                "Forget strap", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            _vm.ForgetDevice();
    }

    // --- Live tab ---

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsRecording)
        {
            _vm.StartRecording();
            return;
        }
        try
        {
            if (_vm.SaveRecording().Count == 0)
                MessageBox.Show(this, "Nothing recorded yet, so there's nothing to save.", "Save recording",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the recording: {ex.Message}\n\nIt's kept and will be recovered into the recordings folder next time the app starts.",
                "Save recording", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_vm.Settings.RecordingFolder);
        Process.Start(new ProcessStartInfo(_vm.Settings.RecordingFolder) { UseShellExecute = true });
    }

    private void ResetSession_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasUnsavedRecording ||
            MessageBox.Show(this, "Throw away the unsaved recording and start a new one?", "Reset",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            _vm.ResetSession();
    }

    private void OpenEventInbox_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.EventInboxFolder);
        Process.Start(new ProcessStartInfo(AppSettings.EventInboxFolder) { UseShellExecute = true });
    }

    // --- Overlay tab ---

    private void ResetOverlay_Click(object sender, RoutedEventArgs e)
    {
        Overlay?.ResetPosition();
        _vm.Settings.OverlayVisible = true;
    }

    private void FixedColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(_vm.Settings.FixedColor, c => _vm.Settings.FixedColor = c);

    private void OutlineColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(_vm.Settings.OutlineColor, c => _vm.Settings.OutlineColor = c);

    private void BackgroundColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(_vm.Settings.BackgroundColor, c => _vm.Settings.BackgroundColor = c);

    // --- Zones tab ---

    private void ZoneColor_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is HeartRateZone zone)
            PickColor(zone.Color, c => zone.Color = c);
    }

    private void RecalculateZones_Click(object sender, RoutedEventArgs e)
    {
        // Replace the contents rather than the collection so existing bindings keep working.
        _vm.Settings.Zones.Clear();
        foreach (var zone in HeartRateZone.CreateDefaults(_vm.Settings.MaxHeartRate))
            _vm.Settings.Zones.Add(zone);
    }

    private void AddZone_Click(object sender, RoutedEventArgs e)
    {
        int top = _vm.Settings.Zones.Count > 0 ? _vm.Settings.Zones.Max(z => z.MinBpm) : 100;
        var zone = new HeartRateZone { Name = $"Z{_vm.Settings.Zones.Count + 1}", MinBpm = top + 10, Color = "#E040FB" };
        _vm.Settings.Zones.Add(zone);
        ZonesGrid.SelectedItem = zone;
    }

    private void RemoveZone_Click(object sender, RoutedEventArgs e)
    {
        if (ZonesGrid.SelectedItem is HeartRateZone zone)
            _vm.Settings.Zones.Remove(zone);
    }

    // --- Settings tab ---

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _vm.Settings.RecordingFolder };
        if (dialog.ShowDialog(this) == true)
            _vm.Settings.RecordingFolder = dialog.FolderName;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Log.FilePath))
            Process.Start("explorer.exe", $"/select,\"{Log.FilePath}\"");
        else
            Process.Start(new ProcessStartInfo(Directory.CreateDirectory(Log.Folder).FullName) { UseShellExecute = true });
    }

    // --- Helpers ---

    private void PickColor(string current, Action<string> apply)
    {
        var color = Controls.BrushCache.ParseColor(current);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B),
        };
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new WindowInteropHelper(this).Handle);
        try
        {
            if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                apply($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        }
        finally
        {
            owner.ReleaseHandle();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.HasUnsavedRecording)
        {
            switch (MessageBox.Show(this, "Save this session's recording before quitting?", "Heart Rate Monitor",
                        MessageBoxButton.YesNoCancel, MessageBoxImage.Question))
            {
                case MessageBoxResult.Yes:
                    try
                    {
                        _vm.SaveRecording();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"Couldn't save the recording: {ex.Message}\n\nIt's kept and will be recovered into the recordings folder next time the app starts.",
                            "Save recording", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    break;
                case MessageBoxResult.No:
                    _vm.DiscardRecording();
                    break;
                default:
                    e.Cancel = true;
                    break;
            }
        }
        base.OnClosing(e);
    }
}
