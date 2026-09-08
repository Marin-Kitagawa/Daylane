using Daylane.Services;
using Xunit;

namespace Daylane.Tests;

public class DailyStatsStoreTests
{
    [Fact]
    public void Constructor_CreatesDatabaseAtSuppliedPath()
    {
        using var temp = new TempDatabase();

        using var store = new DailyStatsStore(temp.DatabasePath);

        Assert.Equal(temp.DatabasePath, store.DatabasePath);
        Assert.True(File.Exists(temp.DatabasePath));
    }
}
