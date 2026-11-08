using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WindowsDynamicIsland.UI.Components;

/// <summary>true → Visible, false → Collapsed (or reversed with <see cref="Invert"/>).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Visible when the value "has data": non-null, non-blank string, true, non-zero number, non-empty collection.
/// Used to hide labels whose source data is empty (no placeholder text, ever).
/// </summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = HasValue(value);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    public static bool HasValue(object? value) => value switch
    {
        null => false,
        string s => !string.IsNullOrWhiteSpace(s),
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0 && !double.IsNaN(d),
        ICollection c => c.Count > 0,
        _ => true,
    };
}

/// <summary>double (element height) → CornerRadius of half that value: a perfect pill at any size.</summary>
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d && d > 0 && !double.IsInfinity(d) ? new CornerRadius(d / 2) : new CornerRadius(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
