using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class SchemaV2Tests
{
    private const string Epoch = "1970-01-01T00:00:00Z";

    private static HashSet<string> ColumnNames(SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void InsertV1Segment(
        SqliteConnection connection, string startUtc, string processName, int isIdle)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActivitySegment
                (StartUtc, EndUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount)
            VALUES ($start, $end, $process, $path, $display, $idle, 0, 0);
            """;
        command.Parameters.AddWithValue("$start", startUtc);
        command.Parameters.AddWithValue("$end", startUtc);
        command.Parameters.AddWithValue("$process", processName);
        command.Parameters.AddWithValue("$path", $@"C:\Apps\{processName}.exe");
        command.Parameters.AddWithValue("$display", processName);
        command.Parameters.AddWithValue("$idle", isIdle);
        command.ExecuteNonQuery();
    }

    /// <summary>Applies V1 only, so a test can seed rows the way the shipped release would
    /// have, then upgrade them.</summary>
    private static void ApplyV1Only(SqliteConnection connection, string path)
        => Migrations.Apply(connection, [Migrations.Scripts[0]], path);

    [Fact]
    public void V2_AddsActivityColumns()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        var columns = ColumnNames(connection, "ActivitySegment");
        Assert.Contains("WindowTitle", columns);
        Assert.Contains("UrlHost", columns);
        Assert.Contains("LocalDate", columns);
        Assert.Contains("LocalHour", columns);
        Assert.Contains("DeviceId", columns);
        Assert.Contains("RemoteId", columns);
        Assert.Contains("UpdatedAt", columns);
        Assert.Contains("Origin", columns);
        Assert.Contains("Excluded", columns);
    }

    [Fact]
    public void V2_AddsOpenAppColumns()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        var columns = ColumnNames(connection, "OpenAppSegment");
        Assert.Contains("LocalDate", columns);
        Assert.Contains("LocalHour", columns);
        Assert.Contains("DeviceId", columns);
        Assert.Contains("RemoteId", columns);
        Assert.Contains("UpdatedAt", columns);
        Assert.Contains("Origin", columns);
    }

    [Fact]
    public void V2_BackfillsLocalDateAndHourFromLocalTimeZone()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        ApplyV1Only(connection, temp.DatabasePath);

        var startUtc = new DateTime(2026, 3, 15, 22, 40, 0, DateTimeKind.Utc);
        InsertV1Segment(connection, startUtc.ToString("O"), "code", isIdle: 0);

        Migrations.Apply(connection, temp.DatabasePath);

        DateTime expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, TimeZoneInfo.Local);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT LocalDate, LocalHour FROM ActivitySegment;";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expectedLocal.ToString("yyyy-MM-dd"), reader.GetString(0));
        Assert.Equal(expectedLocal.Hour, reader.GetInt32(1));
    }

    [Fact]
    public void V2_BackfillsRemoteIdForPreExistingRows()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        ApplyV1Only(connection, temp.DatabasePath);
        InsertV1Segment(connection, "2026-09-01T10:00:00.0000000Z", "code", isIdle: 0);

        Migrations.Apply(connection, temp.DatabasePath);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Id, RemoteId FROM ActivitySegment;";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(reader.GetInt64(0), reader.GetInt64(1));
    }

    [Fact]
    public void V2_TriggerAssignsRemoteIdOnLocalInsert()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        InsertV1Segment(connection, "2026-09-02T08:00:00.0000000Z", "chrome", isIdle: 0);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Id, RemoteId FROM ActivitySegment;";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(reader.GetInt64(0), reader.GetInt64(1));
    }

    [Fact]
    public void V2_BackfillsUpdatedAtFromEndUtc()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        ApplyV1Only(connection, temp.DatabasePath);
        InsertV1Segment(connection, "2026-09-01T10:00:00.0000000Z", "code", isIdle: 0);

        Migrations.Apply(connection, temp.DatabasePath);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT UpdatedAt FROM ActivitySegment;";
        Assert.NotEqual(Epoch, (string)read.ExecuteScalar()!);
    }
}
