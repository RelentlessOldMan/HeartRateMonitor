using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HeartRateMonitor.Controls;

/// <summary>"#RRGGBB" → SolidColorBrush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        BrushCache.Get(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → Color (for effects like DropShadowEffect.Color).</summary>
public sealed class HexToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        BrushCache.ParseColor(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → star GridLength: true gives the parameter's star weight, false collapses the row.</summary>
public sealed class BoolToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? new GridLength(double.Parse((string)parameter, CultureInfo.InvariantCulture), GridUnitType.Star)
            : new GridLength(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Enum ↔ bool for radio buttons; parameter is the enum member name.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == (string)parameter;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, (string)parameter) : Binding.DoNothing;
}

/// <summary>
/// HeartRateDisplay → Visibility. Parameter "Strap"/"Calculated": visible when that reading is shown.
/// Parameter "Both": visible only when both are shown (for captions telling them apart).
/// </summary>
public sealed class HeartRateDisplayToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var display = value as HeartRateMonitor.Models.HeartRateDisplay? ?? HeartRateMonitor.Models.HeartRateDisplay.Strap;
        bool visible = (string)parameter switch
        {
            "Strap" => display != HeartRateMonitor.Models.HeartRateDisplay.Calculated,
            "Calculated" => display != HeartRateMonitor.Models.HeartRateDisplay.Strap,
            _ => display == HeartRateMonitor.Models.HeartRateDisplay.Both,
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
