namespace Daylane.Models;

internal sealed class TitleUsageSummary
{
    public string? Title { get; init; }
    public string? UrlHost { get; init; }
    public TimeSpan Duration { get; init; }
    public int SessionCount { get; init; }
    public bool IsExcluded { get; init; }
}
