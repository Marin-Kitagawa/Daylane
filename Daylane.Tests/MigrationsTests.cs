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

    [Fact]
    public void Apply_WhenScriptFails_RollsBackAndKeepsVersion()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        int before = Migrations.ReadUserVersion(connection);

        string[] scripts =
        [
            .. Migrations.Scripts,
            "CREATE TABLE Good (Id INTEGER); SELECT this_is_not_valid_sql();"
        ];

        var exception = Assert.Throws<InvalidOperationException>(
            () => Migrations.Apply(connection, scripts, temp.DatabasePath));

        Assert.Equal(before, Migrations.ReadUserVersion(connection));
        Assert.DoesNotContain("Good", TableNames(connection));
        Assert.Contains("backup", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenFreshDatabaseScriptFails_MessageDoesNotMentionBackup()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        string[] scripts = ["CREATE TABLE Good (Id INTEGER); SELECT this_is_not_valid_sql();"];

        var exception = Assert.Throws<InvalidOperationException>(
            () => Migrations.Apply(connection, scripts, temp.DatabasePath));

        Assert.DoesNotContain("backup", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenUpgradingExistingDatabase_WritesBackup()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        SqliteConnection.ClearAllPools();
        string[] scripts = [.. Migrations.Scripts, "CREATE TABLE Later (Id INTEGER);"];

        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, scripts, temp.DatabasePath);
        }

        Assert.True(File.Exists($"{temp.DatabasePath}.bak.v{Migrations.CurrentVersion}"));
    }

    [Fact]
    public void Apply_OnFreshDatabase_WritesNoBackup()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(temp.DatabasePath)!, "*.bak.*"));
    }

    [Fact]
    public void Apply_WhenLaterScriptFails_MessageReflectsPartialUpgrade()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        string[] scripts =
        [
            .. Migrations.Scripts,
            "CREATE TABLE Partial (Id INTEGER);",
            "SELECT this_is_not_valid_sql();"
        ];
        int expectedAppliedThrough = Migrations.CurrentVersion + 1;

        var exception = Assert.Throws<InvalidOperationException>(
            () => Migrations.Apply(connection, scripts, temp.DatabasePath));

        Assert.DoesNotContain("left unchanged", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"upgraded to version {expectedAppliedThrough} before the failure", exception.Message);
        Assert.Equal(expectedAppliedThrough, Migrations.ReadUserVersion(connection));
        Assert.Contains("Partial", TableNames(connection));
    }

    [Fact]
    public void Apply_WhenUpgradingExistingDatabase_BackupIncludesUncheckpointedWalCommits()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        // Commit a row without an explicit checkpoint. Under WAL mode this write lands
        // in the -wal sidecar, not the main .db file, until something checkpoints it.
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO DailyInput (LogDate, KeyCount, MouseClickCount) VALUES ('2026-09-01', 42, 7);";
            insert.ExecuteNonQuery();
        }

        string walPath = $"{temp.DatabasePath}-wal";
        Assert.True(File.Exists(walPath));
        Assert.True(new FileInfo(walPath).Length > 0);

        string[] scripts = [.. Migrations.Scripts, "CREATE TABLE Later (Id INTEGER);"];
        Migrations.Apply(connection, scripts, temp.DatabasePath);

        string backupPath = $"{temp.DatabasePath}.bak.v{Migrations.CurrentVersion}";
        Assert.True(File.Exists(backupPath));

        using var backupConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ConnectionString);
        backupConnection.Open();
        using var read = backupConnection.CreateCommand();
        read.CommandText = "SELECT KeyCount, MouseClickCount FROM DailyInput WHERE LogDate = '2026-09-01';";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal(7L, reader.GetInt64(1));
    }
}
