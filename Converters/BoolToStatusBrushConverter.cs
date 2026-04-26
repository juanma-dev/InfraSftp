using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace InfraSftp.Converters;

// Green when connected, grey when not. Used by the tab dot and anywhere
// a live-session indicator is needed.
public class BoolToStatusBrushConverter : IValueConverter
{
    public static readonly BoolToStatusBrushConverter Instance = new();

    private static readonly IBrush Online = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush Offline = new SolidColorBrush(Color.Parse("#9CA3AF"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Online : Offline;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
