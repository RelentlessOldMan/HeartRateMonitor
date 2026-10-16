using System.Collections.ObjectModel;
using System.Text.Json;

namespace HeartRateMonitor.Models;

public enum BpmColorMode { Zone, Fixed }

/// <summary>Everything the user can configure. Persisted as JSON in %AppData%\HeartRateMonitor.</summary>
public sealed class AppSettings : ObservableObject
{
    public static string SettingsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HeartRateMonitor");

    private static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // --- Device ---
    private ulong? _deviceAddress;
    private string? _deviceName;
    private bool _autoConnect = true;

    /// <summary>Bluetooth address of the remembered strap. Null = connect to the first strap found.</summary>
    public ulong? DeviceAddress { get => _deviceAddress; set => SetField(ref _deviceAddress, value); }
    public string? DeviceName { get => _deviceName; set => SetField(ref _deviceName, value); }
    public bool AutoConnect { get => _autoConnect; set => SetField(ref _autoConnect, value); }

    // --- Which heart rate to show: the strap's own number, the one calculated from beat timings, or both ---
    private HeartRateDisplay _mainDisplay = HeartRateDisplay.Both;
    private HeartRateDisplay _overlayDisplay = HeartRateDisplay.Strap;

    public HeartRateDisplay MainDisplay { get => _mainDisplay; set => SetField(ref _mainDisplay, value); }
    public HeartRateDisplay OverlayDisplay { get => _overlayDisplay; set => SetField(ref _overlayDisplay, value); }

    // --- Overlay window ---
    private bool _overlayVisible = true;
    private bool _overlayLocked;
    private double _overlayLeft = 100, _overlayTop = 100, _overlayWidth = 260, _overlayHeight = 160;

    public bool OverlayVisible { get => _overlayVisible; set => SetField(ref _overlayVisible, value); }
    /// <summary>Locked = click-through, can't be moved or resized.</summary>
    public bool OverlayLocked { get => _overlayLocked; set => SetField(ref _overlayLocked, value); }
    public double OverlayLeft { get => _overlayLeft; set => SetField(ref _overlayLeft, value); }
    public double OverlayTop { get => _overlayTop; set => SetField(ref _overlayTop, value); }
    public double OverlayWidth { get => _overlayWidth; set => SetField(ref _overlayWidth, value); }
    public double OverlayHeight { get => _overlayHeight; set => SetField(ref _overlayHeight, value); }

    // --- Overlay look ---
    private BpmColorMode _colorMode = BpmColorMode.Zone;
    private string _fixedColor = "#FF3B5C";
    private string _fontFamily = "Segoe UI";
    private bool _showOutline = true;
    private string _outlineColor = "#000000";
    private string _backgroundColor = "#000000";
    private double _backgroundOpacity;
    private bool _showHeart = true;
    private bool _showZoneName;
    private bool _showOverlayGraph;
    private double _overlayGraphMinutes = 5;

    public BpmColorMode ColorMode { get => _colorMode; set => SetField(ref _colorMode, value); }
    /// <summary>BPM color in Fixed mode, and the color below the lowest zone in Zone mode.</summary>
    public string FixedColor { get => _fixedColor; set => SetField(ref _fixedColor, value); }
    public string FontFamily { get => _fontFamily; set => SetField(ref _fontFamily, value); }
    public bool ShowOutline { get => _showOutline; set => SetField(ref _showOutline, value); }
    public string OutlineColor { get => _outlineColor; set => SetField(ref _outlineColor, value); }
    public string BackgroundColor { get => _backgroundColor; set => SetField(ref _backgroundColor, value); }
    public double BackgroundOpacity { get => _backgroundOpacity; set => SetField(ref _backgroundOpacity, value); }
    public bool ShowHeart { get => _showHeart; set => SetField(ref _showHeart, value); }
    public bool ShowZoneName { get => _showZoneName; set => SetField(ref _showZoneName, value); }
    public bool ShowOverlayGraph { get => _showOverlayGraph; set => SetField(ref _showOverlayGraph, value); }
    /// <summary>How much history the overlay graph shows.</summary>
    public double OverlayGraphMinutes { get => _overlayGraphMinutes; set => SetField(ref _overlayGraphMinutes, value); }

    // --- Zones ---
    private int _maxHeartRate = 190;

    public int MaxHeartRate { get => _maxHeartRate; set => SetField(ref _maxHeartRate, value); }
    public ObservableCollection<HeartRateZone> Zones { get; set; } = [];

    // --- Recording ---
    private string _recordingFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HeartRateMonitor");
    private bool _writeTcx = true;

    public string RecordingFolder { get => _recordingFolder; set => SetField(ref _recordingFolder, value); }
    public bool WriteTcx { get => _writeTcx; set => SetField(ref _writeTcx, value); }

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(SettingsPath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
        }
        catch (Exception)
        {
            // Corrupt or incompatible settings: fall back to defaults rather than refusing to start.
        }

        settings ??= new AppSettings();
        if (settings.Zones.Count == 0)
            settings.Zones = new(HeartRateZone.CreateDefaults(settings.MaxHeartRate));
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
