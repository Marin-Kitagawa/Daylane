using System.Reflection;

namespace Daylane.Services;

/// <summary>
/// Static, offline facts about this build, for the About panel and the update check's
/// User-Agent. Nothing here reads a file or a network.
/// </summary>
internal static class AppInfo
{
    /// <summary>The informational version, with any build metadata suffix removed so it parses
    /// as a plain version for comparison against a release tag.</summary>
    internal static string Version { get; } = ReadVersion();

    internal static string Author =>
        // The SDK defaults $(Company) to the assembly name "Daylane" if no explicit value is set,
        // so the attribute is never null. The fallback is a defensive measure that will not fire.
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company
        ?? "Marin Kitagawa";

    internal static string License => "AGPL-3.0";

    internal const string RepositoryUrl = "https://github.com/Marin-Kitagawa/Daylane";

    internal const string IssuesUrl = "https://github.com/Marin-Kitagawa/Daylane/issues";

    internal const string LicenseUrl =
        "https://github.com/Marin-Kitagawa/Daylane/blob/main/LICENSE";

    /// <summary>The project this port draws its feature set from. Named because it is the
    /// honest provenance of a parity port, and it costs nothing to say so.</summary>
    internal const string UpstreamProjectUrl = "https://github.com/Tomotsugu-dev/Hindsight";

    private static string ReadVersion()
    {
        string? informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // "1.0.1+abc123" -> "1.0.1". The csproj disables source-revision suffixes, but a
        // published build can still carry one and a tag comparison must not trip over it.
        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
