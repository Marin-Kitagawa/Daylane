using Daylane.Services;

namespace Daylane.Tests;

/// <summary>
/// The P/Invoke path cannot be asserted headlessly, and instantiating an Avalonia
/// <see cref="Avalonia.Controls.Window"/> outside a headless test harness throws
/// ("Unable to locate 'Avalonia.Platform.IWindowingPlatform'") because no platform is
/// initialized for the test process -- the same wall the Task 10/11 theme tests hit.
/// WindowTheme is split into a handle-taking overload that holds the only real logic (the
/// no-handle guard) and a Window-taking overload that just extracts the handle, so that
/// guard can be exercised directly with IntPtr.Zero, with no Avalonia runtime required.
///
/// That guard now covers both P/Invokes -- the DWM attribute and the SetWindowPos call that
/// forces the caption to repaint on a live switch. Whether the caption actually redraws is
/// not assertable here: it needs a real HWND and a visual check, so it is verified by driving
/// the running app, not by this suite.
/// </summary>
public class WindowThemeTests
{
    [Fact]
    public void Apply_WithZeroHandle_DoesNotThrow()
    {
        WindowTheme.Apply(IntPtr.Zero, dark: true);
        WindowTheme.Apply(IntPtr.Zero, dark: false);
    }
}
