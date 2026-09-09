namespace Daylane.Services;

/// <summary>
/// The single spelling of a stored timestamp. Every row Daylane writes carries text in this
/// exact round-trip ("O") shape and readers parse it back with RoundtripKind, so the format is
/// part of the on-disk contract: changing it silently invalidates every existing database.
/// </summary>
internal static class Timestamps
{
    internal static string ToUtcText(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O");

    internal static string UtcNowText() => ToUtcText(DateTime.UtcNow);
}
