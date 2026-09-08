using Avalonia.Styling;
using Daylane.Models;

namespace Daylane.Services;

internal static class ThemeSelector
{
    /// <summary>ThemeVariant.Default follows the OS and updates live, so "system" needs no
    /// listener of our own.</summary>
    internal static ThemeVariant ToVariant(string appearance) => appearance switch
    {
        DaylaneSettings.AppearanceLight => ThemeVariant.Light,
        DaylaneSettings.AppearanceDark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    internal static bool IsDark(ThemeVariant actual) => actual == ThemeVariant.Dark;
}
