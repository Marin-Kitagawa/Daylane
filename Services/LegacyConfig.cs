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
                    return parsed;
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }
}
