using System.Net.Http;
using System.Net.Http.Headers;

namespace Daylane.Services;

/// <summary>
/// The only code in Daylane that contacts a network, and it runs solely when the user has
/// turned the update check on or pressed Check now.
///
/// What leaves the machine, in full: one unauthenticated HTTPS GET to the URL below, carrying a
/// User-Agent of "Daylane/<version>" (GitHub rejects requests without one) and an Accept
/// header. No account, no token, no identifier, no telemetry. GitHub observes the requesting IP
/// and that version string, as it would for any download. README.md documents exactly this.
/// </summary>
internal static class ReleaseFetch
{
    // This fork, not the mirbyte/Daylane upstream it was branched from: the shipped build
    // contains work upstream does not have, so pointing there would offer a downgrade.
    internal const string LatestReleaseUrl =
        "https://api.github.com/repos/Marin-Kitagawa/Daylane/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // A check must never be the reason startup feels slow. Ten seconds is generous for
            // one small GET and short enough that nobody waits on it.
            Timeout = TimeSpan.FromSeconds(10)
        };

        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Daylane", AppInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    /// <summary>Returns the response body, or null if the request failed or GitHub returned a
    /// non-success status. Never throws: the caller treats null as "could not reach GitHub".</summary>
    internal static async Task<string?> LatestReleaseJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await Client.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
