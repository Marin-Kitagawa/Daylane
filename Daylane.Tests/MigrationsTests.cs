using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class MigrationsTests
{
    private static HashSet<string> TableNames(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Fact]
    public void Apply_OnFreshDatabase_CreatesV1Tables()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        var tables = TableNames(connection);
        Assert.Contains("DailyInput", tables);
        Assert.Contains("ActivitySegment", tables);
        Assert.Contains("OpenAppSegment", tables);
    }

    [Fact]
    public void Apply_SetsUserVersionToCurrent()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(Migrations.CurrentVersion, Migrations.ReadUserVersion(connection));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);
        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(Migrations.CurrentVersion, Migrations.ReadUserVersion(connection));
    }

    [Fact]
    public void Apply_PreservesExistingRows()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO DailyInput (LogDate, KeyCount, MouseClickCount) VALUES ('2026-09-01', 42, 7);";
            insert.ExecuteNonQuery();
        }

        Migrations.Apply(connection, temp.DatabasePath);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT KeyCount, MouseClickCount FROM DailyInput WHERE LogDate = '2026-09-01';";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal(7L, reader.GetInt64(1));
    }
}
