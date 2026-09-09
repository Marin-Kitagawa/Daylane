using Daylane.Models;

namespace Daylane.Services;

/// <summary>
/// The one place a stored segment becomes a slice of time a total may count.
///
/// Every aggregate needs the same four decisions: clip the segment to the range, treat a
/// still-open segment's end as now, never count past now, and drop what an ignore rule
/// excluded. The first three were duplicated verbatim between
/// <see cref="DailyStatsStore.AggregateAppUsage(IReadOnlyList{ActivitySegment}, DateTime, DateTime)"/>
/// and <see cref="DailyStatsStore.GetTitleUsage"/>, and that duplication is precisely how one
/// copy came to subtract excluded time and the other did not -- "Excluded" was written,
/// recomputed, indexed and badged while no aggregate filtered on it. All four live here now,
/// with exclusion applied by default, so the next aggregate inherits the rule by construction
/// rather than by its author remembering it.
/// </summary>
internal static class SegmentWindows
{
    /// <summary>
    /// The countable slice of each segment that falls inside the range, in input order.
    /// Segments an ignore rule excluded are absent, and segments that clip to nothing are
    /// absent.
    ///
    /// <paramref name="includeExcluded"/> exists for exactly one caller: the app-detail
    /// drawer's per-title breakdown, which shows excluded rows badged so the rule that
    /// excluded them stays discoverable, and which keeps that time out of the total it
    /// displays itself (see <c>TitleUsagePresenter</c>). Nothing that reports a total to the
    /// user may pass true here.
    /// </summary>
    internal static IEnumerable<SegmentWindow> Counted(
        IEnumerable<ActivitySegment> segments,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        DateTime nowUtc,
        bool includeExcluded = false)
    {
        foreach (ActivitySegment segment in segments)
        {
            if (segment.Excluded && !includeExcluded)
            {
                continue;
            }

            DateTime start = segment.StartUtc < rangeStartUtc ? rangeStartUtc : segment.StartUtc;
            DateTime end = segment.EffectiveEndUtc > rangeEndUtc ? rangeEndUtc : segment.EffectiveEndUtc;

            // An open segment's EffectiveEndUtc is "now" as of the moment it was read; clamping
            // to a single nowUtc for the whole pass keeps every row of one aggregate consistent.
            if (end > nowUtc)
            {
                end = nowUtc;
            }

            if (end <= start)
            {
                continue;
            }

            yield return new SegmentWindow(segment, start, end);
        }
    }
}

/// <summary>One segment together with the portion of it that counts: its own row data stays
/// reachable for grouping and labelling, while <see cref="StartUtc"/>/<see cref="EndUtc"/> are
/// already clipped and clamped.</summary>
internal readonly record struct SegmentWindow(ActivitySegment Segment, DateTime StartUtc, DateTime EndUtc)
{
    internal TimeSpan Duration => EndUtc - StartUtc;
}
