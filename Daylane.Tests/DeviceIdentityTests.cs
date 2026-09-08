using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class DeviceIdentityTests
{
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void EnsureSelfDevice_CreatesOneSelfRow()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        string deviceId = DeviceIdentity.EnsureSelfDevice(connection);

        Assert.True(Guid.TryParse(deviceId, out _));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Devices WHERE IsSelf = 1;"));
    }

    [Fact]
    public void EnsureSelfDevice_IsIdempotent()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        string first = DeviceIdentity.EnsureSelfDevice(connection);
        string second = DeviceIdentity.EnsureSelfDevice(connection);

        Assert.Equal(first, second);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Devices;"));
    }

    [Fact]
    public void EnsureSelfDevice_RewritesLocalPlaceholderRows()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO ActivitySegment
                    (StartUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount)
                VALUES ('2026-09-01T10:00:00.0000000Z', 'code', 'C:\Apps\code.exe', 'code', 0, 0, 0);
                INSERT INTO OpenAppSegment (StartUtc, ProcessName, ExePath, DisplayName)
                VALUES ('2026-09-01T10:00:00.0000000Z', 'code', 'C:\Apps\code.exe', 'code');
                INSERT INTO DailyInput (LogDate, DeviceId, KeyCount, MouseClickCount)
                VALUES ('2026-09-01', 'local', 5, 1);
                """;
            seed.ExecuteNonQuery();
        }

        string deviceId = DeviceIdentity.EnsureSelfDevice(connection);

        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM ActivitySegment WHERE DeviceId = 'local';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM OpenAppSegment WHERE DeviceId = 'local';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM DailyInput WHERE DeviceId = 'local';"));
        Assert.Equal(
            1L,
            Scalar(connection, $"SELECT COUNT(*) FROM ActivitySegment WHERE DeviceId = '{deviceId}';"));
    }
}
