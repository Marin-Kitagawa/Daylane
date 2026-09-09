namespace Daylane.Models;

internal readonly record struct ForegroundApp(
    string ProcessName,
    string ExePath,
    string DisplayName,
    bool IsIdle)
{
    public static ForegroundApp Idle { get; } = new("Idle", "", "Away", true);

    public static ForegroundApp Unknown { get; } = new("Unknown", "", "Unknown", false);

    /// <summary>Foreground window title, when title capture is on. Null otherwise.
    /// Deliberately absent from SameIdentity: a retitle must not start a new segment.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>Browser site host, never a full URL. Filled at segment-open only.</summary>
    public string? UrlHost { get; init; }

    public bool SameIdentity(ForegroundApp other) =>
        IsIdle == other.IsIdle
        && string.Equals(ExePath, other.ExePath, StringComparison.OrdinalIgnoreCase)
        && (ExePath.Length > 0
            || string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase));
}
