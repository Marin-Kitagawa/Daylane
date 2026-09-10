using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Daylane.Models;

namespace Daylane.Services;

internal sealed class TrackingService : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly DailyStatsStore _store;
    private readonly InputHook _hook;
    private readonly ForegroundTracker _foreground;
    private readonly OpenAppTracker _openApps;
    private readonly Timer _midnightCheckTimer;
    private readonly Timer _activityRefreshTimer;
    private readonly object _rolloverLock = new();
    private readonly object _segmentStateLock = new();
    private readonly object _openAppsLock = new();
    private readonly Dictionary<string, OpenAppState> _openAppStates = new(StringComparer.OrdinalIgnoreCase);
    private string _currentDateKey;
    private long _keyPressCount;
    private long _mouseClickCount;
    private OpenSegmentState? _openSegment;
    private string _currentAppName = "Starting…";
    private volatile bool _uiVisible = true;
    private volatile bool _trackingEnabled;
    private volatile bool _purging;
    private bool _started;
    private bool _disposed;
    private IReadOnlyList<IgnoreRule> _lastAppliedIgnoreRules = Array.Empty<IgnoreRule>();

    public TrackingService()
    {
        _store = new DailyStatsStore();
        Settings = new SettingsService(_store.ConnectionString);
        LegacyConfig.ImportOnce(Settings, LegacyConfig.DefaultPath);
        IdleMonitor.Bind(Settings);
        _trackingEnabled = Settings.Current.TrackingEnabled;
        _lastAppliedIgnoreRules = Settings.Current.IgnoreRules ?? Array.Empty<IgnoreRule>();
        _store.CloseOrphanOpenSegments(DateTime.UtcNow);

        // Runs after CloseOrphanOpenSegments: a crash-orphaned open segment must be closed
        // before retention can consider it for deletion, or a still-open row older than the
        // window would be deleted out from under a session that never got to close it.
        if (Settings.Current.RetentionDays > 0)
        {
            try
            {
                _store.PruneOldData(Settings.Current.RetentionDays);
            }
            catch (Exception ex)
            {
                // A prune failure (locked file, I/O error) must not block startup - the
                // session should come up normally and simply retry pruning next launch.
                Debug.WriteLine($"Retention prune failed at startup: {ex.Message}");
            }
        }

        _currentDateKey = TodayKey();
        (long keys, long clicks) = _store.GetTodayTotals();
        _keyPressCount = keys;
        _mouseClickCount = clicks;
        _hook = new InputHook(OnInputCaptured);
        _foreground = new ForegroundTracker();
        _foreground.Changed += OnForegroundChanged;
        _openApps = new OpenAppTracker();
        _openApps.Changed += OnOpenAppsChanged;

        // Subscribed last, once every field SetTrackingEnabled touches (_foreground, _openApps)
        // exists: the callback itself only runs later, marshalled onto the UI thread, but there
        // is no reason to leave it referencing fields ahead of their assignment.
        Settings.Changed += OnSettingsChanged;
        _midnightCheckTimer = new Timer(_ => CheckDateRollover(), null, 60000, 60000);
        _activityRefreshTimer = new Timer(
            _ => PublishActivity(),
            null,
            UiRefreshInterval,
            UiRefreshInterval);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal SettingsService Settings { get; }

    public string DatabasePath => _store.DatabasePath;

    public long KeyPressCount => _keyPressCount;

    public long MouseClickCount => _mouseClickCount;

    // Started and not paused by DaylaneSettings.TrackingEnabled. Drives the header's
    // Live/Stopped badge, so pausing tracking is visible without any new UI.
    public bool IsRunning => _started && _trackingEnabled;

    public string CurrentAppName => _currentAppName;

    public string CurrentDateKey => _currentDateKey;

    public IReadOnlyList<ActivitySegment> TodaySegments { get; private set; } = [];

    public IReadOnlyList<AppUsageSummary> TodayAppUsage { get; private set; } = [];

    public IReadOnlyList<OpenAppSummary> TodayOpenApps { get; private set; } = [];

    public DaySnapshot GetDaySnapshot(DateTime localDay)
    {
        DateTime day = localDay.Date;
        string dateKey = day.ToString("yyyy-MM-dd");
        bool isToday = dateKey == TodayKey();

        long keys;
        long clicks;
        if (isToday)
        {
            keys = _keyPressCount;
            clicks = _mouseClickCount;
            FlushOpenSegmentCounts();
        }
        else
        {
            (keys, clicks) = _store.GetTotalsForDate(dateKey);
        }

        var segments = _store.GetSegmentsForLocalDay(day);
        return new DaySnapshot(
            day,
            keys,
            clicks,
            segments,
            _store.AggregateAppUsage(segments, day),
            _store.GetOpenAppsForLocalDay(day));
    }

    public RangeSnapshot GetRangeSnapshot(DateTime startLocalInclusive, DateTime endLocalInclusive)
    {
        DateTime start = startLocalInclusive.Date;
        DateTime end = endLocalInclusive.Date;
        DateTime today = DateTime.Today;
        if (end > today)
        {
            end = today;
        }

        if (end < start)
        {
            end = start;
        }

        FlushOpenSegmentCounts();

        (long keys, long clicks) = _store.GetTotalsForDateRange(start, end);
        if (start <= today && today <= end)
        {
            (long storeTodayKeys, long storeTodayClicks) = _store.GetTotalsForDate(TodayKey());
            keys = keys - storeTodayKeys + _keyPressCount;
            clicks = clicks - storeTodayClicks + _mouseClickCount;
        }

        DateTime endExclusive = end.AddDays(1);
        var segments = _store.GetSegmentsForLocalRange(start, endExclusive);
        var appUsage = _store.AggregateAppUsage(segments, start, endExclusive);
        var dailyActive = BuildDailyActiveMinutes(segments, start, end);

        return new RangeSnapshot(start, end, keys, clicks, appUsage, dailyActive);
    }

    /// <summary>Thin passthrough to the store for the Day view's app-detail panel. Not
    /// unit-tested here for the same reason GetDaySnapshot/GetRangeSnapshot aren't: it adds
    /// nothing over DailyStatsStore.GetTitleUsage, which AppDetailQueryTests already covers.
    /// No FlushOpenSegmentCounts call needed first (unlike GetDaySnapshot/GetRangeSnapshot):
    /// that only reconciles KeyCount/MouseClickCount on the open segment's row, and
    /// GetTitleUsage's Title/UrlHost/Duration/SessionCount come from columns that call never
    /// touches -- the still-open segment's duration is already live because the store computes
    /// it against the current time, not a stored EndUtc.</summary>
    public IReadOnlyList<TitleUsageSummary> GetTitleUsage(
        string exePath,
        DateTime rangeStartLocal,
        DateTime rangeEndExclusiveLocal) =>
        _store.GetTitleUsage(exePath, rangeStartLocal, rangeEndExclusiveLocal);

    /// <summary>Thin passthrough to the store backing the Day view's search box. Not
    /// unit-tested here for the same reason: TitleSearchTests already covers
    /// DailyStatsStore.SearchTitles itself.</summary>
    public IReadOnlyList<ActivitySegment> SearchTitles(
        string query,
        DateTime rangeStartLocal,
        DateTime rangeEndLocal,
        int limit = 200) =>
        _store.SearchTitles(query, rangeStartLocal, rangeEndLocal, limit);

    private void FlushOpenSegmentCounts()
    {
        lock (_segmentStateLock)
        {
            if (_openSegment is { } open)
            {
                _store.UpdateOpenSegmentCounts(open.Id, open.KeyCount, open.MouseClickCount);
            }
        }
    }

    /// <summary>Active minutes per local day for the Insights chart. Internal rather than
    /// private so the rule that matters can be asserted directly: excluded time must not show
    /// up in these minutes. Pure and static -- nothing here touches the instance, which is what
    /// makes it testable at all (a TrackingService cannot be constructed in a unit test).</summary>
    internal static IReadOnlyList<DailyActivityPoint> BuildDailyActiveMinutes(
        IReadOnlyList<ActivitySegment> segments,
        DateTime startLocal,
        DateTime endLocalInclusive)
    {
        int dayCount = (endLocalInclusive - startLocal).Days + 1;
        var minutes = new double[dayCount];

        // Idle segments are dropped here (this chart is active minutes, and idle time has its
        // own row elsewhere); excluded segments are dropped by SegmentWindows, along with the
        // range clip and now-clamp this used to spell out for itself.
        foreach (var (_, windowStart, segEnd) in SegmentWindows.Counted(
            segments.Where(s => !s.IsIdle),
            startLocal.ToUniversalTime(),
            endLocalInclusive.AddDays(1).ToUniversalTime(),
            DateTime.UtcNow))
        {
            DateTime segStart = windowStart;

            while (segStart < segEnd)
            {
                DateTime local = segStart.ToLocalTime();
                DateTime dayStartLocal = local.Date;
                DateTime dayEndLocal = dayStartLocal.AddDays(1);
                DateTime sliceEnd = segEnd < dayEndLocal.ToUniversalTime()
                    ? segEnd
                    : dayEndLocal.ToUniversalTime();
                int index = (dayStartLocal - startLocal).Days;
                if (index >= 0 && index < dayCount)
                {
                    minutes[index] += (sliceEnd - segStart).TotalMinutes;
                }

                segStart = sliceEnd;
            }
        }

        var points = new DailyActivityPoint[dayCount];
        for (int i = 0; i < dayCount; i++)
        {
            points[i] = new DailyActivityPoint(startLocal.AddDays(i), minutes[i]);
        }

        return points;
    }

    public void SetUiVisible(bool visible)
    {
        if (_uiVisible == visible)
        {
            return;
        }

        _uiVisible = visible;
        if (visible)
        {
            PublishActivity(force: true);
        }
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // The input hook has no supported way to reinstall after Dispose (Task 13 review), so
        // it is always installed here regardless of TrackingEnabled; OnInputCaptured is what
        // gates whether it actually records anything. The foreground/open-app pollers can be
        // paused and resumed cleanly, so those only start when tracking begins enabled.
        _hook.Install();
        if (_trackingEnabled)
        {
            _foreground.Start();
            _openApps.Start();
        }

        OnPropertyChanged(nameof(IsRunning));
        PublishActivity();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Settings.Changed -= OnSettingsChanged;
        _midnightCheckTimer.Dispose();
        _activityRefreshTimer.Dispose();
        _foreground.Changed -= OnForegroundChanged;
        _foreground.Dispose();
        _openApps.Changed -= OnOpenAppsChanged;
        _openApps.Dispose();

        CloseOpenSegment(DateTime.UtcNow);
        CloseAllOpenApps(DateTime.UtcNow);

        if (_started)
        {
            _hook.Dispose();
            _started = false;
        }

        _store.Dispose();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            SetTrackingEnabled(e.Settings.TrackingEnabled);

            // Only against rules that actually reached the database. A rule that applies in
            // memory but was never stored disappears at the next restart; recomputing the whole
            // table against it would leave that history excluded with no rule left to delete,
            // which is the one outcome the ignore-rule design must not produce. The rule still
            // takes effect for the rest of this session through CapturePolicy, so the user is
            // not silently ignored -- only the irreversible half is held back.
            if (e.Persisted)
            {
                RecomputeExcludedIfRulesChanged(e.Settings);
            }
        });

    // Runs on the UI thread. The compare-and-swap on _lastAppliedIgnoreRules must happen here,
    // not on the background thread, so that two rapid settings saves serialize on deciding
    // whether to recompute rather than racing each other into two overlapping passes. Only the
    // database work itself (RecomputeExcluded, a full-table pass) is pushed off the UI thread:
    // on a database with a year of history that pass is slow enough to freeze the window if run
    // here, which is the same defect a reviewer caught one task earlier for a different slow call.
    private void RecomputeExcludedIfRulesChanged(DaylaneSettings settings)
    {
        IReadOnlyList<IgnoreRule> rules = settings.IgnoreRules ?? Array.Empty<IgnoreRule>();
        if (rules.SequenceEqual(_lastAppliedIgnoreRules))
        {
            return;
        }

        IReadOnlyList<IgnoreRule> previouslyApplied = _lastAppliedIgnoreRules;
        _lastAppliedIgnoreRules = rules;

        // Re-read Settings.Persisted.IgnoreRules at execution time rather than close over `rules`:
        // two rapid rule edits each pass this compare-and-swap and queue their own Task.Run body,
        // and those two bodies race on _dbWriteLock with no ordering guarantee. If they closed
        // over the list captured at queue time, whichever body's lock acquisition lost the race
        // could still win the write, leaving the database reflecting the older rule set while
        // _lastAppliedIgnoreRules already claims the newer -- and nothing re-triggers a pass to
        // fix it. Reading fresh here means whichever body actually runs last applies the current
        // rules, so the final state is correct regardless of completion order.
        // Persisted, not Current: a rule edit that failed to reach the database applies in
        // memory for this session but is gone at the next restart, and a full-table recompute
        // against it would leave history excluded with no rule left to delete. Reading Persisted
        // keeps "the newest rule set wins" without ever judging the table by a rule that is not
        // stored alongside it.
        Task.Run(() =>
        {
            try
            {
                _store.RecomputeExcluded(Settings.Persisted.IgnoreRules ?? Array.Empty<IgnoreRule>());
            }
            catch (Exception ex)
            {
                // A failed recompute must not take down a settings save. Nothing recomputes at
                // startup -- _lastAppliedIgnoreRules is seeded from the stored settings in the
                // constructor -- so a restart is NOT another chance: whatever this pass failed
                // to write stays stale until something makes the guard above fire again. Roll
                // the marker back to what was actually applied so the next settings write of
                // any kind retries, instead of short-circuiting on a rule set that never
                // reached the table.
                Dispatcher.UIThread.Post(() =>
                {
                    // Only if no later edit has moved on in the meantime; that pass owns the
                    // marker now and its own failure path will roll back to its own baseline.
                    if (ReferenceEquals(_lastAppliedIgnoreRules, rules))
                    {
                        _lastAppliedIgnoreRules = previouslyApplied;
                    }
                });

                Debug.WriteLine($"Excluded recompute failed: {ex.Message}");
            }
        });
    }

    private void SetTrackingEnabled(bool enabled)
    {
        if (_disposed || _trackingEnabled == enabled)
        {
            return;
        }

        _trackingEnabled = enabled;

        if (!enabled)
        {
            // Pause polling and close whatever is currently open, so a paused stretch reads
            // as "nothing happening" rather than one very long segment once tracking resumes.
            // The input hook itself is left running (see Start()) but OnInputCaptured now
            // drops everything it reports.
            if (_started)
            {
                _foreground.Stop();
                _openApps.Stop();
            }

            CloseOpenSegment(DateTime.UtcNow);
            CloseAllOpenApps(DateTime.UtcNow);
            PublishActivity(force: true);
        }
        else if (_started)
        {
            _foreground.Start();
            _openApps.Start();
        }

        OnPropertyChanged(nameof(IsRunning));
    }

    private void OnForegroundChanged(ForegroundApp app)
    {
        // Also decline while a purge is in flight: a segment opened between the close and the
        // delete would be deleted out from under the id we just stored -- the exact dangling-id
        // defect the close-first ordering in PurgeRecordedActivity exists to prevent. A missed
        // foreground switch during a purge is correct; the alternative is recording a segment
        // that is about to be deleted anyway.
        if (_disposed || !_trackingEnabled || _purging)
        {
            return;
        }

        CheckDateRollover();

        // Entering Away: backdate to last input so the inactive span is fully visible.
        // Leaving Away / app switch: boundary is now (same as Inactivity Timer wake).
        DateTime boundaryUtc = app.IsIdle
            ? IdleMonitor.LastInputUtc()
            : DateTime.UtcNow;

        SwitchSegment(app, boundaryUtc);
        PublishActivity();
    }

    private void OnOpenAppsChanged(IReadOnlyList<ForegroundApp> apps)
    {
        // Same reasoning as OnForegroundChanged: an open-app row opened between
        // CloseAllOpenApps and the delete would be deleted out from under its id.
        if (_disposed || !_trackingEnabled || _purging)
        {
            return;
        }

        CheckDateRollover();
        SyncOpenApps(apps, DateTime.UtcNow);
        PublishActivity();
    }

    private void SyncOpenApps(IReadOnlyList<ForegroundApp> apps, DateTime boundaryUtc)
    {
        lock (_openAppsLock)
        {
            var sampledKeys = new HashSet<string>(
                apps.Select(OpenAppTracker.IdentityKey),
                StringComparer.OrdinalIgnoreCase);

            foreach (var key in _openAppStates.Keys.ToList())
            {
                if (sampledKeys.Contains(key))
                {
                    continue;
                }

                if (_openAppStates.Remove(key, out OpenAppState? state))
                {
                    DateTime closeAt = boundaryUtc < state.StartedUtc ? state.StartedUtc : boundaryUtc;
                    _store.CloseOpenAppSegment(state.Id, closeAt);
                }
            }

            foreach (ForegroundApp app in apps)
            {
                string key = OpenAppTracker.IdentityKey(app);
                if (_openAppStates.ContainsKey(key))
                {
                    continue;
                }

                long id = _store.OpenOpenAppSegment(app, boundaryUtc);
                _openAppStates[key] = new OpenAppState(id, app, boundaryUtc);
            }
        }
    }

    private void CloseAllOpenApps(DateTime utcNow)
    {
        lock (_openAppsLock)
        {
            foreach (OpenAppState state in _openAppStates.Values)
            {
                _store.CloseOpenAppSegment(state.Id, utcNow);
            }

            _openAppStates.Clear();
        }
    }

    // Called from the input-hook thread. Must stay lock-free and allocation-light.
    private void OnInputCaptured(InputEvent inputEvent)
    {
        // The hook itself cannot be safely uninstalled and reinstalled (see Start()), so
        // TrackingEnabled=false is honored here instead: drop everything the hook reports.
        if (!_trackingEnabled)
        {
            return;
        }

        _store.Enqueue(inputEvent);

        if (inputEvent.EventType == "Key")
        {
            Interlocked.Increment(ref _keyPressCount);
            Volatile.Read(ref _openSegment)?.AddKey();
        }
        else if (inputEvent.EventType == "Mouse")
        {
            Interlocked.Increment(ref _mouseClickCount);
            Volatile.Read(ref _openSegment)?.AddClick();
        }
    }

    private void SwitchSegment(ForegroundApp app, DateTime boundaryUtc)
    {
        // Snapshot for the browser-host gate below only. It has to be read before the walk, and
        // it is the right value for deciding whether to walk at all.
        DaylaneSettings settings = Settings.Current;

        // MUST stay outside lock (_segmentStateLock). This is a cross-process COM call into the
        // browser's UI Automation provider: ~46ms when the browser is healthy and UNBOUNDED when
        // its renderer is hung. The UI thread takes this same lock via GetDaySnapshot,
        // GetRangeSnapshot and SetTrackingEnabled, so holding it across the walk would freeze the
        // whole window for as long as the browser stayed stuck. Do not fold this back into the
        // locked block.
        //
        // RecordWindowTitles is part of the gate, not just RecordBrowserHost: Apply strips
        // UrlHost whenever titles are off, so without it a browser segment would pay for the walk
        // to produce a value that is guaranteed to be discarded.
        //
        // Runs once per segment, never on the one-second poll -- ForegroundTracker raises Changed
        // only when the app identity actually changes. Kept outside CapturePolicy.Apply as well,
        // because Apply is pure and is reused by the rule-change recompute, where there is no
        // live foreground window to interrogate. Enriching before Apply means a
        // privacy-suppressed segment discards the host instead of storing it.
        if (settings.RecordBrowserHost
            && settings.RecordWindowTitles
            && BrowserHost.IsBrowser(app.ProcessName))
        {
            app = app with { UrlHost = BrowserHost.TryGetForegroundHost() };
        }

        lock (_segmentStateLock)
        {
            if (_openSegment is { } open && open.App.SameIdentity(app))
            {
                return;
            }

            if (_openSegment is { } current)
            {
                DateTime closeAt = boundaryUtc;
                if (closeAt < current.StartedUtc)
                {
                    closeAt = current.StartedUtc;
                }

                if (closeAt > DateTime.UtcNow)
                {
                    closeAt = DateTime.UtcNow;
                }

                _store.CloseSegment(current.Id, closeAt, current.KeyCount, current.MouseClickCount);
                Volatile.Write(ref _openSegment, null);

                // Keep Away contiguous with the closed app segment.
                if (app.IsIdle && boundaryUtc < closeAt)
                {
                    boundaryUtc = closeAt;
                }
            }

            DateTime openAt = boundaryUtc;
            if (openAt > DateTime.UtcNow)
            {
                openAt = DateTime.UtcNow;
            }

            // Re-read Settings.Current here rather than reuse the gate's snapshot: the browser
            // walk above has no bound on how long it runs, and the newest settings must win over
            // whatever was current when the walk started. Every case resolves correctly under
            // this rule -- if titles were switched off during the walk, Apply strips the host it
            // just fetched; if they were switched on, UrlHost stays null because nothing was
            // fetched, which is the honest outcome. And the exclusion verdict is judged by the
            // newest rules, so it agrees with whatever RecomputeExcluded just wrote for every
            // other row, instead of a stale snapshot writing one row the recompute can never
            // revisit (nothing re-triggers a pass once _lastAppliedIgnoreRules already matches
            // the new list).
            ForegroundApp stored = CapturePolicy.Apply(app, Settings.Current, out bool excluded);
            long id = _store.OpenSegment(stored, openAt, excluded);
            Volatile.Write(ref _openSegment, new OpenSegmentState(id, stored, openAt));
            _currentAppName = stored.DisplayName;
        }

        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CurrentAppName)));
    }

    private void CloseOpenSegment(DateTime utcNow)
    {
        lock (_segmentStateLock)
        {
            if (_openSegment is not { } current)
            {
                return;
            }

            _store.CloseSegment(current.Id, utcNow, current.KeyCount, current.MouseClickCount);
            Volatile.Write(ref _openSegment, null);
        }
    }

    /// <summary>
    /// Closes whatever is open, deletes all recorded activity, and resets the live counters.
    ///
    /// The close must come first: this service holds an open segment's row id, and purging
    /// underneath it would leave the next flush writing counts against a row that no longer
    /// exists -- a silent no-op that leaves memory and the database permanently disagreeing.
    /// </summary>
    internal int PurgeRecordedActivity()
    {
        // Set before the close, cleared in finally: between CloseOpenSegment/CloseAllOpenApps
        // returning and the delete actually running, the foreground- and open-apps-changed
        // handlers must not open a new segment or app row against an id the purge is about to
        // delete -- that is the exact dangling-id defect the close-first ordering exists to
        // prevent, just reintroduced by a race instead of by ordering. See OnForegroundChanged
        // and OnOpenAppsChanged for the other half of this guard.
        _purging = true;
        try
        {
            CloseOpenSegment(DateTime.UtcNow);
            CloseAllOpenApps(DateTime.UtcNow);
            _store.Flush();

            int deleted = _store.PurgeRecordedActivity();

            lock (_rolloverLock)
            {
                _keyPressCount = 0;
                _mouseClickCount = 0;
                _currentDateKey = TodayKey();
            }

            // Force, so the UI shows zeroes now rather than at the next five-second tick.
            PublishActivity(force: true);
            return deleted;
        }
        finally
        {
            _purging = false;
        }
    }

    internal StorageReport MeasureStorage() =>
        StorageUsage.Measure(_store.DatabasePath, _store.ConnectionString);

    private void PublishActivity(bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        if (!force && !_uiVisible)
        {
            return;
        }

        lock (_segmentStateLock)
        {
            if (_openSegment is { } open)
            {
                _store.UpdateOpenSegmentCounts(open.Id, open.KeyCount, open.MouseClickCount);
            }
        }

        DateTime today = DateTime.Now;
        var segments = _store.GetSegmentsForLocalDay(today);
        var usage = _store.AggregateAppUsage(segments, today);
        var openApps = _store.GetOpenAppsForLocalDay(today);

        Dispatcher.UIThread.Post(() =>
        {
            TodaySegments = segments;
            TodayAppUsage = usage;
            TodayOpenApps = openApps;
            OnPropertyChanged(nameof(TodaySegments));
            OnPropertyChanged(nameof(TodayAppUsage));
            OnPropertyChanged(nameof(TodayOpenApps));
            OnPropertyChanged(nameof(KeyPressCount));
            OnPropertyChanged(nameof(MouseClickCount));
        });
    }

    private void CheckDateRollover()
    {
        string today = TodayKey();
        if (today == _currentDateKey)
        {
            return;
        }

        lock (_rolloverLock)
        {
            today = TodayKey();
            if (today == _currentDateKey)
            {
                return;
            }

            DateTime utcNow = DateTime.UtcNow;
            ForegroundApp? resumeApp = null;

            lock (_segmentStateLock)
            {
                if (_openSegment is { } open)
                {
                    resumeApp = open.App;
                    _store.CloseSegment(open.Id, utcNow, open.KeyCount, open.MouseClickCount);
                    Volatile.Write(ref _openSegment, null);
                }
            }

            _currentDateKey = today;
            _store.Flush();
            (long keys, long clicks) = _store.GetTodayTotals();
            _keyPressCount = keys;
            _mouseClickCount = clicks;

            if (resumeApp is { } app)
            {
                SwitchSegment(app, utcNow);
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(CurrentDateKey));
            OnPropertyChanged(nameof(KeyPressCount));
            OnPropertyChanged(nameof(MouseClickCount));
        });

        PublishActivity(force: true);
    }

    private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class OpenSegmentState(long id, ForegroundApp app, DateTime startedUtc)
    {
        private long _keyCount;
        private long _mouseClickCount;

        public long Id { get; } = id;
        public ForegroundApp App { get; } = app;
        public DateTime StartedUtc { get; } = startedUtc;
        public long KeyCount => Interlocked.Read(ref _keyCount);
        public long MouseClickCount => Interlocked.Read(ref _mouseClickCount);

        public void AddKey() => Interlocked.Increment(ref _keyCount);
        public void AddClick() => Interlocked.Increment(ref _mouseClickCount);
    }

    private sealed class OpenAppState(long id, ForegroundApp app, DateTime startedUtc)
    {
        public long Id { get; } = id;
        public ForegroundApp App { get; } = app;
        public DateTime StartedUtc { get; } = startedUtc;
    }
}
