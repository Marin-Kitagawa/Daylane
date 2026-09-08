using System.Runtime.InteropServices;

namespace Daylane.Services;

/// <summary>
/// Idle detection via GetLastInputInfo (keyboard, mouse buttons, mouse move, wheel).
/// Same approach as the standalone Inactivity Timer: threshold-based Away sessions.
/// </summary>
internal static class IdleMonitor
{
    public const int DefaultThresholdMinutes = 15;
    public const int MinThresholdMinutes = 1;
    public const int MaxThresholdMinutes = 240;

    private static SettingsService? _settings;

    public static TimeSpan Threshold => TimeSpan.FromMinutes(
        _settings?.Current.IdleThresholdMinutes ?? DefaultThresholdMinutes);

    /// <summary>Reads the threshold live, so changing it in Settings needs no restart.</summary>
    public static void Bind(SettingsService settings) => _settings = settings;

    public static double GetIdleSeconds()
    {
        var info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        ulong idleMs = unchecked(GetTickCount64() - info.dwTime);
        return idleMs / 1000.0;
    }

    public static bool IsAway() => GetIdleSeconds() >= Threshold.TotalSeconds;

    public static DateTime LastInputUtc()
    {
        double idleSeconds = GetIdleSeconds();
        return DateTime.UtcNow - TimeSpan.FromSeconds(idleSeconds);
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
