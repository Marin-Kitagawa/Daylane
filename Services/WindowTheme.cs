using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Daylane.Services;

/// <summary>
/// Darkens the native titlebar. MainWindow uses system chrome, so without this a dark
/// theme leaves a light caption bar above the app. Setting the DWM attribute does not by
/// itself repaint the caption of a window that is already on screen, so this also forces a
/// non-client redraw -- otherwise a live Appearance switch re-themes the body and leaves the
/// old titlebar behind until the next restart.
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

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

            // Setting the attribute alone does not repaint the caption of an already-visible
            // window, so a live switch left a stale titlebar over a re-themed body until
            // restart. SWP_FRAMECHANGED forces the non-client area to redraw; the NOMOVE /
            // NOSIZE / NOZORDER / NOACTIVATE flags make that the only thing it does, so the
            // window is not moved, resized, restacked or given focus. Return value likewise
            // unchecked -- a caption that only corrects itself on restart is not worth an
            // exception.
            SetWindowPos(
                handle,
                IntPtr.Zero,
                0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
