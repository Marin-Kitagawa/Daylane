using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class RetentionPrunerTests
{
    private static readonly ForegroundApp TestApp = new("code", @"C:\Apps\code.exe", "code", false);

    private static void InsertSegment(SqliteConnection connection, string localDate)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActivitySegment
                (StartUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount, LocalDate, LocalHour)
            VALUES ($date || 'T10:00:00.0000000Z', 'code', 'C:\Apps\code.exe', 'code', 0, 0, 0, $date, 10);
            """;
        command.Parameters.AddWithValue("$date", localDate);
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Prune_WithZeroDays_KeepsEverything()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        InsertSegment(connection, "2020-01-01");

        int deleted = RetentionPruner.Prune(connection, 0, new DateTime(2026, 9, 8));

        Assert.Equal(0, deleted);
        Assert.Equal(1L, Count(connection, "ActivitySegment"));
    }

    [Fact]
    public void Prune_WithNegativeDays_KeepsEverything()
    {
        // The <= 0 guard exists for a hand-edited store that could hold a negative value even
        // though DaylaneSettings.Normalize() clamps it to non-negative before it ever gets there.
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        InsertSegment(connection, "2020-01-01");

        int deleted = RetentionPruner.Prune(connection, -5, new DateTime(2026, 9, 8));

        Assert.Equal(0, deleted);
        Assert.Equal(1L, Count(connection, "ActivitySegment"));
    }

    [Fact]
    public void Prune_DeletesOnlyRowsOlderThanWindow()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        InsertSegment(connection, "2026-09-08");
        InsertSegment(connection, "2026-09-01");
        InsertSegment(connection, "2026-06-01");

        int deleted = RetentionPruner.Prune(connection, 30, new DateTime(2026, 9, 8));

        Assert.Equal(1, deleted);
        Assert.Equal(2L, Count(connection, "ActivitySegment"));
    }

    [Fact]
    public void Prune_KeepsTheBoundaryDay()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        InsertSegment(connection, "2026-08-09");

        int deleted = RetentionPruner.Prune(connection, 30, new DateTime(2026, 9, 8));

        Assert.Equal(0, deleted);
        Assert.Equal(1L, Count(connection, "ActivitySegment"));
    }

    // The three tests above hand-populate LocalDate directly in the INSERT, which is exactly
    // what production code does NOT do at capture time (a real bug this branch shipped: the
    // migration backfills LocalDate once, but nothing wrote it on new rows). The tests below
    // route rows through the actual capture-path methods on DailyStatsStore, so a regression
    // that stops populating LocalDate/LocalHour on insert makes these fail even though the
    // hand-populated tests above would keep passing.

    [Fact]
    public void Prune_DeletesActivitySegmentRowCapturedThroughOpenSegment()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        DateTime oldStartUtc = DateTime.UtcNow.AddDays(-400);
        store.OpenSegment(TestApp, oldStartUtc);

        using var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();

        int deleted = RetentionPruner.Prune(connection, 30, DateTime.Now);

        Assert.Equal(1, deleted);
        Assert.Equal(0L, Count(connection, "ActivitySegment"));
    }

    [Fact]
    public void Prune_DeletesOpenAppSegmentRowCapturedThroughOpenOpenAppSegment()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        DateTime oldStartUtc = DateTime.UtcNow.AddDays(-400);
        store.OpenOpenAppSegment(TestApp, oldStartUtc);

        using var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();

        int deleted = RetentionPruner.Prune(connection, 30, DateTime.Now);

        Assert.Equal(1, deleted);
        Assert.Equal(0L, Count(connection, "OpenAppSegment"));
    }

    [Fact]
    public void Prune_DeletesDailyInputRowCapturedThroughFlush()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        DateTime oldUtc = DateTime.UtcNow.AddDays(-400);
        store.Enqueue(new InputEvent(oldUtc, "Key", 0, 0));
        store.Flush();

        using var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();

        int deleted = RetentionPruner.Prune(connection, 30, DateTime.Now);

        Assert.Equal(1, deleted);
        Assert.Equal(0L, Count(connection, "DailyInput"));
    }
}
