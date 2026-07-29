using System;
using System.Globalization;
using System.Windows.Data;

namespace K2.Core.Converters;

/// <summary>Display-only uppercase transform for bound text (device/home titles).
/// Non-string content (e.g. a TabItem.Header holding an icon TextBlock) passes
/// through unchanged rather than being stringified.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? s.ToUpperInvariant() : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
