namespace Daylane.Models;

/// <summary>The two things Daylane uses from a GitHub release: what it is called, and where to
/// send the user. Deliberately not the release body -- Daylane does not render release notes,
/// and doing so would mean rendering untrusted Markdown from the network.</summary>
internal sealed record ReleaseInfo(string TagName, string HtmlUrl);
