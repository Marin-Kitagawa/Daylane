using System.Globalization;
using Avalonia.Data.Converters;
using Daylane.Models;

namespace Daylane.Converters;

/// <summary>
/// Formats one ignore rule row for display: the process alone, or the process plus its
/// optional title-keyword restriction.
/// </summary>
internal sealed class IgnoreRuleDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IgnoreRule rule
            ? rule.TitleKeyword is { Length: > 0 } keyword ? $"{rule.ProcessName} — {keyword}" : rule.ProcessName
            : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
