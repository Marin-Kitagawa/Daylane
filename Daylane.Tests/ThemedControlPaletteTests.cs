using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Daylane.Controls;

// Avalonia's headless platform can be set up only once per process, and its dispatcher belongs
// to the session's own UI thread. It does not survive xunit running test classes on several
// threads at once, which shows up as "a different thread owns it" -- and only in a full-suite
// run, never when this class is run under a filter by itself.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Daylane.Tests;

/// <summary>
/// Regression coverage for a post-launch bug: the Day timeline (and every other
/// ThemedControl-derived custom-drawn control -- TimelineLane, TimelineRuler, HourlyChart,
/// AwayFill) rendered entirely in TimelinePalette's deliberate magenta fallback.
///
/// There were two independent defects, both established empirically against the real
/// ThemedControl/TimelinePalette classes (not a stand-in dictionary). Each was confirmed by
/// reverting it alone and watching this test go red:
///
/// 1. TimelinePalette.Lookup called IResourceHost.TryGetResource, which consults only the
///    host's *own* resources and does not walk the resource parent chain. A Control host
///    therefore never reached the app-level ThemeDictionaries that hold these tokens, so all
///    eleven lookups missed and every field came back magenta -- even for a fully attached
///    control with the correct ActualThemeVariant. The fix is TryFindResource, which walks up
///    to Application. This is also why MainWindowViewModel's IdlePalette always looked right:
///    it passes Application.Current as the host, so the non-walking lookup happened to succeed.
///
/// 2. <c>Palette => _palette ??= TimelinePalette.Resolve(this, ActualThemeVariant)</c> can be
///    read before the control is attached. A detached control has no resource parent chain, so
///    even TryFindResource misses, and `??=` then cached that miss permanently:
///    ActualThemeVariantChanged fires only on a later *variant change*, never on the attach
///    itself, so nothing ever corrected it. The fix is to drop the cache in
///    OnAttachedToVisualTree.
///
/// Fixing either one alone still renders magenta; both are required.
///
/// This uses Avalonia.Headless (a real Application, a real Window, real resource resolution,
/// no OS window) rather than TimelinePaletteTests' own hand-built ResourceDictionary host:
/// that older test only proves Resolve works given a host that *already holds* the tokens,
/// which is exactly why it -- and 107 other green tests, and three reviews -- missed this. It
/// never exercised a real control reaching Application-level ThemeDictionaries through the
/// live attach path, which is the path that actually failed.
/// </summary>
public class ThemedControlPaletteTests
{
    public class HeadlessTestApp : Application
    {
        public override void Initialize()
        {
            // FluentTheme supplies Window's ControlTemplate. Without it Window has no template,
            // so its Content is never realized into the visual tree and the control under test
            // never actually attaches -- which would make these assertions vacuous.
            Styles.Add(new FluentTheme());
            Resources.MergedDictionaries.Add(BuildThemeDictionary());
        }

        private static ResourceDictionary BuildThemeDictionary()
        {
            var dict = new ResourceDictionary();
            dict.ThemeDictionaries[ThemeVariant.Light] = BuildVariant(Colors.Black);
            dict.ThemeDictionaries[ThemeVariant.Dark] = BuildVariant(Colors.White);
            return dict;
        }

        // The exact eleven tokens TimelinePalette.Resolve looks up (Controls/TimelinePalette.cs).
        // One color per variant is enough: the assertions only care whether resolution succeeded
        // (anything but magenta), not which concrete color came back.
        private static ResourceDictionary BuildVariant(Color textColor) => new()
        {
            ["AppTextBrush"] = new SolidColorBrush(textColor),
            ["AppMutedBrush"] = new SolidColorBrush(textColor),
            ["AppAccentBrush"] = new SolidColorBrush(textColor),
            ["AppGridMinorBrush"] = new SolidColorBrush(textColor),
            ["AppGridMajorBrush"] = new SolidColorBrush(textColor),
            ["AppBorderBrush"] = new SolidColorBrush(textColor),
            ["AppSurfaceBrush"] = new SolidColorBrush(textColor),
            ["AppIdleBrush"] = new SolidColorBrush(textColor),
            ["AppIdleStripeBrush"] = new SolidColorBrush(textColor),
            ["AppIdleSoftBrush"] = new SolidColorBrush(textColor),
            ["AppIdleSoftStripeBrush"] = new SolidColorBrush(textColor)
        };
    }

    // ThemedControl.Palette is `protected`; this test-only subclass is how Daylane.Tests
    // (already granted InternalsVisibleTo by Daylane.csproj) reaches it without changing the
    // production access modifier.
    private sealed class ProbeControl : ThemedControl
    {
        public TimelinePalette GetPalette() => Palette;
    }

    /// <summary>
    /// Both scenarios share one session and one dispatch on purpose: the headless platform can
    /// only be initialized once per process, so a second session (or a second dispatch under
    /// per-method isolation, which rebuilds the Application) fails with "a different thread
    /// owns it" once the rest of the suite is running alongside it.
    /// </summary>
    [Fact]
    public async Task ThemedControl_ResolvesRealPaletteColors_ThroughTheLiveApplicationTree()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            // Scenario 1 -- a control that only ever reads Palette after it is attached, on the
            // Application's startup variant. Production's Appearance="system" is exactly this
            // (RequestedThemeVariant left at Default, ActualThemeVariant resolved to a concrete
            // Light/Dark). This is the half that a *fully attached* control still got wrong
            // before Lookup switched to TryFindResource.
            //
            // It runs first, while the Application is untouched: assigning ThemeVariant.Default
            // back after an explicit variant does not re-resolve to a concrete Light/Dark, and
            // the dictionary above deliberately has no Default entry.
            var plain = new ProbeControl();
            var plainWindow = new Window { Content = plain };
            plainWindow.Show();
            AssertAttached(plain);

            Assert.NotEqual(Colors.Magenta, plain.GetPalette().Text);
            plainWindow.Close();

            // Scenario 2 -- the shipped bug: Palette is read before the control is attached.
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            var early = new ProbeControl();

            // Detached, there is no resource parent chain to resolve against, so this really is
            // the magenta fallback. The bug was that `??=` then cached it forever.
            Assert.Equal(Colors.Magenta, early.GetPalette().Text);

            var earlyWindow = new Window { Content = early };
            earlyWindow.Show();
            AssertAttached(early);

            // Attaching must invalidate that pre-attach miss so the control repaints against the
            // now-reachable theme resources.
            Assert.NotEqual(Colors.Magenta, early.GetPalette().Text);
            earlyWindow.Close();
        }, CancellationToken.None);
    }

    private static void AssertAttached(Control control) =>
        Assert.True(
            Avalonia.VisualTree.VisualExtensions.IsAttachedToVisualTree(control),
            "Control never attached to the visual tree; the palette assertions would pass vacuously.");
}
