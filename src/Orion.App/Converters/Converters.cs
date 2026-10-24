using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Orion.App.Converters;

/// <summary>true → Visible, false → Collapsed. Pass "invert" as ConverterParameter to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>0..1 → percentage text.</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:0.0}%" : "0%";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Byte count → human readable size.</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes <= 0) return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.0} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Empty-string → Collapsed, used to hide hint labels.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Account name → single initial used by the avatar badge.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? "").Trim();
        if (text.Length == 0) return "?";

        // Latin names look best upper-cased; CJK characters stay as they are.
        var first = text[0];
        return char.IsAsciiLetter(first) ? char.ToUpperInvariant(first).ToString() : first.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
