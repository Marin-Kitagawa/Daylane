using System.Text.Json;
using System.Text.Json.Serialization;
using Daylane.Models;

namespace Daylane.Services;

/// <summary>
/// Turns a GitHub "latest release" response into a <see cref="ReleaseInfo"/>, or into null.
///
/// Every unusable input -- malformed JSON, a rate-limit body, a draft, a prerelease, a release
/// with no download page -- returns null rather than throwing. This runs on a background thread
/// during startup, so an escaping exception would be a crash, and there is nothing a user could
/// do about any of these cases anyway.
/// </summary>
internal static class ReleaseFeed
{
    internal static ReleaseInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize(json, ReleaseJsonContext.Default.GitHubRelease);
        }
        catch (JsonException)
        {
            return null;
        }

        if (release is null
            || release.Draft
            || release.Prerelease
            || string.IsNullOrWhiteSpace(release.TagName)
            || string.IsNullOrWhiteSpace(release.HtmlUrl))
        {
            return null;
        }

        return new ReleaseInfo(release.TagName.Trim(), release.HtmlUrl.Trim());
    }
}

/// <summary>Positional record with defaulted parameters, not init-only properties: see the doc
/// comment on DaylaneSettings for why source-generated deserialization needs a constructor.</summary>
internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName = null,
    [property: JsonPropertyName("html_url")] string? HtmlUrl = null,
    [property: JsonPropertyName("draft")] bool Draft = false,
    [property: JsonPropertyName("prerelease")] bool Prerelease = false);

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
