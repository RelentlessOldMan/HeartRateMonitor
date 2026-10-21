using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using HeartRateMonitor.Controls;
using HeartRateMonitor.Models;

namespace HeartRateMonitor;

/// <summary>The always-on-top BPM display that sits over the game.</summary>
public partial class OverlayWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _heartbeat = new();
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IntPtr _hwnd;

    /// <summary>Raised when the user picks "Settings…" from the overlay's menu.</summary>
    public event Action? SettingsRequested;

    public OverlayWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        EnsureOnScreen();

        _heartbeat.Tick += (_, _) => Beat();
        // Some games push themselves above topmost windows; quietly put the overlay back on top.
        _topmostTimer.Tick += (_, _) => SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyLock();
        };
        Loaded += (_, _) => _topmostTimer.Start();
        Closed += (_, _) => { _heartbeat.Stop(); _topmostTimer.Stop(); };

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        MouseEnter += (_, _) => HoverFrame.Visibility = _vm.Settings.OverlayLocked ? Visibility.Hidden : Visibility.Visible;
        MouseLeave += (_, _) => HoverFrame.Visibility = Visibility.Hidden;

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Settings.PropertyChanged += OnSettingsChanged;
        _vm.Strap.PropertyChanged += OnReadingChanged;
        _vm.Calculated.PropertyChanged += OnReadingChanged;
        ApplyOutline();
        UpdateHeartbeat();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.OverlayLead))
            UpdateHeartbeat();
    }

    private void OnReadingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BpmReading.Bpm) or nameof(BpmReading.IsFrozen))
            UpdateHeartbeat();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.OverlayLocked):
                ApplyLock();
                break;
            case nameof(AppSettings.ShowOutline) or nameof(AppSettings.OutlineColor):
                ApplyOutline();
                break;
        }
    }

    // --- Heart animation: one pulse per beat at the current BPM, still while the number is frozen ---

    private void UpdateHeartbeat()
    {
        if (_vm.OverlayLead.Bpm is not { } bpm || bpm <= 0 || _vm.OverlayLead.IsFrozen)
        {
            _heartbeat.Stop();
            return;
        }
        _heartbeat.Interval = TimeSpan.FromMilliseconds(60000.0 / bpm);
        if (!_heartbeat.IsEnabled)
            _heartbeat.Start();
    }

    private void Beat()
    {
        if (!_vm.Settings.ShowHeart)
            return;
        var pulse = new DoubleAnimation(1.0, 1.2, TimeSpan.FromMilliseconds(90))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase(),
        };
        HeartScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        HeartScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    // --- Outline (a tight black glow keeps the number readable over busy game visuals) ---

    private void ApplyOutline()
    {
        Effect? MakeEffect() => _vm.Settings.ShowOutline
            ? new DropShadowEffect
            {
                Color = BrushCache.ParseColor(_vm.Settings.OutlineColor),
                ShadowDepth = 0,
                BlurRadius = 8,
                Opacity = 1,
            }
            : null;
        TextPanel.Effect = MakeEffect();
        Graph.Effect = MakeEffect();
    }

    // --- Lock = click-through ---

    private void ApplyLock()
    {
        bool locked = _vm.Settings.OverlayLocked;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        Root.Background = locked ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        HoverFrame.Visibility = Visibility.Hidden;

        if (_hwnd == IntPtr.Zero)
            return;
        long style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64() | WsExToolWindow;
        style = locked
            ? style | WsExTransparent | WsExNoActivate
            : style & ~(WsExTransparent | WsExNoActivate);
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(style));
    }

    /// <summary>If the saved position is off every monitor (e.g. a monitor was unplugged), move it back.</summary>
    private void EnsureOnScreen()
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var s = _vm.Settings;
        if (!screen.IntersectsWith(new Rect(s.OverlayLeft, s.OverlayTop, s.OverlayWidth, s.OverlayHeight)))
        {
            s.OverlayLeft = 100;
            s.OverlayTop = 100;
        }
    }

    public void ResetPosition()
    {
        _vm.Settings.OverlayLeft = 100;
        _vm.Settings.OverlayTop = 100;
        _vm.Settings.OverlayWidth = 260;
        _vm.Settings.OverlayHeight = 160;
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => _vm.Settings.OverlayLocked = true;
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void Hide_Click(object sender, RoutedEventArgs e) => _vm.Settings.OverlayVisible = false;

    // --- Win32 ---

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
