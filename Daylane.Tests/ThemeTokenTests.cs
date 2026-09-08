using System.Xml.Linq;

namespace Daylane.Tests;

/// <summary>
/// Verifies App.axaml's theme dictionaries define every required brush token in both
/// the Light and Dark variants. This is asserted directly against the XAML as XML rather
/// than through AvaloniaXamlLoader: instantiating Daylane.App and loading its precompiled
/// XAML from a plain xunit test host throws
/// "XamlLoadException: No precompiled XAML found for Daylane.App, make sure to specify
/// x:Class and include your XAML file as AvaloniaResource" because no Avalonia platform
/// is initialized for the test process. The XML route below tests exactly the property
/// that matters -- that both x:Key="Light" and x:Key="Dark" dictionaries under
/// ThemeDictionaries define every token as a SolidColorBrush -- without needing a running
/// Avalonia application.
/// </summary>
public class ThemeTokenTests
{
    private static readonly string[] RequiredTokens =
    [
        "AppBgBrush", "AppSurfaceBrush", "AppSurfaceAltBrush", "AppBorderBrush",
        "AppTextBrush", "AppMutedBrush", "AppAccentBrush", "AppAccentSoftBrush",
        "AppIdleBrush", "AppIdleSoftBrush", "AppRowHoverBrush", "AppHoverBrush",
        "AppSubtleBrush", "AppSurfaceHoverBrush", "AppThumbBrush", "AppSegmentHoverBrush",
        "AppAccentBorderBrush", "AppAccentStrongBrush", "AppGridMinorBrush",
        "AppGridMajorBrush", "AppIdleStripeBrush", "AppIdleSoftStripeBrush",
        "AppIntensity1Brush", "AppIntensity2Brush", "AppIntensity3Brush"
    ];

    public static TheoryData<string> Tokens()
    {
        var data = new TheoryData<string>();
        foreach (string token in RequiredTokens)
        {
            data.Add(token);
        }

        return data;
    }

    private static string FindAppAxamlPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "App.axaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate App.axaml above the test output directory.");
    }

    private static XElement GetThemeDictionary(string key)
    {
        string path = FindAppAxamlPath();
        XDocument doc = XDocument.Load(path);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement? themeDictionaries = doc.Descendants(avalonia + "ResourceDictionary.ThemeDictionaries").FirstOrDefault();
        Assert.True(themeDictionaries is not null, "App.axaml has no ResourceDictionary.ThemeDictionaries block.");

        XElement? dictionary = themeDictionaries!
            .Elements(avalonia + "ResourceDictionary")
            .FirstOrDefault(e => (string?)e.Attribute(x + "Key") == key);

        Assert.True(dictionary is not null, $"No ResourceDictionary with x:Key=\"{key}\" found under ThemeDictionaries.");
        return dictionary!;
    }

    [Theory]
    [MemberData(nameof(Tokens))]
    public void BothVariants_DefineToken(string token)
    {
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string variant in new[] { "Light", "Dark" })
        {
            XElement dictionary = GetThemeDictionary(variant);
            XElement? brush = dictionary
                .Elements(avalonia + "SolidColorBrush")
                .FirstOrDefault(e => (string?)e.Attribute(x + "Key") == token);

            Assert.True(brush is not null, $"{token} is not defined for {variant}.");

            string? color = (string?)brush!.Attribute("Color");
            Assert.True(!string.IsNullOrWhiteSpace(color), $"{token} in {variant} has no Color value.");
        }
    }
}
