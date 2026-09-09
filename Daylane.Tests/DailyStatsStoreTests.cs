using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Daylane.Tests;

public class DailyStatsStoreTests
{
    private static readonly ForegroundApp TestApp = new("code", @"C:\Apps\code.exe", "code", false);

    private const string EpochDefault = "1970-01-01T00:00:00Z";

    private static (string DeviceId, string UpdatedAt) ReadSyncColumns(
        DailyStatsStore store,
        string table,
        long id)
    {
        using var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DeviceId, UpdatedAt FROM {table} WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1));
    }

    [Fact]
    public void Constructor_CreatesDatabaseAtSuppliedPath()
    {
        using var temp = new TempDatabase();

        using var store = new DailyStatsStore(temp.DatabasePath);

        Assert.Equal(temp.DatabasePath, store.DatabasePath);
        Assert.True(File.Exists(temp.DatabasePath));
    }

    [Fact]
    public void Flush_AccumulatesKeyAndClickCountsAcrossMultipleFlushes()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        var now = DateTime.UtcNow;

        for (int i = 0; i < 7; i++)
        {
            store.Enqueue(new InputEvent(now, "Key", 0, 0));
        }

        for (int i = 0; i < 3; i++)
        {
            store.Enqueue(new InputEvent(now, "Mouse", 0, 0));
        }

        store.Flush();

        Assert.Equal((7L, 3L), store.GetTodayTotals());

        for (int i = 0; i < 4; i++)
        {
            store.Enqueue(new InputEvent(now, "Key", 0, 0));
        }

        store.Flush();

        // The upsert's ON CONFLICT must accumulate, not overwrite: 7 + 4 keys, clicks
        // untouched by the second flush.
        Assert.Equal((11L, 3L), store.GetTodayTotals());
    }

    // DeviceId and UpdatedAt carry DDL defaults ('local' and the epoch) that exist purely so the
    // v2 migration could add the columns to rows that already existed. Nothing re-runs over new
    // rows: DeviceIdentity promotes 'local' to a real id once per database lifetime, at the first
    // start after the upgrade. A capture path that omits these columns therefore writes rows that
    // stay wrong forever -- and, being defaults rather than errors, does so silently. These tests
    // drive the real write methods rather than hand-inserting, so an insert that drops the columns
    // again fails here.

    [Fact]
    public void OpenSegment_StampsRealDeviceIdAndUpdatedAt()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        long id = store.OpenSegment(TestApp, DateTime.UtcNow);

        var (deviceId, updatedAt) = ReadSyncColumns(store, "ActivitySegment", id);

        Assert.Equal(store.DeviceId, deviceId);
        Assert.NotEqual(DeviceIdentity.Placeholder, deviceId);
        Assert.True(Guid.TryParse(deviceId, out _), $"DeviceId was not a real device id: '{deviceId}'");
        Assert.NotEqual(EpochDefault, updatedAt);
        Assert.True(
            DateTime.Parse(updatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind)
                > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            $"UpdatedAt was not a capture-time stamp: '{updatedAt}'");
    }

    [Fact]
    public void OpenOpenAppSegment_StampsRealDeviceIdAndUpdatedAt()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        long id = store.OpenOpenAppSegment(TestApp, DateTime.UtcNow);

        var (deviceId, updatedAt) = ReadSyncColumns(store, "OpenAppSegment", id);

        Assert.Equal(store.DeviceId, deviceId);
        Assert.NotEqual(DeviceIdentity.Placeholder, deviceId);
        Assert.True(Guid.TryParse(deviceId, out _), $"DeviceId was not a real device id: '{deviceId}'");
        Assert.NotEqual(EpochDefault, updatedAt);
    }

    [Fact]
    public void CloseSegment_AdvancesUpdatedAt()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        long id = store.OpenSegment(TestApp, DateTime.UtcNow.AddMinutes(-5));
        string openedAt = ReadSyncColumns(store, "ActivitySegment", id).UpdatedAt;

        // "O" resolves to 100ns ticks, but a same-tick close would still be a legitimate pass;
        // sleep past the clock's granularity so a strictly-greater assertion is meaningful.
        Thread.Sleep(20);
        store.CloseSegment(id, DateTime.UtcNow, 12, 3);

        string closedAt = ReadSyncColumns(store, "ActivitySegment", id).UpdatedAt;

        Assert.NotEqual(EpochDefault, closedAt);
        Assert.True(
            string.CompareOrdinal(closedAt, openedAt) > 0,
            $"Closing did not advance UpdatedAt: opened '{openedAt}', closed '{closedAt}'");
    }

    [Fact]
    public void CloseOpenAppSegment_AdvancesUpdatedAt()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        long id = store.OpenOpenAppSegment(TestApp, DateTime.UtcNow.AddMinutes(-5));
        string openedAt = ReadSyncColumns(store, "OpenAppSegment", id).UpdatedAt;

        Thread.Sleep(20);
        store.CloseOpenAppSegment(id, DateTime.UtcNow);

        string closedAt = ReadSyncColumns(store, "OpenAppSegment", id).UpdatedAt;

        Assert.True(
            string.CompareOrdinal(closedAt, openedAt) > 0,
            $"Closing did not advance UpdatedAt: opened '{openedAt}', closed '{closedAt}'");
    }

    [Fact]
    public void UpdateOpenSegmentCounts_AdvancesUpdatedAt()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        long id = store.OpenSegment(TestApp, DateTime.UtcNow.AddMinutes(-5));
        string openedAt = ReadSyncColumns(store, "ActivitySegment", id).UpdatedAt;

        Thread.Sleep(20);
        store.UpdateOpenSegmentCounts(id, 9, 4);

        string bumpedAt = ReadSyncColumns(store, "ActivitySegment", id).UpdatedAt;

        Assert.True(
            string.CompareOrdinal(bumpedAt, openedAt) > 0,
            $"Counting did not advance UpdatedAt: opened '{openedAt}', bumped '{bumpedAt}'");
    }

    [Fact]
    public void CloseOrphanOpenSegments_AdvancesUpdatedAtOnBothTables()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        long segmentId = store.OpenSegment(TestApp, DateTime.UtcNow.AddMinutes(-5));
        long openAppId = store.OpenOpenAppSegment(TestApp, DateTime.UtcNow.AddMinutes(-5));
        string segmentOpenedAt = ReadSyncColumns(store, "ActivitySegment", segmentId).UpdatedAt;
        string openAppOpenedAt = ReadSyncColumns(store, "OpenAppSegment", openAppId).UpdatedAt;

        Thread.Sleep(20);
        store.CloseOrphanOpenSegments(DateTime.UtcNow);

        Assert.True(
            string.CompareOrdinal(
                ReadSyncColumns(store, "ActivitySegment", segmentId).UpdatedAt, segmentOpenedAt) > 0,
            "Orphan close did not advance ActivitySegment.UpdatedAt");
        Assert.True(
            string.CompareOrdinal(
                ReadSyncColumns(store, "OpenAppSegment", openAppId).UpdatedAt, openAppOpenedAt) > 0,
            "Orphan close did not advance OpenAppSegment.UpdatedAt");
    }
}
