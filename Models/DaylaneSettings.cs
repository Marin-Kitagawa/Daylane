using System.Text.Json.Serialization;
using Daylane.Services;

namespace Daylane.Models;

/// <summary>
/// A positional record with defaulted parameters rather than init-only auto-properties: the
/// source-generated JSON context (<see cref="SettingsJsonContext"/>) only falls back to a
/// property's declared default when a stored value is missing if deserialization goes through a
/// constructor call. With init-only properties instead, a source-generated (not reflection-based)
/// deserializer leaves any property absent from the JSON at its CLR default (0 / false / null)
/// rather than the C# property initializer, so reading the seeded <c>{}</c> row would silently
/// zero out every setting.
/// </summary>
internal sealed record DaylaneSettings(
    [property: JsonPropertyName("appearance")]
    string Appearance = DaylaneSettings.AppearanceSystem,

    [property: JsonPropertyName("idleThresholdMinutes")]
    int IdleThresholdMinutes = IdleMonitor.DefaultThresholdMinutes,

    [property: JsonPropertyName("trackingEnabled")]
    bool TrackingEnabled = true,

    [property: JsonPropertyName("autoStart")]
    bool AutoStart = false,

    [property: JsonPropertyName("showWindowOnAutoStart")]
    bool ShowWindowOnAutoStart = false,

    [property: JsonPropertyName("minimizeToTray")]
    bool MinimizeToTray = true,

    /// <summary>0 keeps history forever, which is the behavior before this setting existed.</summary>
    [property: JsonPropertyName("retentionDays")]
    int RetentionDays = 0,

    /// <summary>Set once the pre-settings-store config.ini has been imported. A dedicated flag
    /// rather than "is the threshold still the default", so a user who deliberately chose the
    /// default value is not silently overwritten by a stale config.ini.</summary>
    [property: JsonPropertyName("legacyConfigImported")]
    bool LegacyConfigImported = false)
{
    public const string AppearanceSystem = "system";
    public const string AppearanceLight = "light";
    public const string AppearanceDark = "dark";

    /// <summary>Clamps anything a hand-edited store or an older build could have written.</summary>
    public DaylaneSettings Normalize() => this with
    {
        Appearance = Appearance is AppearanceLight or AppearanceDark or AppearanceSystem
            ? Appearance
            : AppearanceSystem,
        IdleThresholdMinutes = Math.Clamp(
            IdleThresholdMinutes, IdleMonitor.MinThresholdMinutes, IdleMonitor.MaxThresholdMinutes),
        RetentionDays = Math.Max(0, RetentionDays)
    };
}

[JsonSerializable(typeof(DaylaneSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
