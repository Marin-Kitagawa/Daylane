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

    // Every table PurgeRecordedActivity is supposed to touch, seeded independently, so a
    // table silently dropped from the purge list -- not just ActivitySegment -- turns this
    // test red. Telling a user their history is gone while some of it is still on disk is
    // not a cosmetic bug, so every name gets its own seed row and its own assertion.
    private static readonly string[] AllPurgedTables =
    [
        "ActivitySegment",
        "OpenAppSegment",
        "DailyInput",
        "ProcessPaths",
        "AppIcons",
        "SyncOutbox",
        "SyncCursor"
    ];

    [Fact]
    public void Purge_EmptiesEveryTableInThePurgeList()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-10);

        // ActivitySegment, OpenAppSegment, DailyInput: the store has methods for these.
        long segmentId = store.OpenSegment(App("chrome"), start);
        store.CloseSegment(segmentId, start.AddMinutes(1), 1, 1);

        long openAppId = store.OpenOpenAppSegment(App("edge"), start);
        store.CloseOpenAppSegment(openAppId, start.AddMinutes(1));

        store.Enqueue(new InputEvent(start, "Key", 0, 0));
        store.Flush();

        // ProcessPaths, AppIcons, SyncOutbox, SyncCursor: no store method writes these, so
        // seed them directly. This is setup for the thing under test (PurgeRecordedActivity),
        // not the construct-the-state-you-verify anti-pattern.
        using (var connection = temp.Open())
        {
            using var processPaths = connection.CreateCommand();
            processPaths.CommandText =
                "INSERT INTO ProcessPaths (ProcessName, ExePath, SeenAt) VALUES ($p, $e, $s);";
            processPaths.Parameters.AddWithValue("$p", "chrome");
            processPaths.Parameters.AddWithValue("$e", @"C:\Apps\chrome.exe");
            processPaths.Parameters.AddWithValue("$s", "2026-01-01T00:00:00Z");
            processPaths.ExecuteNonQuery();

            using var appIcons = connection.CreateCommand();
            appIcons.CommandText = "INSERT INTO AppIcons (ProcessName, IconPng) VALUES ($p, $icon);";
            appIcons.Parameters.AddWithValue("$p", "chrome");
            appIcons.Parameters.AddWithValue("$icon", new byte[] { 1, 2, 3 });
            appIcons.ExecuteNonQuery();

            using var syncOutbox = connection.CreateCommand();
            syncOutbox.CommandText = """
                INSERT INTO SyncOutbox (Op, Entity, EntityPk, Payload, CreatedAt, NextRetryAt)
                VALUES ($op, $entity, $pk, $payload, $created, $retry);
                """;
            syncOutbox.Parameters.AddWithValue("$op", "upsert");
            syncOutbox.Parameters.AddWithValue("$entity", "ActivitySegment");
            syncOutbox.Parameters.AddWithValue("$pk", "1");
            syncOutbox.Parameters.AddWithValue("$payload", "{}");
            syncOutbox.Parameters.AddWithValue("$created", "2026-01-01T00:00:00Z");
            syncOutbox.Parameters.AddWithValue("$retry", "2026-01-01T00:00:00Z");
            syncOutbox.ExecuteNonQuery();

            using var syncCursor = connection.CreateCommand();
            syncCursor.CommandText = "INSERT INTO SyncCursor (Entity) VALUES ($entity);";
            syncCursor.Parameters.AddWithValue("$entity", "ActivitySegment");
            syncCursor.ExecuteNonQuery();
        }

        foreach (string table in AllPurgedTables)
        {
            Assert.True(Count(temp, table) >= 1, $"setup failed to seed {table}");
        }

        store.PurgeRecordedActivity();

        foreach (string table in AllPurgedTables)
        {
            Assert.True(Count(temp, table) == 0, $"{table} was not emptied by the purge");
        }
    }
}
