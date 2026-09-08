using System.Globalization;

namespace Daylane.Services;

/// <summary>
/// Reads the pre-settings-store config.ini. Imported once into SettingsStore, after which
/// the file is dormant.
/// </summary>
internal static class LegacyConfig
{
    internal static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.ini");

    internal static int? ReadThresholdMinutes(string configPath)
    {
        int? minutes = null;
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            foreach (string raw in File.ReadAllLines(configPath))
            {
                string line = raw.Trim();
                if (!line.StartsWith("threshold_minutes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals >= 0
                    && int.TryParse(
                        line[(equals + 1)..].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsed)
                    && parsed is >= IdleMonitor.MinThresholdMinutes and <= IdleMonitor.MaxThresholdMinutes)
                {
                    // Last valid line wins, matching the original IdleMonitor.Load() behavior.
                    minutes = parsed;
                }
            }
        }
        catch (IOException)
        {
        }

        return minutes;
    }

    /// <summary>Imports the threshold from <paramref name="configPath"/> exactly once, gated on
    /// <see cref="DaylaneSettings.LegacyConfigImported"/> rather than "is the threshold still the
    /// default" — a user who deliberately chose the default value must not be overwritten by a
    /// stale config.ini. Sets the flag whether or not a value was found, so a missing or
    /// malformed file never leaves the import armed forever.</summary>
    internal static void ImportOnce(SettingsService settings, string configPath)
    {
        if (settings.Current.LegacyConfigImported)
        {
            return;
        }

        int? minutes = ReadThresholdMinutes(configPath);
        settings.Update(s => s with
        {
            IdleThresholdMinutes = minutes ?? s.IdleThresholdMinutes,
            LegacyConfigImported = true
        });
    }
}
