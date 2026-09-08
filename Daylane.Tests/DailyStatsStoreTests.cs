using Daylane.Models;
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

    [Fact]
    public void Flush_AccumulatesKeyAndClickCountsAcrossMultipleFlushes()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        var now = DateTime.UtcNow;

        for (int i = 0; i < 7; i++)
        {
            store.Enqueue(new InputEvent(now, "Key", 0, 0));
        }

        for (int i = 0; i < 3; i++)
        {
            store.Enqueue(new InputEvent(now, "Mouse", 0, 0));
        }

        store.Flush();

        Assert.Equal((7L, 3L), store.GetTodayTotals());

        for (int i = 0; i < 4; i++)
        {
            store.Enqueue(new InputEvent(now, "Key", 0, 0));
        }

        store.Flush();

        // The upsert's ON CONFLICT must accumulate, not overwrite: 7 + 4 keys, clicks
        // untouched by the second flush.
        Assert.Equal((11L, 3L), store.GetTodayTotals());
    }
}
