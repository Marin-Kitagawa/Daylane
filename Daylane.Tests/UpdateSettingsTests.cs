using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class UpdateSettingsTests
{
    [Fact]
    public void CheckForUpdates_DefaultsToOff()
    {
        // The promise: a default install contacts nothing. If this flips, the README is a lie.
        Assert.False(new DaylaneSettings().CheckForUpdates);
    }

    [Fact]
    public void LastSeenVersion_DefaultsToNull()
        => Assert.Null(new DaylaneSettings().LastSeenVersion);

    [Fact]
    public void CheckForUpdates_SurvivesARoundTripThroughTheStore()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var service = new SettingsService(temp.ConnectionString);
        Assert.True(service.Update(s => s with { CheckForUpdates = true, LastSeenVersion = "v1.2.3" }));

        // A fresh service reads the persisted row, not the in-memory value.
        var reread = new SettingsService(temp.ConnectionString).Current;
        Assert.True(reread.CheckForUpdates);
        Assert.Equal("v1.2.3", reread.LastSeenVersion);
    }

    [Fact]
    public void Normalize_LeavesTheUpdateKeysAlone()
    {
        var settings = (new DaylaneSettings() with
        {
            CheckForUpdates = true,
            LastSeenVersion = "v9.9.9"
        }).Normalize();

        Assert.True(settings.CheckForUpdates);
        Assert.Equal("v9.9.9", settings.LastSeenVersion);
    }
}
