using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class ExcludedRecomputeTests
{
    private static ForegroundApp App(string process, string? title) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title };

    private static long ExcludedCount(TempDatabase temp)
    {
        using var connection = temp.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActivitySegment WHERE Excluded = 1;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void AddingARule_ExcludesMatchingHistory()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        store.OpenSegment(App("WindowsTerminal", "npm download running"), DateTime.UtcNow);
        store.OpenSegment(App("WindowsTerminal", "editing a file"), DateTime.UtcNow);

        store.RecomputeExcluded(new[] { new IgnoreRule("WindowsTerminal", "download") });

        Assert.Equal(1L, ExcludedCount(temp));
    }

    [Fact]
    public void RemovingAllRules_RestoresPreviouslyExcludedTime()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        store.OpenSegment(App("Solitaire", "Klondike"), DateTime.UtcNow, excluded: true);
        Assert.Equal(1L, ExcludedCount(temp));

        store.RecomputeExcluded(Array.Empty<IgnoreRule>());

        // This is the reversibility promise: a mistaken rule must not be permanent.
        Assert.Equal(0L, ExcludedCount(temp));
    }

    [Fact]
    public void RowsWithNoTitle_OnlyMatchWholeProcessRules()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        store.OpenSegment(App("chrome", null), DateTime.UtcNow);

        store.RecomputeExcluded(new[] { new IgnoreRule("chrome", "mail") });
        Assert.Equal(0L, ExcludedCount(temp));

        store.RecomputeExcluded(new[] { new IgnoreRule("chrome", null) });
        Assert.Equal(1L, ExcludedCount(temp));
    }
}
