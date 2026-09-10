namespace Daylane.Services;

/// <summary>
/// Compares a GitHub release tag against the running version.
///
/// Versions, never strings: a string comparison makes "1.0.9" newer than "1.0.10", which is
/// exactly the point in a project's life when the bug would first appear and would look like
/// the update check silently breaking.
/// </summary>
internal static class VersionCompare
{
    /// <summary>Parses a release tag, tolerating a leading "v". Returns null for anything that
    /// is not a plain numeric version -- including prerelease suffixes like "1.2.3-beta.1",
    /// which System.Version does not accept and which we do not want to offer anyway.</summary>
    internal static Version? TryParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        return Version.TryParse(trimmed, out Version? parsed) ? parsed : null;
    }

    /// <summary>True only when both sides parse AND the candidate is strictly greater. Every
    /// other case -- unreadable tag, unreadable current version, equal, older -- is "no
    /// update", never an error.</summary>
    internal static bool IsNewer(string? candidateTag, string currentVersion)
    {
        Version? candidate = TryParseTag(candidateTag);
        Version? current = TryParseTag(currentVersion);

        return candidate is not null && current is not null && candidate > current;
    }
}
