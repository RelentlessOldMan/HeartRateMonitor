using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeartRateMonitor.Models;
using HeartRateMonitor.Sensors;

namespace HeartRateMonitor;

/// <summary>
/// Dev aid: <c>--snapshot &lt;folder&gt;</c> renders every tab and the overlay to PNGs with dummy data,
/// entirely off-screen. No window is ever shown, so nothing steals focus or pops up over a game.
/// Uses default settings and never saves them.
/// </summary>
public static class Snapshot
{
    public static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var settings = new AppSettings();
        settings.Zones = new(HeartRateZone.CreateDefaults(settings.MaxHeartRate));
        var vm = new MainViewModel(settings, dummy: true);

        // Ten minutes of fake session.
        var dummy = new DummyHeartRateSource();
        var start = DateTime.Now.AddMinutes(-10);
        for (int i = 0; i < 600; i++)
            vm.OnSample(dummy.Next(start.AddSeconds(i)));

        // A few songs, as the Simply Love module would report them: two singles, then a two-song course still going.
        void Send(int second, string json) =>
            vm.OnEvent(Events.AppEvent.Parse(json, start.AddSeconds(second), out _)!);
        Send(30, """{"kind":"begin","type":"song","id":"s1","source":"simply-love","label":"Acme Songo Wongo [Hard 7]","data":{"title":"Acme Songo Wongo","players":[{"player":"P1","difficulty":"Hard","meter":7}]}}""");
        Send(140, """{"kind":"end","type":"song","id":"s1","source":"simply-love","data":{"result":"cleared"}}""");
        Send(170, """{"kind":"begin","type":"song","id":"s2","source":"simply-love","label":"Stamina Thing [Challenge 12]"}""");
        Send(300, """{"kind":"end","type":"song","id":"s2","source":"simply-love","data":{"result":"failed"}}""");
        Send(330, """{"kind":"begin","type":"course","id":"c1","source":"simply-love","label":"Some Course [Medium]"}""");
        Send(330, """{"kind":"begin","type":"song","id":"c1s1","parent":"c1","source":"simply-love","label":"First Course Song"}""");
        Send(450, """{"kind":"end","type":"song","id":"c1s1","source":"simply-love","data":{"result":"cleared"}}""");
        Send(452, """{"kind":"begin","type":"song","id":"c1s2","parent":"c1","source":"simply-love","label":"Second Course Song"}""");

        // Steal the windows' content and lay it out on its own; the windows themselves are never shown.
        var main = new MainWindow(vm);
        var mainContent = (FrameworkElement)main.Content;
        main.Content = null;
        // Implicit styles live in the window's resources; move them along with the content.
        var mainResources = main.Resources;
        main.Resources = new ResourceDictionary();
        mainContent.Resources = mainResources;
        mainContent.DataContext = vm;
        var tabs = main.Tabs;
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            await Render(mainContent, main.Width - 16, main.Height - 39, Brushes.White,
                Path.Combine(folder, $"tab_{((TabItem)tabs.Items[i]).Header}.png"));
        }

        var overlay = new OverlayWindow(vm);
        var overlayContent = (FrameworkElement)overlay.Content;
        overlay.Content = null;
        // Implicit styles live in the window's resources; move them along with the content.
        var overlayResources = overlay.Resources;
        overlay.Resources = new ResourceDictionary();
        overlayContent.Resources = overlayResources;
        overlayContent.DataContext = vm;
        var gameBackdrop = new SolidColorBrush(Color.FromRgb(40, 30, 60));
        await Render(overlayContent, settings.OverlayWidth, settings.OverlayHeight, gameBackdrop,
            Path.Combine(folder, "overlay.png"));
        settings.ShowOverlayGraph = true;
        settings.ShowZoneName = true;
        settings.OverlayDisplay = HeartRateDisplay.Both;
        await Render(overlayContent, settings.OverlayWidth, settings.OverlayHeight + 80, gameBackdrop,
            Path.Combine(folder, "overlay_graph.png"));

        vm.Dispose();
    }

    private static async Task Render(FrameworkElement element, double width, double height, Brush background, string path)
    {
        // Let bindings and templates settle before measuring.
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        const double dpi = 192;
        var bitmap = new RenderTargetBitmap((int)(width * 2), (int)(height * 2), dpi, dpi, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
