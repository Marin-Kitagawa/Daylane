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

    internal static TitleUsagePanel Build(IReadOnlyList<TitleUsageSummary> rows, bool isBrowser)
    {
        TimeSpan total = TimeSpan.Zero;
        foreach (var row in rows)
        {
            // Excluded rows are still shown below (so the rule that excluded them stays
            // discoverable), but their duration must never inflate what the panel reports as
            // the app's total -- that is the entire point of excluding them.
            if (!row.IsExcluded)
            {
                total += row.Duration;
            }
        }

        if (rows.Count == 0)
        {
            // An explicit empty Items list (via HasItems below), not a placeholder row -- the
            // view model uses this to show the "titles may be off" empty state instead of an
            // ItemsControl that renders zero rows as a blank panel indistinguishable from a
            // loading glitch.
            return new TitleUsagePanel([], TimeSpan.Zero);
        }

        var items = new List<TitleUsageItemViewModel>();
        if (isBrowser)
        {
            var groups = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.UrlHost) ? OtherHostLabel : r.UrlHost!)
                .OrderByDescending(g => g.Sum(r => r.Duration.Ticks));

            foreach (var group in groups)
            {
                items.Add(TitleUsageItemViewModel.Header(group.Key));
                foreach (var row in group.OrderByDescending(r => r.Duration))
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

    /// <summary>True when there is at least one row to show. Equivalent to
    /// <c>Items.Count > 0</c>, named so the view model's IsVisible bindings read as "is there
    /// something to show" rather than repeating that arithmetic in more than one place.</summary>
    internal bool HasItems => Items.Count > 0;
}

/// <summary>One row of the app-detail panel: either a browser host header (<see cref="IsHeader"/>,
/// no duration of its own) or a title row, nested under a header for a browser app or flat for
/// anything else.</summary>
internal sealed class TitleUsageItemViewModel
{
    public required string DisplayText { get; init; }
    public string DurationText { get; init; } = "";
    public string SessionCountText { get; init; } = "";
    public bool IsExcluded { get; init; }
    public bool IsHeader { get; init; }
    public bool IsNested { get; init; }

    internal static TitleUsageItemViewModel Header(string host) => new()
    {
        DisplayText = host,
        IsHeader = true
    };

    internal static TitleUsageItemViewModel Row(TitleUsageSummary row, bool nested) => new()
    {
        DisplayText = string.IsNullOrWhiteSpace(row.Title) ? "(untitled)" : row.Title!,
        DurationText = FormatDuration(row.Duration),
        SessionCountText = row.SessionCount.ToString("N0"),
        IsExcluded = row.IsExcluded,
        IsNested = nested
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds:D2}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }
}
