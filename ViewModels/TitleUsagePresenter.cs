using System.Linq;
using Daylane.Models;

namespace Daylane.ViewModels;

/// <summary>
/// Shapes <see cref="TitleUsageSummary"/> rows for the Day view's app-detail panel: the
/// running total (which must exclude excluded rows) and the browser-vs-other layout (grouped
/// by host with titles nested, or a flat title list). Pure and static, mirroring
/// Services/CapturePolicy.cs (Task 6) and ViewModels/SettingsChangeNotifier.cs: none of
/// MainWindowViewModel can be exercised in a unit test because its TrackingService opens a
/// real database next to the executable, so the two genuinely risky rules here -- "excluded
/// time must never inflate the total" and "browser apps group by host, everything else stays
/// flat" -- are pulled out where they CAN be tested directly, and the view model just calls
/// this.
/// </summary>
internal static class TitleUsagePresenter
{
    /// <summary>Label for a browser row that carries no host at all (e.g. a chrome:// page or
    /// a local file) -- grouped together rather than dropped or left to litter the top level
    /// ungrouped, since GetTitleUsage deliberately never drops a row.</summary>
    internal const string OtherHostLabel = "Other";

    /// <summary>True when the selected app can have no per-window breakdown at all, whatever
    /// the privacy settings say: Daylane never resolved its program file (an elevated or
    /// protected process), or the row is the synthetic "Away" one, which is not an app and has
    /// no windows. The drawer must not answer this case with "turn on title capture" -- that
    /// sends the user to a switch which changes nothing here -- so the same predicate that
    /// makes the query short-circuit also picks the message.</summary>
    internal static bool DetailUnavailable(bool isIdle, string? exePath) =>
        isIdle || string.IsNullOrWhiteSpace(exePath);

    internal static TitleUsagePanel Build(IReadOnlyList<TitleUsageSummary> rows, bool isBrowser)
    {
        // With title capture off -- the default -- every segment stored a null title and a null
        // host, so GetTitleUsage returns exactly one (null, null) group carrying the app's whole
        // duration. Rendering that gives a fresh user a group called "Other" holding a row
        // called "(untitled)", and leaves the "No titles captured" empty state unreachable
        // because the panel always had one item. No row carrying any detail means there is no
        // breakdown to show: report none and let the caller show the empty state instead.
        // A single detail-less row ALONGSIDE titled ones is different -- that is real
        // information (time in windows whose title was suppressed) and stays.
        bool anyDetail = rows.Any(r =>
            !string.IsNullOrWhiteSpace(r.Title) || !string.IsNullOrWhiteSpace(r.UrlHost));
        if (!anyDetail)
        {
            return new TitleUsagePanel(Array.Empty<TitleUsageItemViewModel>(), TimeSpan.Zero);
        }

        TimeSpan total = TotalOf(rows);

        var items = new List<TitleUsageItemViewModel>();
        if (isBrowser)
        {
            // Ordered by, and labelled with, the same number: time per site is the whole point
            // of grouping a browser by host, so the header carries it rather than leaving the
            // ordering key invisible. Excluded rows are left out of it for the same reason they
            // are left out of the panel total, which keeps the headers summing to that total.
            var groups = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.UrlHost) ? OtherHostLabel : r.UrlHost!)
                .Select(g => (Host: g.Key, Rows: g, Counted: TotalOf(g)))
                .OrderByDescending(g => g.Counted);

            foreach (var group in groups)
            {
                items.Add(TitleUsageItemViewModel.Header(group.Host, group.Counted));
                foreach (var row in group.Rows.OrderByDescending(r => r.Duration))
                {
                    items.Add(TitleUsageItemViewModel.Row(row, nested: true));
                }
            }
        }
        else
        {
            foreach (var row in rows.OrderByDescending(r => r.Duration))
            {
                items.Add(TitleUsageItemViewModel.Row(row, nested: false));
            }
        }

        return new TitleUsagePanel(items, total);
    }

    /// <summary>The time a set of rows contributes to a figure the user reads as time spent --
    /// the panel total, and each browser host header. Excluded rows are still shown, so the
    /// rule that excluded them stays discoverable, but their duration must never inflate that
    /// figure: that is the entire point of excluding them.</summary>
    private static TimeSpan TotalOf(IEnumerable<TitleUsageSummary> rows)
    {
        TimeSpan total = TimeSpan.Zero;
        foreach (var row in rows)
        {
            if (!row.IsExcluded)
            {
                total += row.Duration;
            }
        }

        return total;
    }
}

/// <summary>The shaped result <see cref="TitleUsagePresenter.Build"/> returns: the item list the
/// panel binds to, plus the total it displays alongside it.</summary>
internal sealed class TitleUsagePanel
{
    internal TitleUsagePanel(IReadOnlyList<TitleUsageItemViewModel> items, TimeSpan totalDuration)
    {
        Items = items;
        TotalDuration = totalDuration;
    }

    internal IReadOnlyList<TitleUsageItemViewModel> Items { get; }

    internal TimeSpan TotalDuration { get; }
}

/// <summary>One row of the app-detail panel: either a browser host header (<see cref="IsHeader"/>,
/// carrying that host's counted time and no session count) or a title row, nested under a header
/// for a browser app or flat for anything else.</summary>
internal sealed class TitleUsageItemViewModel
{
    public required string DisplayText { get; init; }
    public string DurationText { get; init; } = "";
    public string SessionCountText { get; init; } = "";
    public bool IsExcluded { get; init; }
    public bool IsHeader { get; init; }
    public bool IsNested { get; init; }

    internal static TitleUsageItemViewModel Header(string host, TimeSpan duration) => new()
    {
        DisplayText = host,
        DurationText = MainWindowViewModel.FormatDuration(duration),
        IsHeader = true
    };

    // MainWindowViewModel.FormatDuration, not a second copy: two independent copies of the
    // same 13 lines agree today and could silently drift apart, which would make the panel's
    // total (formatted by the view model) disagree with its own rows (formatted here).
    internal static TitleUsageItemViewModel Row(TitleUsageSummary row, bool nested) => new()
    {
        DisplayText = string.IsNullOrWhiteSpace(row.Title) ? "(untitled)" : row.Title!,
        DurationText = MainWindowViewModel.FormatDuration(row.Duration),
        SessionCountText = row.SessionCount.ToString("N0"),
        IsExcluded = row.IsExcluded,
        IsNested = nested
    };
}
