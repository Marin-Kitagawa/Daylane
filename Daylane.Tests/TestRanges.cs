namespace Daylane.Tests;

/// <summary>
/// Shared helper for tests that seed segments near "now" and then query a local-date range.
/// Seeding uses <c>DateTime.UtcNow</c> while a fresh <c>DateTime.Now.Date</c> read for the query
/// range is a second, independent clock read: the two can straddle local midnight and make the
/// test flake depending on what time of day it runs. Anchoring the query range to the same
/// instant that built the data avoids that.
/// </summary>
internal static class TestRanges
{
    internal static (DateTime StartLocal, DateTime EndExclusiveLocal) UnambiguousRangeAround(DateTime anyUtcInstant)
    {
        DateTime anchor = anyUtcInstant.ToLocalTime().Date;
        return (anchor.AddDays(-1), anchor.AddDays(2));
    }
}
