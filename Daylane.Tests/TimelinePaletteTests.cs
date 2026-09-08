using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Daylane.Controls;

namespace Daylane.Tests;

/// <summary>
/// Verifies TimelinePalette.Resolve reads the ten timeline tokens from the correct
/// ThemeVariant dictionary. This resolves against a plain Border (a Control, hence an
/// IResourceHost) carrying its own ResourceDictionary.ThemeDictionaries rather than
/// against a real Daylane.App: as ThemeTokenTests.cs documents, instantiating
/// Daylane.App and calling AvaloniaXamlLoader.Load throws XamlLoadException in a plain
/// xunit test host because no Avalonia platform is initialized for the test process.
/// Resolving against a self-built resource host avoids that entirely -- resource lookup
/// is pure data traversal and needs no running platform -- while still proving exactly
/// what matters: that each palette field pulls from the right token key, under the
/// right variant.
/// </summary>
public class TimelinePaletteTests
{
    private static Control BuildHost()
    {
        var resources = new ResourceDictionary
        {
            ThemeDictionaries =
            {
                [ThemeVariant.Light] = new ResourceDictionary
                {
                    ["AppTextBrush"] = new SolidColorBrush(Color.Parse("#111827")),
                    ["AppMutedBrush"] = new SolidColorBrush(Color.Parse("#6B7280")),
                    ["AppAccentBrush"] = new SolidColorBrush(Color.Parse("#2F9E6B")),
                    ["AppGridMinorBrush"] = new SolidColorBrush(Color.Parse("#F3F4F6")),
                    ["AppGridMajorBrush"] = new SolidColorBrush(Color.Parse("#E5E7EB")),
                    ["AppBorderBrush"] = new SolidColorBrush(Color.Parse("#E5E7EB")),
                    ["AppIdleBrush"] = new SolidColorBrush(Color.Parse("#AEB4BE")),
                    ["AppIdleStripeBrush"] = new SolidColorBrush(Color.Parse("#A0A7B1")),
                    ["AppIdleSoftBrush"] = new SolidColorBrush(Color.Parse("#EEF0F3")),
                    ["AppIdleSoftStripeBrush"] = new SolidColorBrush(Color.Parse("#E4E7EB"))
                },
                [ThemeVariant.Dark] = new ResourceDictionary
                {
                    ["AppTextBrush"] = new SolidColorBrush(Color.Parse("#E5E7EB")),
                    ["AppMutedBrush"] = new SolidColorBrush(Color.Parse("#9AA3B2")),
                    ["AppAccentBrush"] = new SolidColorBrush(Color.Parse("#3DBE82")),
                    ["AppGridMinorBrush"] = new SolidColorBrush(Color.Parse("#1E222A")),
                    ["AppGridMajorBrush"] = new SolidColorBrush(Color.Parse("#2A2F39")),
                    ["AppBorderBrush"] = new SolidColorBrush(Color.Parse("#2A2F39")),
                    ["AppIdleBrush"] = new SolidColorBrush(Color.Parse("#5A616D")),
                    ["AppIdleStripeBrush"] = new SolidColorBrush(Color.Parse("#6A717D")),
                    ["AppIdleSoftBrush"] = new SolidColorBrush(Color.Parse("#23272F")),
                    ["AppIdleSoftStripeBrush"] = new SolidColorBrush(Color.Parse("#2C313A"))
                }
            }
        };

        return new Border { Resources = resources };
    }

    [Fact]
    public void Resolve_UsesLightTokens()
    {
        var palette = TimelinePalette.Resolve(BuildHost(), ThemeVariant.Light);

        Assert.Equal(Color.Parse("#111827"), palette.Text);
        Assert.Equal(Color.Parse("#6B7280"), palette.Muted);
        Assert.Equal(Color.Parse("#2F9E6B"), palette.Accent);
        Assert.Equal(Color.Parse("#F3F4F6"), palette.GridMinor);
        Assert.Equal(Color.Parse("#E5E7EB"), palette.GridMajor);
        Assert.Equal(Color.Parse("#E5E7EB"), palette.Border);
        Assert.Equal(Color.Parse("#AEB4BE"), palette.IdleFill);
        Assert.Equal(Color.Parse("#A0A7B1"), palette.IdleStripe);
        Assert.Equal(Color.Parse("#EEF0F3"), palette.IdleSoftFill);
        Assert.Equal(Color.Parse("#E4E7EB"), palette.IdleSoftStripe);
    }

    [Fact]
    public void Resolve_UsesDarkTokens()
    {
        var palette = TimelinePalette.Resolve(BuildHost(), ThemeVariant.Dark);

        Assert.Equal(Color.Parse("#E5E7EB"), palette.Text);
        Assert.Equal(Color.Parse("#9AA3B2"), palette.Muted);
        Assert.Equal(Color.Parse("#3DBE82"), palette.Accent);
        Assert.Equal(Color.Parse("#1E222A"), palette.GridMinor);
        Assert.Equal(Color.Parse("#2A2F39"), palette.GridMajor);
        Assert.Equal(Color.Parse("#2A2F39"), palette.Border);
        Assert.Equal(Color.Parse("#5A616D"), palette.IdleFill);
        Assert.Equal(Color.Parse("#6A717D"), palette.IdleStripe);
        Assert.Equal(Color.Parse("#23272F"), palette.IdleSoftFill);
        Assert.Equal(Color.Parse("#2C313A"), palette.IdleSoftStripe);
    }

    [Fact]
    public void Resolve_FallsBackToMagentaWhenTokenMissing()
    {
        var host = new Border { Resources = new ResourceDictionary() };

        var palette = TimelinePalette.Resolve(host, ThemeVariant.Light);

        Assert.Equal(Colors.Magenta, palette.Text);
    }
}
