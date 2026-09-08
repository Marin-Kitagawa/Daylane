using Avalonia.Styling;
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class ThemeSelectorTests
{
    [Fact]
    public void ToVariant_MapsExplicitChoices()
    {
        Assert.Equal(ThemeVariant.Light, ThemeSelector.ToVariant(DaylaneSettings.AppearanceLight));
        Assert.Equal(ThemeVariant.Dark, ThemeSelector.ToVariant(DaylaneSettings.AppearanceDark));
    }

    [Fact]
    public void ToVariant_MapsSystemToDefault()
    {
        Assert.Equal(ThemeVariant.Default, ThemeSelector.ToVariant(DaylaneSettings.AppearanceSystem));
    }

    [Fact]
    public void ToVariant_FallsBackToDefault()
    {
        Assert.Equal(ThemeVariant.Default, ThemeSelector.ToVariant("neon"));
    }
}
