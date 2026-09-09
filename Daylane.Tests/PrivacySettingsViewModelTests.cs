using Daylane.Models;

namespace Daylane.Tests;

public class PrivacySettingsViewModelTests
{
    [Fact]
    public void RecordBrowserHost_SurvivesNormalize_WhenTitlesAreOn()
    {
        var settings = (new DaylaneSettings() with
        {
            RecordWindowTitles = true,
            RecordBrowserHost = true
        }).Normalize();

        // Complement of TurningTitlesOff_AlsoClearsBrowserHost below: the clause must only
        // clear the host when titles are off, not unconditionally.
        Assert.True(settings.RecordBrowserHost);
    }

    [Fact]
    public void TurningTitlesOff_AlsoClearsBrowserHost()
    {
        var settings = (new DaylaneSettings() with
        {
            RecordWindowTitles = true,
            RecordBrowserHost = true
        }).Normalize();

        var afterOff = (settings with { RecordWindowTitles = false }).Normalize();

        // Leaving RecordBrowserHost true while titles are off would be a setting that reads
        // as on and does nothing.
        Assert.False(afterOff.RecordBrowserHost);
    }
}
