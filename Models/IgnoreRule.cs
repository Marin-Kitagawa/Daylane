using System.Text.Json.Serialization;

namespace Daylane.Models;

/// <summary>
/// One ignore rule: a process name (exact) plus an optional window-title keyword (substring).
///
/// A rule MUST carry a process name. A title-keyword-only rule would match windows across
/// every application, and the time it swallowed would raise no error and no warning — the
/// totals would simply stop adding up. Silent data loss is this feature's characteristic
/// accident, so the shape forbids it rather than warning about it.
/// </summary>
internal sealed record IgnoreRule(
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("titleKeyword")] string? TitleKeyword = null);
