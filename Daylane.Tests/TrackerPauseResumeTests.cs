using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

/// <summary>
/// Regression coverage for a tracking gap introduced alongside the TrackingEnabled switch.
///
/// Stop() only parked the poll timer and left the cached foreground identity in place, so the
/// immediate Poll() inside the next Start() compared the current app against the one held from
/// before the pause, found no change, and raised nothing. Pausing had already closed the open
/// segment, so a pause -> resume with no app switch in between (the common case: pause, keep
/// working in the same app, resume) recorded nothing at all until the user happened to switch
/// apps -- while IsRunning read true and the header showed "Live".
///
/// This drives the real ForegroundTracker. Its Win32 sampling is deliberately left alone and
/// nothing is mocked; the test only needs the sample to be *stable*, not to be any particular
/// app, so it works whether the foreground resolves to a real process or to Unknown (as it does
/// when the test host has no interactive foreground window).
///
/// The middle step is a deliberate control: a second Start() with no Stop() in between must
/// stay silent. That proves the sampled identity really is stable across back-to-back polls in
/// this environment, so the final assertion cannot pass for the wrong reason -- if the machine's
/// foreground window changed mid-test, the control fails loudly instead of granting a false pass.
///
/// OpenAppTracker.Stop() received the same fix, but is not asserted here: its sample is the full
/// set of visible top-level windows, which any background app can change at any moment, so the
/// equivalent control step would be flaky. It is covered by inspection and by driving the app.
/// </summary>
public class TrackerPauseResumeTests
{
    [Fact]
    public void Start_AfterStop_RaisesChanged_EvenWhenTheForegroundAppIsUnchanged()
    {
        using var tracker = new ForegroundTracker();

        int changes = 0;
        tracker.Changed += _ => Interlocked.Increment(ref changes);

        // Start() polls synchronously before arming the timer, so each call's effect is
        // observable immediately. The poll interval is 1s, far longer than this test takes.

        // 1. First ever Start: the cache is empty, so this always reports a change. That is the
        //    app-launch path, and it must keep working -- the fix must not be "always emit".
        tracker.Start();
        Assert.Equal(1, Volatile.Read(ref changes));

        // 2. Control: Start() again with no Stop() in between. The cache is intact and the
        //    foreground has not changed, so this must stay silent. If this fails, the machine's
        //    foreground is moving underneath the test and step 3 proves nothing.
        tracker.Start();
        Assert.Equal(1, Volatile.Read(ref changes));

        // 3. The fix: Stop() must clear the cached identity, so the resume poll reports a change
        //    and the service opens a fresh segment. Before the fix this stayed at 1.
        tracker.Stop();
        tracker.Start();
        Assert.Equal(2, Volatile.Read(ref changes));

        tracker.Stop();
    }

    /// <summary>
    /// Regression coverage for ResolveForegroundApp attaching a freshly-read title to
    /// ForegroundApp.Unknown on the cache-miss path -- a real title stored against a process
    /// that could not be identified. The fix withholds the title whenever resolution lands on
    /// Unknown, cached or not.
    ///
    /// This cannot force that path: GetForegroundWindow, GetWindowThreadProcessId and
    /// Process.GetProcessById are real Win32/CLR calls with no seam to inject a failure, and
    /// this test host has an interactive desktop with a real foreground window (verified by
    /// hand while writing this test), so Current resolves to an actual process here, not
    /// Unknown. The assertion is therefore an implication, not a forced branch: it only bites
    /// in an environment where resolution genuinely fails (a disconnected session, a service
    /// account, GetForegroundWindow returning null) -- which is exactly the environment the
    /// bug was reachable in. It is a real regression guard there and a no-op here, never a
    /// false pass.
    /// </summary>
    [Fact]
    public void Current_NeverAttachesATitleToAnUnresolvedApp()
    {
        using var tracker = new ForegroundTracker();
        ForegroundApp current = tracker.Current;

        if (current == ForegroundApp.Unknown)
        {
            Assert.Null(current.WindowTitle);
        }
    }

    [Fact]
    public void Stop_IsSafeToCallRepeatedlyAndAfterDispose()
    {
        var tracker = new ForegroundTracker();

        // Rapid pause/resume/pause must not throw or wedge; clearing the cache is idempotent.
        tracker.Start();
        tracker.Stop();
        tracker.Stop();
        tracker.Start();
        tracker.Stop();

        // Dispose does not route through Stop(), and Stop() short-circuits once disposed.
        tracker.Dispose();
        tracker.Stop();
        tracker.Dispose();
    }
}
