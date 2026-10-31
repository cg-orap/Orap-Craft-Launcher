using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Orion.App.Converters;

/// <summary>集合数量为 0 时显示（空状态提示用）：0 → Visible，其余 Collapsed。</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int number => number,
            System.Collections.ICollection collection => collection.Count,
            null => 0,
            _ => 1,
        };

        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}