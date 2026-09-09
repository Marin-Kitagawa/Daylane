namespace Daylane.Models;

internal sealed class ActivitySegment
{
    public long Id { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime? EndUtc { get; init; }
    public string ProcessName { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsIdle { get; init; }
    public long KeyCount { get; init; }
    public long MouseClickCount { get; init; }
    public string? WindowTitle { get; init; }
    public string? UrlHost { get; init; }
    public bool Excluded { get; init; }

    public DateTime EffectiveEndUtc => EndUtc ?? DateTime.UtcNow;

    public TimeSpan Duration => EffectiveEndUtc - StartUtc;
}
