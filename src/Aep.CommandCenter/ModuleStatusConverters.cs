using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Aep.ModuleContracts;

namespace Aep.CommandCenter;

/// <summary>
/// Maps a ModuleStatusCardDto.Severity string ("ok"/"attention"/"alert") to
/// the accent color drawn on the left edge of that card. Falls back to the
/// "ok" color for anything unrecognized, rather than throwing - a future
/// severity value added on the API side should degrade gracefully here, not
/// crash the console.
/// </summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = new(Color.FromRgb(0x7F, 0xD1, 0xAE));
    private static readonly SolidColorBrush Attention = new(Color.FromRgb(0xE0, 0xA4, 0x58));
    private static readonly SolidColorBrush Alert = new(Color.FromRgb(0xE0, 0x58, 0x58));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            ModuleStatusSeverity.Attention => Attention,
            ModuleStatusSeverity.Alert => Alert,
            _ => Ok,
        };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses a TextBlock when its bound string is null/empty/whitespace -
/// used for ModuleStatusCardDto.Detail, which is legitimately null on some
/// cards (see collector-intelligence-engine's quality-flags card).
/// </summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
