namespace Daylane.Services;

/// <summary>
/// Decides whether a segment's detail is captured at all.
///
/// Orthogonal to <see cref="IgnoreRules"/>: a hit here means no title and no host are
/// written, but the row still exists and still counts toward totals. A hit there means the
/// detail is written but the time is not counted.
///
/// Suppression is not reversible — the detail was never persisted. That is the intent: a
/// user who names a keyword is asking for nothing to be recorded, not for it to be recorded
/// and hidden.
/// </summary>
internal static class PrivacyKeywords
{
    internal static bool Suppresses(string? title, string? host, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0 || (title is null && host is null))
        {
            return false;
        }

        foreach (string raw in keywords)
        {
            string keyword = raw.Trim();

            // An empty keyword would match everything, suppressing capture entirely.
            if (keyword.Length == 0)
            {
                continue;
            }

            if (title is not null && title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (host is not null && host.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
