using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class SettingsServiceTests
{
    private static TempDatabase MigratedDatabase()
    {
        var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        return temp;
    }

    [Fact]
    public void Current_OnEmptyStore_ReturnsDocumentedDefaults()
    {
        using var temp = MigratedDatabase();

        var service = new SettingsService(temp.ConnectionString);

        Assert.Equal("system", service.Current.Appearance);
        Assert.Equal(15, service.Current.IdleThresholdMinutes);
        Assert.True(service.Current.TrackingEnabled);
        Assert.False(service.Current.ShowWindowOnAutoStart);
        Assert.True(service.Current.MinimizeToTray);
        Assert.Equal(0, service.Current.RetentionDays);
    }

    [Fact]
    public void Update_WithNoSeededRow_StillPersists()
    {
        // The migration seeds row 1, so this is the state no supported path produces -- which is
        // the point: an UPDATE ... WHERE Id = 1 against a missing row reports success and writes
        // nothing, so settings would stop persisting with no error to notice. The upsert must
        // create the row instead.
        using var temp = MigratedDatabase();
        using (var connection = temp.Open())
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM SettingsStore;";
            delete.ExecuteNonQuery();
        }

        var service = new SettingsService(temp.ConnectionString);
        service.Update(s => s with { Appearance = "dark", RetentionDays = 90 });

        var reloaded = new SettingsService(temp.ConnectionString);
        Assert.Equal("dark", reloaded.Current.Appearance);
        Assert.Equal(90, reloaded.Current.RetentionDays);
    }

    [Fact]
    public void Update_PersistsAcrossInstances()
    {
        using var temp = MigratedDatabase();
        var service = new SettingsService(temp.ConnectionString);

        service.Update(s => s with { Appearance = "dark", RetentionDays = 90 });

        var reloaded = new SettingsService(temp.ConnectionString);
        Assert.Equal("dark", reloaded.Current.Appearance);
        Assert.Equal(90, reloaded.Current.RetentionDays);
    }

    [Fact]
    public void Update_RaisesChanged()
    {
        using var temp = MigratedDatabase();
        var service = new SettingsService(temp.ConnectionString);
        SettingsChangedEventArgs? observed = null;
        service.Changed += (_, e) => observed = e;

        bool persisted = service.Update(s => s with { Appearance = "light" });

        Assert.NotNull(observed);
        Assert.Equal("light", observed!.Settings.Appearance);
        Assert.True(observed.Persisted);
        Assert.True(persisted);
    }

    [Fact]
    public void Update_WhenTheWriteFails_ReportsItAndStillAppliesInMemory()
    {
        using var temp = MigratedDatabase();
        var service = new SettingsService(temp.ConnectionString);
        SettingsChangedEventArgs? observed = null;
        service.Changed += (_, e) => observed = e;

        // Stands in for the real failure -- a backup tool holding the database, an I/O error --
        // in the only way a test can make Write throw deterministically.
        using (var connection = temp.Open())
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE SettingsStore;";
            drop.ExecuteNonQuery();
        }

        bool persisted = service.Update(s => s with
        {
            RecordWindowTitles = true,
            IgnoreRules = new[] { new IgnoreRule("Solitaire", null) }
        });

        // Applied for this session: silently ignoring a privacy or tracking change because the
        // file was locked would be worse than not persisting it. But the caller and the
        // subscribers are told, because this change is gone at the next restart -- and the
        // retroactive recompute must not run against a rule that was never stored.
        Assert.False(persisted);
        Assert.NotNull(observed);
        Assert.False(observed!.Persisted);
        Assert.True(service.Current.RecordWindowTitles);
        Assert.Single(service.Current.IgnoreRules!);

        // And the stored view does not move: the retroactive recompute reads this, so it can
        // never judge the whole table by a rule that is not in the database beside it.
        Assert.False(service.Persisted.RecordWindowTitles);
        Assert.Empty(service.Persisted.IgnoreRules!);
    }

    [Fact]
    public void Update_PreservesKeysItDoesNotKnow()
    {
        using var temp = MigratedDatabase();
        using (var connection = temp.Open())
        {
            using var seed = connection.CreateCommand();
            seed.CommandText =
                """UPDATE SettingsStore SET Data = '{"appearance":"dark","futureFlag":true}' WHERE Id = 1;""";
            seed.ExecuteNonQuery();
        }

        var service = new SettingsService(temp.ConnectionString);
        service.Update(s => s with { RetentionDays = 30 });

        using var read = temp.Open();
        using var command = read.CreateCommand();
        command.CommandText = "SELECT Data FROM SettingsStore WHERE Id = 1;";
        Assert.Contains("futureFlag", (string)command.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(500, 240)]
    [InlineData(30, 30)]
    public void Normalize_ClampsIdleThreshold(int stored, int expected)
    {
        var settings = new DaylaneSettings { IdleThresholdMinutes = stored }.Normalize();

        Assert.Equal(expected, settings.IdleThresholdMinutes);
    }

    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("light", "light")]
    [InlineData("system", "system")]
    [InlineData("neon", "system")]
    public void Normalize_RejectsUnknownAppearance(string stored, string expected)
    {
        var settings = new DaylaneSettings { Appearance = stored }.Normalize();

        Assert.Equal(expected, settings.Appearance);
    }
}
