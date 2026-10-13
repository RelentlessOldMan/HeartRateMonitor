using System.Windows.Media;

namespace HeartRateMonitor.Controls;

/// <summary>Frozen brushes and pens from "#RRGGBB" strings, cached since they're rebuilt every render.</summary>
public static class BrushCache
{
    private static readonly Dictionary<(string, byte?), SolidColorBrush> Brushes = [];
    private static readonly Dictionary<(string, double), Pen> Pens = [];

    public static Color ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color color)
                return color;
        }
        catch (FormatException)
        {
        }
        return Colors.White;
    }

    public static SolidColorBrush Get(string? hex, byte? alpha = null)
    {
        var key = (hex ?? "", alpha);
        if (!Brushes.TryGetValue(key, out var brush))
        {
            var color = ParseColor(hex);
            if (alpha is { } a)
                color.A = a;
            brush = new SolidColorBrush(color);
            brush.Freeze();
            Brushes[key] = brush;
        }
        return brush;
    }

    public static Pen GetPen(string hex, double thickness)
    {
        var key = (hex, thickness);
        if (!Pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Get(hex), thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            Pens[key] = pen;
        }
        return pen;
    }
}
