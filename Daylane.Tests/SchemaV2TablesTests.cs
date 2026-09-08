using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class SchemaV2TablesTests
{
    private static void InsertV1Segment(SqliteConnection connection, string processName, int isIdle)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActivitySegment
                (StartUtc, EndUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount)
            VALUES ('2026-09-01T10:00:00.0000000Z', '2026-09-01T10:05:00.0000000Z',
                    $process, $path, $display, $idle, 0, 0);
            """;
        command.Parameters.AddWithValue("$process", processName);
        command.Parameters.AddWithValue("$path", $@"C:\Apps\{processName}.exe");
        command.Parameters.AddWithValue("$display", processName);
        command.Parameters.AddWithValue("$idle", isIdle);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Theory]
    [InlineData("SettingsStore")]
    [InlineData("SuperCategories")]
    [InlineData("Categories")]
    [InlineData("AppGroups")]
    [InlineData("AppGroupMembers")]
    [InlineData("ProcessPaths")]
    [InlineData("AppIcons")]
    [InlineData("Devices")]
    [InlineData("SyncOutbox")]
    [InlineData("SyncCursor")]
    [InlineData("AuthState")]
    public void V2_CreatesTable(string table)
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(
            1L,
            Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
    }

    [Fact]
    public void V2_SeedsSettingsAndAuthSingletonRows()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM SettingsStore WHERE Id = 1;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM AuthState WHERE Id = 1;"));

        using var data = connection.CreateCommand();
        data.CommandText = "SELECT Data FROM SettingsStore WHERE Id = 1;";
        Assert.Equal("{}", (string)data.ExecuteScalar()!);
    }

    [Fact]
    public void V2_SeedsBuiltinCategories()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(5L, Scalar(connection, "SELECT COUNT(*) FROM Categories WHERE Builtin = 1;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Categories WHERE Id = 'other';"));
    }

    [Fact]
    public void V2_BackfillsOneGroupPerProcessNameWithProcessNameAsId()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, [Migrations.Scripts[0]], temp.DatabasePath);
        InsertV1Segment(connection, "code", isIdle: 0);
        InsertV1Segment(connection, "chrome", isIdle: 0);

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups WHERE Id = 'code';"));
        Assert.Equal(
            2L,
            Scalar(connection, "SELECT COUNT(*) FROM AppGroupMembers WHERE ProcessName = GroupId;"));
    }

    [Fact]
    public void V2_ExcludesIdleFromAppGroups()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, [Migrations.Scripts[0]], temp.DatabasePath);
        InsertV1Segment(connection, "code", isIdle: 0);
        InsertV1Segment(connection, "Idle", isIdle: 1);

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups WHERE Id = 'Idle';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups;"));
    }
}
