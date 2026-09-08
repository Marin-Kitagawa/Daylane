using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Daylane.Converters;

/// <summary>
/// Backs a RadioButton.IsChecked binding against a single string property (here,
/// DaylaneSettings.Appearance) where ConverterParameter carries the value this particular
/// radio represents. Avalonia's RadioButton group fires ConvertBack for both the button that
/// becomes checked (true) and the one that becomes unchecked (false); returning
/// BindingOperations.DoNothing for the false case stops that second call from clobbering the
/// value the true call just set.
/// </summary>
internal sealed class StringEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : BindingOperations.DoNothing;
}
