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
    public void CheckForUpdates_SurvivesARoundTripThroughTheStore()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var service = new SettingsService(temp.ConnectionString);
        Assert.True(service.Update(s => s with { CheckForUpdates = true }));

        // A fresh service reads the persisted row, not the in-memory value.
        var reread = new SettingsService(temp.ConnectionString).Current;
        Assert.True(reread.CheckForUpdates);
    }

    [Fact]
    public void Normalize_LeavesTheUpdateKeyAlone()
    {
        var settings = (new DaylaneSettings() with { CheckForUpdates = true }).Normalize();

        Assert.True(settings.CheckForUpdates);
    }
}
