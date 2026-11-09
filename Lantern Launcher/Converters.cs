using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LanternLauncher.Common;

namespace LanternLauncher;

/// <summary>LoadState to Visibility. ConverterParameter = Loading|Ready|Empty|Error.</summary>
public sealed class LoadStateToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LoadState state) return Visibility.Collapsed;
        var target = (parameter as string ?? "").Trim();
        var match = Enum.TryParse<LoadState>(target, true, out var want) && want == state;
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>bool to Visibility. Pass Parameter="invert" to flip the result.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is Visibility.Visible;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag;
    }
}







/// <summary>Visible only while the collection is non-empty; offline just hides the strip.</summary>
public sealed class CountToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
