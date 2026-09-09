using Daylane.Models;

namespace Daylane.Services;

/// <summary>
/// Decides whether an activity row counts toward totals.
///
/// Orthogonal to <see cref="PrivacyKeywords"/> and easy to confuse with it:
/// privacy controls whether DETAIL IS CAPTURED (a hit stores no title and no host, but the
/// row still counts); this controls whether TIME IS COUNTED (a hit stores the detail but
/// marks the row Excluded, and every report skips it).
///
/// This is the fine-grained sibling of a future "hidden" category: hidden will exclude a
/// whole app, a rule here can exclude one kind of window within an app — a terminal left
/// running a download overnight, where the terminal itself should still count.
/// </summary>
internal static class IgnoreRules
{
    internal static bool IsExcluded(string processName, string? title, IReadOnlyList<IgnoreRule> rules)
    {
        if (rules.Count == 0)
        {
            return false;
        }

        string app = processName.Trim();
        if (app.Length == 0)
        {
            return false;
        }

        foreach (IgnoreRule rule in rules)
        {
            string want = rule.ProcessName.Trim();

            // A rule with no process name would match every window. Treat it as inert
            // rather than letting it swallow everything.
            if (want.Length == 0 || !string.Equals(want, app, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.TitleKeyword is null)
            {
                return true;
            }

            string keyword = rule.TitleKeyword.Trim();

            // An all-whitespace keyword matches nothing: Contains("") is always true, and
            // treating it as "match everything" would let one UI slip exclude a whole app.
            if (keyword.Length == 0 || title is null)
            {
                continue;
            }

            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
