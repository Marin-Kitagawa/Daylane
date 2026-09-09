using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Daylane.Models;

namespace Daylane.Services;

internal sealed class ForegroundTracker : IDisposable
{
    // App focus: 1s. Away state uses IdleMonitor (GetLastInputInfo), same as Inactivity Timer.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Timer _timer;
    private readonly object _lock = new();
    private ForegroundApp? _current;
    private uint _cachedPid;
    private ForegroundApp? _cachedByPid;
    private bool _disposed;

    public ForegroundTracker()
    {
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<ForegroundApp>? Changed;

    public ForegroundApp Current
    {
        get
        {
            lock (_lock)
            {
                return _current ?? Sample();
            }
        }
    }

    public void Start()
    {
        Poll();
        _timer.Change(PollInterval, PollInterval);
    }

    /// <summary>Pauses polling; Start() resumes it. Used to honor DaylaneSettings.TrackingEnabled
    /// without tearing the tracker down.</summary>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Change(Timeout.Infinite, Timeout.Infinite);

        // Drop the cached identity as well. Pausing closes the open segment, so if the cache
        // survived, the immediate Poll() inside the next Start() would see the same app it saw
        // before the pause, report no change, and open nothing -- recording nothing at all until
        // the user happened to switch apps. That is the common case: pause, keep working in the
        // same app, resume. null is exactly the state the tracker is constructed in, so the
        // first Start() at launch behaves as it always did.
        lock (_lock)
        {
            _current = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        ForegroundApp sample = Sample();
        bool changed;

        lock (_lock)
        {
            changed = _current is null || !_current.Value.SameIdentity(sample);
            if (changed)
            {
                _current = sample;
            }
        }

        if (changed)
        {
            Changed?.Invoke(sample);
        }
    }

    private ForegroundApp Sample()
    {
        // Matches Inactivity Timer: idle >= threshold => Away; otherwise track foreground app.
        if (IdleMonitor.IsAway())
        {
            return ForegroundApp.Idle;
        }

        return ResolveForegroundApp();
    }

    private static string? ReadWindowTitle(IntPtr hwnd)
    {
        try
        {
            var buffer = new char[512];
            int length = GetWindowTextW(hwnd, buffer, buffer.Length);
            if (length <= 0)
            {
                return null;
            }

            string title = new string(buffer, 0, length).Trim();
            return title.Length == 0 ? null : title;
        }
        catch (Exception)
        {
            // A title is never worth failing a poll over.
            return null;
        }
    }

    private ForegroundApp ResolveForegroundApp()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return ForegroundApp.Unknown;
        }

        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return ForegroundApp.Unknown;
        }

        string? title = ReadWindowTitle(hwnd);

        if (processId == _cachedPid && _cachedByPid is { } cached)
        {
            return WithTitle(cached, title);
        }

        ForegroundApp resolved = ResolveProcess(processId);
        _cachedPid = processId;
        _cachedByPid = resolved;
        return WithTitle(resolved, title);
    }

    /// <summary>Attaches a freshly-read window title to a resolved app, except when the app
    /// could not be resolved at all. Withholding the title there matters because a stored row
    /// whose process is Unknown would otherwise still carry a real, persisted title -- the
    /// process could not be identified, but the window text was read anyway.</summary>
    internal static ForegroundApp WithTitle(ForegroundApp app, string? title) =>
        app == ForegroundApp.Unknown ? app : app with { WindowTitle = title };

    private static ForegroundApp ResolveProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            string processName = string.IsNullOrWhiteSpace(process.ProcessName)
                ? "Unknown"
                : process.ProcessName;

            string exePath = TryGetProcessPath(process) ?? "";
            string displayName = TryGetDisplayName(exePath, processName);

            return new ForegroundApp(processName, exePath, displayName, false);
        }
        catch (ArgumentException)
        {
            return ForegroundApp.Unknown;
        }
        catch (InvalidOperationException)
        {
            return ForegroundApp.Unknown;
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            // Elevated / protected processes often deny MainModule access.
        }

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return buffer.ToString();
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        return null;
    }

    private static string TryGetDisplayName(string exePath, string processName)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return processName;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription;
            }

            if (!string.IsNullOrWhiteSpace(info.ProductName))
            {
                return info.ProductName;
            }
        }
        catch (Exception)
        {
            // Fall through to file name.
        }

        try
        {
            return Path.GetFileNameWithoutExtension(exePath);
        }
        catch (Exception)
        {
            return processName;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        int dwFlags,
        StringBuilder lpExeName,
        ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
