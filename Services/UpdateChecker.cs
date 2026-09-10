using Daylane.Models;

namespace Daylane.Services;

internal enum UpdateCheckOutcome
{
    /// <summary>No request was made. The setting is off and the user did not ask.</summary>
    Skipped,
    UpToDate,
    UpdateAvailable,
    /// <summary>We tried and could not reach GitHub. Not an error the user must act on.</summary>
    Failed
}

internal sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release = null);

/// <summary>
/// Decides whether to look for a newer release, and what to make of the answer.
///
/// The network call arrives as a delegate rather than an HttpClient held here, which is what
/// lets every branch of this class be tested without a socket -- including the one that matters
/// most, that a default install never calls the delegate at all.
/// </summary>
internal sealed class UpdateChecker(
    Func<CancellationToken, Task<string?>> fetchLatestReleaseJson,
    string currentVersion)
{
    /// <param name="userRequested">True when the user pressed Check now. An explicit act, so it
    /// runs regardless of the setting and ignores the already-seen suppression.</param>
    internal async Task<UpdateCheckResult> CheckAsync(
        DaylaneSettings settings,
        bool userRequested,
        CancellationToken cancellationToken)
    {
        if (!userRequested && !settings.CheckForUpdates)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Skipped);
        }

        string? json;
        try
        {
            json = await fetchLatestReleaseJson(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Offline, DNS, TLS, timeout, rate-limit-as-status: all the same to a user who
            // cannot act on any of them, and none of them may escape a background check.
            return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        }

        if (json is null)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        }

        ReleaseInfo? release = ReleaseFeed.Parse(json);
        if (release is null || !VersionCompare.IsNewer(release.TagName, currentVersion))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        }

        // A release the user has already been shown is not news. Compared as a version, not by
        // string equality, so dismissing v1.5.0 does not hide v2.0.0.
        if (!userRequested
            && settings.LastSeenVersion is { } seen
            && !VersionCompare.IsNewer(release.TagName, seen))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        }

        return new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, release);
    }
}
