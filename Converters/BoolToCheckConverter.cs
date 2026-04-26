using System.Globalization;
using Avalonia.Data.Converters;

namespace InfraSftp.Converters;

// Renders a boolean as a check/empty glyph for menu-item toggles.
public class BoolToCheckConverter : IValueConverter
{
    public static readonly BoolToCheckConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "✓" : "·";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
