using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HeartRateMonitor.Events;
using HeartRateMonitor.Models;

namespace HeartRateMonitor.Controls;

/// <summary>
/// Line graph of heart rate over time, colored by zone, with optional zone bands behind it.
/// With <see cref="IsSelectable"/>, a time range can be picked: drag across it, click a song (or segment) to pick
/// that, Shift-click to stretch the selection; dragging past either edge runs to the start or end.
/// </summary>
public sealed class HeartRateGraph : FrameworkElement
{
    /// <summary>Color of the calculated line when it's drawn next to the strap's, so the two can be told apart.</summary>
    public const string CalculatedLineColor = "#00E5FF";

    /// <summary>Samples further apart than this are drawn as a gap (e.g. strap dropped out).</summary>
    private static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(5);

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IList<HeartRateSample>), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionChanged));

    public static readonly DependencyProperty ZonesProperty = DependencyProperty.Register(
        nameof(Zones), typeof(IEnumerable<HeartRateZone>), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionChanged));

    public static readonly DependencyProperty WindowMinutesProperty = DependencyProperty.Register(
        nameof(WindowMinutes), typeof(double), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DefaultColorProperty = DependencyProperty.Register(
        nameof(DefaultColor), typeof(string), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UseZoneColorsProperty = DependencyProperty.Register(
        nameof(UseZoneColors), typeof(bool), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowZoneBandsProperty = DependencyProperty.Register(
        nameof(ShowZoneBands), typeof(bool), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineThicknessProperty = DependencyProperty.Register(
        nameof(LineThickness), typeof(double), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(HeartRateDisplay), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(HeartRateDisplay.Strap, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpansProperty = DependencyProperty.Register(
        nameof(Spans), typeof(IList<EventSpan>), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionChanged));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(DateTime?), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(DateTime?), typeof(HeartRateGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsSelectableProperty = DependencyProperty.Register(
        nameof(IsSelectable), typeof(bool), typeof(HeartRateGraph), new FrameworkPropertyMetadata(false));

    public DateTime? SelectionStart { get => (DateTime?)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public DateTime? SelectionEnd { get => (DateTime?)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public bool IsSelectable { get => (bool)GetValue(IsSelectableProperty); set => SetValue(IsSelectableProperty, value); }

    /// <summary>Songs etc. to shade behind the line. Only the innermost ones are drawn (songs, not the course around them), with their stream segments lighter.</summary>
    public IList<EventSpan>? Spans { get => (IList<EventSpan>?)GetValue(SpansProperty); set => SetValue(SpansProperty, value); }
    public IList<HeartRateSample>? Samples { get => (IList<HeartRateSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public IEnumerable<HeartRateZone>? Zones { get => (IEnumerable<HeartRateZone>?)GetValue(ZonesProperty); set => SetValue(ZonesProperty, value); }
    /// <summary>Show only the last N minutes, scrolling. 0 = the whole session.</summary>
    public double WindowMinutes { get => (double)GetValue(WindowMinutesProperty); set => SetValue(WindowMinutesProperty, value); }
    /// <summary>Line color when not using zone colors, or below the lowest zone.</summary>
    public string DefaultColor { get => (string)GetValue(DefaultColorProperty); set => SetValue(DefaultColorProperty, value); }
    public bool UseZoneColors { get => (bool)GetValue(UseZoneColorsProperty); set => SetValue(UseZoneColorsProperty, value); }
    public bool ShowZoneBands { get => (bool)GetValue(ShowZoneBandsProperty); set => SetValue(ShowZoneBandsProperty, value); }
    public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
    public double LineThickness { get => (double)GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }
    /// <summary>Which line(s) to draw. With both, the calculated one is drawn in <see cref="CalculatedLineColor"/>.</summary>
    public HeartRateDisplay Display { get => (HeartRateDisplay)GetValue(DisplayProperty); set => SetValue(DisplayProperty, value); }

    public HeartRateGraph()
    {
        ClipToBounds = true;
    }

    private static void OnCollectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (HeartRateGraph)d;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= graph.OnItemsChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += graph.OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    // The time axis as last drawn, for turning mouse positions into times.
    private DateTime _axisStart;
    private double _axisTicks, _axisWidth;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        var samples = Samples;
        if (width < 2 || height < 2 || samples is null || samples.Count == 0)
            return;

        var zones = (Zones ?? []).OrderBy(z => z.MinBpm).ToList();

        // Time axis.
        DateTime end = samples[^1].Time;
        DateTime start;
        if (WindowMinutes > 0)
        {
            start = end - TimeSpan.FromMinutes(WindowMinutes);
        }
        else
        {
            start = samples[0].Time;
            if (end - start < TimeSpan.FromMinutes(1))
                end = start + TimeSpan.FromMinutes(1);
        }
        double spanTicks = (end - start).Ticks;
        (_axisStart, _axisTicks, _axisWidth) = (start, spanTicks, width);
        // Something to hit everywhere, so clicks between the bands still count.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        int first = FirstIndexAtOrAfter(samples, start);
        if (first >= samples.Count)
            return;

        bool showStrap = Display != HeartRateDisplay.Calculated;
        bool showCalculated = Display != HeartRateDisplay.Strap;
        static int? StrapValue(HeartRateSample s) => s.Bpm;
        static int? CalculatedValue(HeartRateSample s) => s.CalculatedBpm;

        // BPM axis: fit the visible data with some headroom, at least 40 bpm tall.
        int min = int.MaxValue, max = int.MinValue;
        for (int i = first; i < samples.Count; i++)
        {
            foreach (int? value in (ReadOnlySpan<int?>)[showStrap ? samples[i].Bpm : null, showCalculated ? samples[i].CalculatedBpm : null])
            {
                if (value is { } v)
                {
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                }
            }
        }
        if (min > max)
            return; // Nothing to draw, e.g. only the calculated line and no beat data yet.
        double lo = min - 10, hi = max + 10;
        if (hi - lo < 40)
        {
            double mid = (hi + lo) / 2;
            lo = mid - 20;
            hi = mid + 20;
        }

        double X(DateTime t) => (t - start).Ticks / spanTicks * width;
        double Y(double bpm) => height - (bpm - lo) / (hi - lo) * height;

        if (ShowZoneBands)
        {
            for (int i = 0; i < zones.Count; i++)
            {
                double bandLo = Math.Max(zones[i].MinBpm, lo);
                double bandHi = Math.Min(i + 1 < zones.Count ? zones[i + 1].MinBpm : hi, hi);
                if (bandHi <= bandLo)
                    continue;
                var brush = BrushCache.Get(zones[i].Color, alpha: 0x38);
                dc.DrawRectangle(brush, null, new Rect(0, Y(bandHi), width, Y(bandLo) - Y(bandHi)));
            }
        }

        if (Spans is { Count: > 0 } spans)
        {
            var bandBrush = BrushCache.Get("#FFFFFF", alpha: 0x16);
            var streamBrush = BrushCache.Get("#FFFFFF", alpha: 0x14);
            var edgePen = BrushCache.GetPen("#555555", 1);
            var textBrush = BrushCache.Get("#AAAAAA");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var parents = spans.Where(s => !s.IsSegment).Select(s => s.Parent).Where(p => p is not null).ToHashSet();
            foreach (var span in spans)
            {
                if (parents.Contains(span.Id))
                    continue;
                double x0 = Math.Max(X(span.Start), 0), x1 = Math.Min(X(span.EndOr(samples[^1].Time)), width);
                if (x1 <= 0 || x0 >= width || x1 <= x0)
                    continue;
                if (span.IsSegment)
                {
                    if (span.SegmentKind == "stream")
                        dc.DrawRectangle(streamBrush, null, new Rect(x0, 0, x1 - x0, height));
                    continue;
                }
                dc.DrawRectangle(bandBrush, null, new Rect(x0, 0, x1 - x0, height));
                dc.DrawLine(edgePen, new Point(x0, 0), new Point(x0, height));
                if (ShowLabels && x1 - x0 > 30)
                {
                    var text = new FormattedText(span.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 10, textBrush, dpi)
                    { MaxTextWidth = x1 - x0 - 6, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(text, new Point(x0 + 3, height - text.Height - 16));
                }
            }
        }

        string ColorFor(int bpm) =>
            UseZoneColors ? HeartRateZone.Find(zones, bpm)?.Color ?? DefaultColor : DefaultColor;

        // Draw runs of same-colored, contiguous samples as one polyline each.
        void DrawLine(Func<HeartRateSample, int?> valueOf, Func<int, string> colorFor)
        {
            var run = new List<Point>();
            string? runColor = null;
            void Flush()
            {
                if (run.Count >= 2 && runColor is not null)
                    DrawPolyline(dc, run, BrushCache.GetPen(runColor, LineThickness));
                run.Clear();
            }

            double lastX = double.NegativeInfinity;
            DateTime? lastTime = null;
            for (int i = first; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (valueOf(sample) is not { } bpm)
                {
                    Flush(); // No value here: break the line.
                    lastTime = null;
                    continue;
                }
                var point = new Point(X(sample.Time), Y(bpm));
                bool gap = lastTime is { } t && sample.Time - t > GapThreshold;
                string color = colorFor(bpm);

                if (gap || lastTime is null)
                {
                    Flush();
                    runColor = color;
                }
                else if (color != runColor)
                {
                    // Start the new color at the previous point so the line stays continuous.
                    Point? joint = run.Count > 0 ? run[^1] : null;
                    Flush();
                    if (joint is { } j)
                        run.Add(j);
                    runColor = color;
                }
                else if (point.X - lastX < 0.75 && i != samples.Count - 1)
                {
                    continue; // More samples than pixels: skip ones that would land on the same column.
                }

                run.Add(point);
                lastX = point.X;
                lastTime = sample.Time;
            }
            Flush();
        }

        if (SelectionStart is { } selStart && SelectionEnd is { } selEnd && selEnd > selStart)
        {
            double x0 = Math.Max(X(selStart), -1), x1 = Math.Min(X(selEnd), width + 1);
            if (x1 > x0)
            {
                var edge = BrushCache.GetPen("#66B2FF", 1);
                dc.DrawRectangle(BrushCache.Get("#3399FF", alpha: 0x40), null, new Rect(x0, 0, x1 - x0, height));
                dc.DrawLine(edge, new Point(x0, 0), new Point(x0, height));
                dc.DrawLine(edge, new Point(x1, 0), new Point(x1, height));
            }
        }

        if (showStrap)
            DrawLine(StrapValue, ColorFor);
        if (showCalculated)
            DrawLine(CalculatedValue, showStrap ? _ => CalculatedLineColor : ColorFor);

        if (ShowLabels)
        {
            var labelBrush = BrushCache.Get("#888888");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText Label(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, labelBrush, dpi);
            dc.DrawText(Label(((int)hi).ToString()), new Point(4, 2));
            var bottom = Label(((int)lo).ToString());
            dc.DrawText(bottom, new Point(4, height - bottom.Height - 2));

            if (showStrap && showCalculated)
            {
                // Legend, top right: which line is which.
                double x = width - 4;
                foreach (var (text, color) in new[] { ("calculated", CalculatedLineColor), ("strap", DefaultColor) })
                {
                    var label = Label(text);
                    x -= label.Width;
                    dc.DrawText(label, new Point(x, 2));
                    x -= 20;
                    double y = 2 + label.Height / 2;
                    dc.DrawLine(BrushCache.GetPen(color, 2), new Point(x, y), new Point(x + 14, y));
                    x -= 12;
                }
            }
        }
    }

    // --- Selecting ---

    private Point? _pressedAt;
    private bool _dragging;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsSelectable || Samples is not { Count: > 0 } || _axisTicks <= 0)
            return;
        _pressedAt = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedAt is not { } from || !IsMouseCaptured)
            return;
        var at = e.GetPosition(this);
        if (!_dragging && Math.Abs(at.X - from.X) < 4)
            return;
        _dragging = true;
        SetSelection(TimeAt(from.X), TimeAt(at.X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressedAt is null)
            return;
        _pressedAt = null;
        ReleaseMouseCapture();
        if (_dragging || Samples is not { Count: > 0 } samples)
            return;

        // A click: pick the smallest span under it; with Shift, stretch the selection to take it (or that moment) in.
        var time = TimeAt(e.GetPosition(this).X);
        var last = samples[^1].Time;
        var span = Spans?.Where(s => s.Start <= time && s.EndOr(last) >= time).MinBy(s => s.EndOr(last) - s.Start);
        var (from, to) = span is null ? (time, time) : (span.Start, span.EndOr(last));
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && SelectionStart is { } a && SelectionEnd is { } b)
            SetSelection(a < from ? a : from, b > to ? b : to);
        else if (span is not null)
            SetSelection(from, to);
        else
            SetSelection(null, null);
    }

    private void SetSelection(DateTime? from, DateTime? to)
    {
        if (from > to)
            (from, to) = (to, from);
        SelectionStart = from;
        SelectionEnd = to;
    }

    /// <summary>The time at a point across the graph, kept within the recording.</summary>
    private DateTime TimeAt(double x)
    {
        var samples = Samples!;
        if (x <= 0)
            return samples[0].Time;
        if (x >= _axisWidth)
            return samples[^1].Time;
        var time = _axisStart.AddTicks((long)(x / _axisWidth * _axisTicks));
        return time < samples[0].Time ? samples[0].Time : time > samples[^1].Time ? samples[^1].Time : time;
    }

    private static int FirstIndexAtOrAfter(IList<HeartRateSample> samples, DateTime time)
    {
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static void DrawPolyline(DrawingContext dc, List<Point> points, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
