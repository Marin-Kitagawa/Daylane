using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class RetentionPrunerTests
{
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

    private static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActivitySegment;";
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
        Assert.Equal(1L, Count(connection));
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
        Assert.Equal(2L, Count(connection));
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
        Assert.Equal(1L, Count(connection));
    }
}
