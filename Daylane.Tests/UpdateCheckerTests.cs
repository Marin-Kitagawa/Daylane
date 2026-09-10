using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class UpdateCheckerTests
{
    private const string NewerJson = """
        {"tag_name":"v2.0.0","html_url":"https://example.com/r/2","draft":false,"prerelease":false}
        """;

    private static string Json(string tag) => $$"""
        {"tag_name":"{{tag}}","html_url":"https://example.com/r","draft":false,"prerelease":false}
        """;

    [Fact]
    public async Task WithDefaultSettings_NoRequestIsEverMade()
    {
        // THE promise of this sub-project. If this test fails, a default install has started
        // talking to GitHub and the README is false.
        bool fetched = false;
        var checker = new UpdateChecker(_ => { fetched = true; return Task.FromResult<string?>(NewerJson); }, "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(new DaylaneSettings(), userRequested: false, CancellationToken.None);

        Assert.False(fetched);
        Assert.Equal(UpdateCheckOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task AUserRequestedCheckRunsEvenWhenTheSettingIsOff()
    {
        // Pressing a button is an explicit act. The toggle governs automatic checks.
        bool fetched = false;
        var checker = new UpdateChecker(_ => { fetched = true; return Task.FromResult<string?>(NewerJson); }, "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(new DaylaneSettings(), userRequested: true, CancellationToken.None);

        Assert.True(fetched);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task WhenEnabledAndANewerReleaseExists_ItIsReported()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(NewerJson), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: false, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v2.0.0", result.Release!.TagName);
        Assert.Equal("https://example.com/r/2", result.Release.HtmlUrl);
    }

    [Fact]
    public async Task WhenTheReleaseIsNotNewer_ItReportsUpToDate()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(Json("v1.0.1")), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: false, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task AlreadySeenRelease_ReportsUpToDateRatherThanNagging()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true, LastSeenVersion = "v2.0.0" };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(NewerJson), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: false, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task AReleaseNewerThanTheOneAlreadySeen_IsStillReported()
    {
        // Suppression must not become "never tell me again".
        var settings = new DaylaneSettings() with { CheckForUpdates = true, LastSeenVersion = "v1.5.0" };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(NewerJson), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: false, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task AUserRequestedCheckIgnoresSuppression()
    {
        // If you press the button, you get the answer, even about a release you dismissed.
        var settings = new DaylaneSettings() with { CheckForUpdates = true, LastSeenVersion = "v2.0.0" };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(NewerJson), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: true, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task AFetchThatThrows_ReportsFailedRatherThanPropagating()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(
            _ => Task.FromException<string?>(new HttpRequestException("offline")), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: true, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AFetchThatReturnsNull_ReportsFailed()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(null), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: true, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AnUnparseableBody_ReportsUpToDateNotFailed()
    {
        // We reached GitHub and it told us something we cannot use. That is not a failure to
        // report to the user -- there is no newer release we can offer, which is "up to date".
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>("{\"message\":\"rate limited\"}"), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: true, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }
}
