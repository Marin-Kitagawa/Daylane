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
    /// runs regardless of the setting.</param>
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

        // Deliberately no already-seen suppression. Suppression exists to stop a banner from
        // nagging, and Daylane has no banner: the only place a release is ever mentioned is a
        // Settings panel the user has to open on purpose. Suppressing there would answer a user
        // who opened Settings specifically to fetch the download link with "you are up to date"
        // and no link -- withholding, not restraint.
        return new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, release);
    }
}
