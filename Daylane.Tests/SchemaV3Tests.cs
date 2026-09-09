using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class SchemaV3Tests
{
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void V3_CreatesTitleSearchIndex()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(1L, Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_ActivitySegment_LocalDate_Title';"));
    }

    [Fact]
    public void V3_IsReachedFromAV2Database()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, new[] { Migrations.Scripts[0], Migrations.Scripts[1] }, temp.DatabasePath);
        Assert.Equal(2, Migrations.ReadUserVersion(connection));

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(Migrations.CurrentVersion, Migrations.ReadUserVersion(connection));
        Assert.Equal(3, Migrations.CurrentVersion);
    }
}
