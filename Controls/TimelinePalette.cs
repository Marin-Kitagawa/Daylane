using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Daylane.Controls;

/// <summary>
/// The colors TimelineBar draws with, resolved from theme resources. Extracted from the
/// control so it can be unit-tested without a UI harness, and so a variant change is a
/// single cache rebuild rather than a hunt through Render.
/// </summary>
internal sealed record TimelinePalette
{
    public required Color Text { get; init; }
    public required Color Muted { get; init; }
    public required Color Accent { get; init; }
    public required Color GridMinor { get; init; }
    public required Color GridMajor { get; init; }
    public required Color Border { get; init; }
    public required Color Surface { get; init; }
    public required Color IdleFill { get; init; }
    public required Color IdleStripe { get; init; }
    public required Color IdleSoftFill { get; init; }
    public required Color IdleSoftStripe { get; init; }

    internal static TimelinePalette Resolve(IResourceHost host, ThemeVariant variant) => new()
    {
        Text = Lookup(host, variant, "AppTextBrush"),
        Muted = Lookup(host, variant, "AppMutedBrush"),
        Accent = Lookup(host, variant, "AppAccentBrush"),
        GridMinor = Lookup(host, variant, "AppGridMinorBrush"),
        GridMajor = Lookup(host, variant, "AppGridMajorBrush"),
        Border = Lookup(host, variant, "AppBorderBrush"),
        Surface = Lookup(host, variant, "AppSurfaceBrush"),
        IdleFill = Lookup(host, variant, "AppIdleBrush"),
        IdleStripe = Lookup(host, variant, "AppIdleStripeBrush"),
        IdleSoftFill = Lookup(host, variant, "AppIdleSoftBrush"),
        IdleSoftStripe = Lookup(host, variant, "AppIdleSoftStripeBrush")
    };

    // TryFindResource, not TryGetResource: TryGetResource only consults the host's own resources,
    // so a Control host never reached the app-level ThemeDictionaries where these tokens live and
    // every lookup fell through to magenta. TryFindResource walks the resource parent chain up to
    // Application. It still resolves for a host that holds the tokens directly (the view model
    // passes Application.Current), since the walk starts at the host itself.
    private static Color Lookup(IResourceHost host, ThemeVariant variant, string key)
        => host.TryFindResource(key, variant, out object? value) && value is ISolidColorBrush brush
            ? brush.Color
            : Colors.Magenta; // Deliberately loud: a missing token should be visible, not silent.
}
