using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class TitlePersistenceTests
{
    private static ForegroundApp App(string process, string? title) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title };

    private static (string? Title, long Excluded) ReadRow(TempDatabase temp)
    {
        using var connection = temp.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT WindowTitle, Excluded FROM ActivitySegment ORDER BY Id DESC LIMIT 1;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1));
    }

    [Fact]
    public void OpenSegment_PersistsTheTitle()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        store.OpenSegment(App("chrome", "Inbox - Mail"), DateTime.UtcNow);

        Assert.Equal("Inbox - Mail", ReadRow(temp).Title);
    }

    [Fact]
    public void OpenSegment_WithNoTitle_PersistsNull()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        store.OpenSegment(App("chrome", null), DateTime.UtcNow);

        Assert.Null(ReadRow(temp).Title);
    }

    [Fact]
    public void OpenSegment_MarksExcludedWhenAsked()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        store.OpenSegment(App("Solitaire", "Klondike"), DateTime.UtcNow, excluded: true);

        Assert.Equal(1L, ReadRow(temp).Excluded);
    }

    [Fact]
    public void OpenSegment_DefaultsToNotExcluded()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        store.OpenSegment(App("chrome", "x"), DateTime.UtcNow);

        Assert.Equal(0L, ReadRow(temp).Excluded);
    }

    [Fact]
    public void ExclusionAndStorage_AgreeOnTheSameTitle()
    {
        // The live decision and the rule-change recompute must reach the same verdict, and
        // the recompute can only ever see the stored title. So a stripped title means a
        // title-keyword rule does not match — live or on recompute. This drives the real
        // CapturePolicy instead of re-implementing its branch, so deleting the policy fails
        // the test rather than silently passing it.
        var captured = App("chrome", "Inbox - mail");
        var keyword = new DaylaneSettings
        {
            IgnoreRules = [new IgnoreRule("chrome", "mail")]
        }.Normalize();

        // Titles off (the default): the stored title is null, so the keyword cannot match.
        ForegroundApp storedOff = CapturePolicy.Apply(captured, keyword, out bool excludedOff);
        Assert.Null(storedOff.WindowTitle);
        Assert.False(excludedOff);

        // Titles on: the stored title is the real one, so it matches.
        ForegroundApp storedOn = CapturePolicy.Apply(
            captured, keyword with { RecordWindowTitles = true }, out bool excludedOn);
        Assert.Equal("Inbox - mail", storedOn.WindowTitle);
        Assert.True(excludedOn);

        // A whole-process rule works either way.
        var wholeProcess = new DaylaneSettings
        {
            IgnoreRules = [new IgnoreRule("chrome", null)]
        }.Normalize();
        CapturePolicy.Apply(captured, wholeProcess, out bool excludedWhole);
        Assert.True(excludedWhole);
    }

    [Fact]
    public void Apply_StripsHost_WhenRecordBrowserHostIsOffButTitlesAreOn()
    {
        // Deliberately not Normalize()'d: normalizing would clear RecordBrowserHost on its own
        // whenever titles are off, so a settings object built that way would pass this test
        // even if Apply's own strip did nothing. This pins Apply's strip specifically, which is
        // what closes the mid-walk flip (RecordBrowserHost turned off while titles stay on).
        // PrivacyKeywords/IgnoreRules are supplied directly (rather than left at their null
        // default) since Apply reads both un-normalized and would otherwise null-reference.
        var app = new ForegroundApp("chrome", @"C:\Apps\chrome.exe", "chrome", false)
            { WindowTitle = "Inbox", UrlHost = "mail.google.com" };
        var settings = new DaylaneSettings
        {
            RecordWindowTitles = true,
            RecordBrowserHost = false,
            PrivacyKeywords = Array.Empty<string>(),
            IgnoreRules = Array.Empty<IgnoreRule>()
        };

        ForegroundApp stored = CapturePolicy.Apply(app, settings, out _);

        Assert.Equal("Inbox", stored.WindowTitle);
        Assert.Null(stored.UrlHost);
    }
}
