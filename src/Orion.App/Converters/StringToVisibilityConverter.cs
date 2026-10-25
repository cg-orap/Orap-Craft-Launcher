using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Orion.App.Converters;

/// <summary>Visible only while the bound activity state reads 进行中 / Running.</summary>
public sealed class StateToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var running = value is string text &&
                      (text.Contains("进行中", StringComparison.Ordinal) ||
                       text.Contains("Running", StringComparison.OrdinalIgnoreCase));

        return running ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}