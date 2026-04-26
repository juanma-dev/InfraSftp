using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace InfraSftp.Converters;

// Parses "#RRGGBB" / "#AARRGGBB" strings coming from Models (which must stay
// free of Avalonia.Media references) into SolidColorBrush for XAML bindings.
public class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { return new SolidColorBrush(Color.Parse(hex)); }
            catch { return Brushes.Gray; }
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
