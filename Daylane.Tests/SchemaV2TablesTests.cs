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

    private static void InsertV1OpenAppSegment(SqliteConnection connection, string processName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO OpenAppSegment (StartUtc, EndUtc, ProcessName, ExePath, DisplayName)
            VALUES ('2026-09-01T10:00:00.0000000Z', '2026-09-01T10:05:00.0000000Z',
                    $process, $path, $display);
            """;
        command.Parameters.AddWithValue("$process", processName);
        command.Parameters.AddWithValue("$path", $@"C:\Apps\{processName}.exe");
        command.Parameters.AddWithValue("$display", processName);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string? StringScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    // What every shared entity's UpdatedAt column defaults to when a row has never been
    // touched locally. Last-write-wins sync compares against this exact literal, so a
    // typo here would corrupt merge semantics silently.
    private const string TombstoneDefault = "'1970-01-01T00:00:00Z'";

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

    [Fact]
    public void V2_ExcludesIdleFromAppGroups_OpenAppSegment()
    {
        // OpenAppSegment has no IsIdle column, so this half of the exclusion is enforced
        // by process name alone. Nothing stops OpenAppTracker from one day emitting an
        // 'Idle' row, so this must be pinned independently of the ActivitySegment case.
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, [Migrations.Scripts[0]], temp.DatabasePath);
        InsertV1OpenAppSegment(connection, "code");
        InsertV1OpenAppSegment(connection, "Idle");

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups WHERE Id = 'Idle';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups;"));
    }

    [Theory]
    [InlineData("SuperCategories")]
    [InlineData("Categories")]
    [InlineData("AppGroups")]
    [InlineData("AppGroupMembers")]
    [InlineData("AppIcons")]
    [InlineData("Devices")]
    public void V2_SharedEntityHasTombstoneDefaultAndSoftDelete(string table)
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(
            TombstoneDefault,
            StringScalar(
                connection,
                $"SELECT dflt_value FROM pragma_table_info('{table}') WHERE name = 'UpdatedAt';"));

        Assert.Equal(
            1L,
            Scalar(
                connection,
                $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = 'DeletedAt';"));
    }

    [Theory]
    [InlineData("IX_AppGroupMembers_Group")]
    [InlineData("IX_SyncOutbox_Due")]
    public void V2_CreatesIndex(string index)
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(
            1L,
            Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}';"));
    }
}
