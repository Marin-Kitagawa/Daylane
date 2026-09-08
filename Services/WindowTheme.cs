using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Daylane.Services;

/// <summary>
/// Darkens the native titlebar. MainWindow uses system chrome, so without this a dark
/// theme leaves a light caption bar above the app.
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    internal static void Apply(nint handle, bool dark)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int value = dark ? 1 : 0;

            // Return value is not checked: this is purely cosmetic, and a failure here
            // should never take down a window.
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Cosmetic and version-dependent; never worth failing a window for.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    internal static void Apply(Window window, bool dark)
    {
        Apply(window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, dark);
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
