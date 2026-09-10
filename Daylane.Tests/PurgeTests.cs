using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class PurgeTests
{
    private static ForegroundApp App(string process) =>
        new(process, $@"C:\Apps\{process}.exe", process, false);

    private static long Count(TempDatabase temp, string table)
    {
        using var connection = temp.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Purge_EmptiesTheCaptureTables()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        long id = store.OpenSegment(App("chrome"), start);
        store.CloseSegment(id, start.AddMinutes(1), 3, 4);
        store.Flush();
        Assert.Equal(1L, Count(temp, "ActivitySegment"));

        int deleted = store.PurgeRecordedActivity();

        Assert.Equal(0L, Count(temp, "ActivitySegment"));
        Assert.True(deleted >= 1, $"expected at least the one segment to be counted, got {deleted}");
    }

    [Fact]
    public void Purge_LeavesSettingsAlone()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        var settings = new SettingsService(temp.ConnectionString);
        settings.Update(s => s with { RetentionDays = 45, CheckForUpdates = true });

        store.PurgeRecordedActivity();

        // Clearing your history must not clear your preferences.
        var reread = new SettingsService(temp.ConnectionString).Current;
        Assert.Equal(45, reread.RetentionDays);
        Assert.True(reread.CheckForUpdates);
    }

    [Fact]
    public void Purge_LeavesThisDevicesIdentityAlone()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        long before = Count(temp, "Devices");
        Assert.True(before >= 1, "the store seeds a self device row on open");

        store.PurgeRecordedActivity();

        Assert.Equal(before, Count(temp, "Devices"));
    }

    [Fact]
    public void Purge_OnAnEmptyDatabase_ReportsNothingDeletedAndDoesNotThrow()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        Assert.Equal(0, store.PurgeRecordedActivity());
    }

    [Fact]
    public void Purge_IsRepeatable()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        store.CloseSegment(store.OpenSegment(App("chrome"), start), start.AddMinutes(1), 0, 0);

        store.PurgeRecordedActivity();
        store.PurgeRecordedActivity();

        Assert.Equal(0L, Count(temp, "ActivitySegment"));
    }

    [Fact]
    public void Purge_LeavesTheDatabaseUsable()
    {
        // VACUUM rebuilds the file. If it left the connection or the schema in a bad state,
        // the very next write would fail -- which is the failure a user would hit immediately.
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        store.PurgeRecordedActivity();

        DateTime start = DateTime.UtcNow;
        long id = store.OpenSegment(App("notepad"), start);
        store.CloseSegment(id, start.AddMinutes(1), 1, 1);
        store.Flush();

        Assert.Equal(1L, Count(temp, "ActivitySegment"));
    }
}
