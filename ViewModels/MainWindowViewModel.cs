using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Daylane.Controls;
using Daylane.Models;
using Daylane.Services;

namespace Daylane.ViewModels;

internal enum AppTab
{
    Day,
    Insights,
    Settings
}

internal enum InsightPeriod
{
    Week,
    Month
}

/// <summary>
/// Resolves the away/idle swatch color for the non-Control list rows (segment and app
/// usage rows) that mirror TimelineBar's idle styling but have no IResourceHost of their
/// own to cache a TimelinePalette against or a theme-change event to invalidate it on.
/// Resolved fresh from Application.Current on each call -- these rows are rebuilt on
/// selection/refresh anyway, so a live lookup costs nothing and never goes stale.
/// </summary>
internal static class IdlePalette
{
    public static Color Fill
    {
        get
        {
            var app = Avalonia.Application.Current!;
            return TimelinePalette.Resolve(app, app.ActualThemeVariant).IdleFill;
        }
    }
}

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly TrackingService _tracking;
    private readonly SettingsService _settings;
    private readonly ObservableCollection<AppUsageItemViewModel> _appUsage = [];
    private readonly ObservableCollection<AppUsageItemViewModel> _insightAppUsage = [];
    private readonly ObservableCollection<ActivitySegmentItemViewModel> _segmentItems = [];
    private readonly ObservableCollection<OpenAppItemViewModel> _openApps = [];
    private IReadOnlyList<ActivitySegment> _timelineSegments = [];
    private IReadOnlyList<double> _hourlyValues = new double[24];
    private IReadOnlyList<double> _insightDailyValues = [];
    private IReadOnlyList<string> _insightDailyLabels = [];
    private AppTab _selectedTab = AppTab.Day;
    private InsightPeriod _insightPeriod = InsightPeriod.Week;
    private DateTime _selectedDay = DateTime.Today;
    private DateTime _periodAnchor = DateTime.Today;
    private string _screenTimeText = "0m";
    private string _activeDurationText = "0m";
    private string _idleDurationText = "0m";
    private string _keyPressCount = "0";
    private string _mouseClickCount = "0";
    private string _peakHourText = "No peak yet";
    private string _insightScreenTimeText = "0m";
    private string _insightActiveDurationText = "0m";
    private string _insightIdleDurationText = "0m";
    private string _insightKeyPressCount = "0";
    private string _insightMouseClickCount = "0";
    private string _insightPeakDayText = "No peak yet";
    private string _insightAvgActiveText = "0m";
    private long? _selectedSegmentId;
    private string _focusLongestText = "—";
    private string _focusLongestDetailText = "";
    private string _focusAwayCountText = "0";
    private string _focusSessionCountText = "0";
    private string _focusLastAwayText = "Last Away: —";
    private string _selectedDetailTitle = "";
    private string _selectedDetailRangeText = "";
    private string _selectedDetailDurationText = "";
    private string _selectedDetailKeysText = "0";
    private string _selectedDetailClicksText = "0";
    private string _selectedDetailShareText = "";
    private IBrush? _selectedDetailColor;
    private Avalonia.Media.Imaging.Bitmap? _selectedDetailIcon;
    private bool _selectedDetailIsAway;
    private double _timelineZoom = 2;
    private readonly ObservableCollection<string> _privacyKeywords = [];
    private readonly ObservableCollection<IgnoreRule> _ignoreRules = [];
    private string _newPrivacyKeyword = "";
    private string _newIgnoreRuleProcess = "";
    private string _newIgnoreRuleKeyword = "";
    private readonly ObservableCollection<TitleUsageItemViewModel> _selectedAppTitles = [];
    private AppUsageItemViewModel? _selectedAppUsageItem;
    private string _selectedAppDisplayName = "";
    private string _selectedAppTotalText = "";
    private bool _selectedAppDetailUnavailable;
    private string _searchQuery = "";
    private readonly ObservableCollection<TitleSearchItemViewModel> _searchResults = [];
    private TitleSearchItemViewModel? _selectedSearchItem;
    private string _updateStatus = "Automatic checks are off.";
    private string? _updateLinkUrl;
    private bool _confirmPurgeVisible;
    private string _storageSummary = "Calculating…";
    private string _recordedRowsSummary = "Calculating…";
    private string _purgeResultSummary = "";

    // Debounces SearchQuery: without it, every keystroke would run SearchTitles synchronously
    // against _dbWriteLock, which the background writer also takes.
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public MainWindowViewModel(TrackingService tracking)
    {
        _tracking = tracking;
        _settings = tracking.Settings;
        _tracking.PropertyChanged += OnTrackingPropertyChanged;
        SyncPrivacyLists();
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RefreshSearch();
        };

        // Settings.Changed is raised outside the service's lock and can arrive on any thread
        // (the tray's startup checkbox writes from the UI thread, but nothing guarantees that
        // stays true), and it fires for writes made by anyone -- not just this view model's own
        // setters -- so the Settings tab and the tray checkbox cannot drift apart.
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            SettingsChangeNotifier.Raise(p => OnPropertyChanged(p));

            // The privacy lists are ObservableCollections, not simple pass-through getters, so
            // re-raising their property name would not refresh what an ItemsControl shows. Any
            // settings write -- including one of this view model's own add/remove commands --
            // resyncs them from the store, which is the source of truth.
            SyncPrivacyLists();
        });
        OpenDataFolderCommand = new RelayCommand(() => OpenDataFolder(_tracking.DatabasePath));
        SelectDayCommand = new RelayCommand(() => SelectedTab = AppTab.Day);
        SelectInsightsCommand = new RelayCommand(() => SelectedTab = AppTab.Insights);
        SelectSettingsCommand = new RelayCommand(() => SelectedTab = AppTab.Settings);
        SelectWeekPeriodCommand = new RelayCommand(() => InsightPeriodKind = InsightPeriod.Week);
        SelectMonthPeriodCommand = new RelayCommand(() => InsightPeriodKind = InsightPeriod.Month);
        PreviousDayCommand = new RelayCommand(() => SelectedDay = SelectedDay.AddDays(-1));
        NextDayCommand = new RelayCommand(() => SelectedDay = SelectedDay.AddDays(1));
        SelectTodayCommand = new RelayCommand(() => SelectedDay = DateTime.Today);
        PreviousPeriodCommand = new RelayCommand(GoPreviousPeriod);
        NextPeriodCommand = new RelayCommand(GoNextPeriod);
        SelectCurrentPeriodCommand = new RelayCommand(GoCurrentPeriod);
        ClearSelectionCommand = new RelayCommand(() =>
        {
            SelectedSegmentId = null;
            SelectedAppUsageItem = null;
        });
        AddPrivacyKeywordCommand = new RelayCommand(AddPrivacyKeyword);
        RemovePrivacyKeywordCommand = new RelayCommand<string>(RemovePrivacyKeyword);
        AddIgnoreRuleCommand = new RelayCommand(AddIgnoreRule);
        RemoveIgnoreRuleCommand = new RelayCommand<IgnoreRule>(RemoveIgnoreRule);
        CheckForUpdatesCommand = new RelayCommand(() => _ = RunUpdateCheckAsync(userRequested: true));
        OpenUpdateLinkCommand = new RelayCommand(() => OpenWebUrl(_updateLinkUrl));
        PurgeDataCommand = new RelayCommand(() =>
        {
            PurgeResultSummary = "";
            ConfirmPurgeVisible = true;
        });
        ConfirmPurgeCommand = new RelayCommand(ConfirmPurge);
        CancelPurgeCommand = new RelayCommand(() =>
        {
            PurgeResultSummary = "";
            ConfirmPurgeVisible = false;
        });
        OpenRepositoryCommand = new RelayCommand(() => OpenWebUrl(AppInfo.RepositoryUrl));
        OpenIssuesCommand = new RelayCommand(() => OpenWebUrl(AppInfo.IssuesUrl));
        OpenLicenseCommand = new RelayCommand(() => OpenWebUrl(AppInfo.LicenseUrl));
        OpenUpstreamProjectCommand = new RelayCommand(() => OpenWebUrl(AppInfo.UpstreamProjectUrl));
        RefreshDay();
        RefreshInsights();

        // Fire-and-forget on purpose: an update check must never delay the window appearing,
        // and its failure is already a no-op. Guarded so a default install makes no request.
        if (_settings.Current.CheckForUpdates)
        {
            _ = RunUpdateCheckAsync(userRequested: false);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning => _tracking.IsRunning;

    public string DatabasePath => _tracking.DatabasePath;

    public string CurrentAppName => _tracking.CurrentAppName;

    public DateTime SelectedDay
    {
        get => _selectedDay;
        set
        {
            DateTime day = value.Date;
            if (day > DateTime.Today)
            {
                day = DateTime.Today;
            }

            if (_selectedDay.Date == day)
            {
                return;
            }

            _selectedDay = day;
            SelectedSegmentId = null;
            SelectedAppUsageItem = null;
            TimelineZoom = day == DateTime.Today ? 2 : 1;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DayLabel));
            OnPropertyChanged(nameof(IsSelectedDayToday));
            OnPropertyChanged(nameof(CanGoNextDay));
            RefreshDay();
        }
    }

    public bool IsSelectedDayToday => SelectedDay.Date == DateTime.Today;

    public bool CanGoNextDay => SelectedDay.Date < DateTime.Today;

    public string DayLabel => SelectedDay.ToString("ddd, MMM d, yyyy");

    public string KeyPressCount => _keyPressCount;

    public string MouseClickCount => _mouseClickCount;

    public string ScreenTimeText => _screenTimeText;

    public string ActiveDurationText => _activeDurationText;

    public string IdleDurationText => _idleDurationText;

    public string PeakHourText => _peakHourText;

    public string FocusLongestText => _focusLongestText;

    public string FocusLongestDetailText => _focusLongestDetailText;

    public string FocusAwayCountText => _focusAwayCountText;

    public string FocusSessionCountText => _focusSessionCountText;

    public string FocusLastAwayText => _focusLastAwayText;

    public bool HasSelectedSegment => _selectedSegmentId is not null && FindSelectedSegment() is not null;

    public string SelectedDetailTitle => _selectedDetailTitle;

    public string SelectedDetailRangeText => _selectedDetailRangeText;

    public string SelectedDetailDurationText => _selectedDetailDurationText;

    public string SelectedDetailKeysText => _selectedDetailKeysText;

    public string SelectedDetailClicksText => _selectedDetailClicksText;

    public string SelectedDetailShareText => _selectedDetailShareText;

    public IBrush? SelectedDetailColor => _selectedDetailColor;

    public Avalonia.Media.Imaging.Bitmap? SelectedDetailIcon => _selectedDetailIcon;

    public bool HasSelectedDetailIcon => _selectedDetailIcon is not null;

    public bool SelectedDetailIsAway => _selectedDetailIsAway;

    public double TimelineZoom
    {
        get => _timelineZoom;
        set
        {
            double zoom = Math.Clamp(value, 1, 8);
            if (Math.Abs(_timelineZoom - zoom) < 0.000001)
            {
                return;
            }

            _timelineZoom = zoom;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ActivitySegment> TimelineSegments => _timelineSegments;

    public ObservableCollection<AppUsageItemViewModel> AppUsage => _appUsage;

    public ObservableCollection<OpenAppItemViewModel> OpenApps => _openApps;

    public ObservableCollection<AppUsageItemViewModel> InsightAppUsage => _insightAppUsage;

    public ObservableCollection<ActivitySegmentItemViewModel> SegmentItems => _segmentItems;

    public long? SelectedSegmentId
    {
        get => _selectedSegmentId;
        set
        {
            if (_selectedSegmentId == value)
            {
                return;
            }

            _selectedSegmentId = value;

            // A segment and an app share the one "Selection" panel; picking one from the
            // timeline or the Activity list must drop the other so the panel doesn't have to
            // decide which detail wins.
            if (value is not null && _selectedAppUsageItem is not null)
            {
                SelectedAppUsageItem = null;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSegmentItem));
            OnPropertyChanged(nameof(ShowSelectionEmptyState));
            RefreshSelectedDetail();
        }
    }

    public ActivitySegmentItemViewModel? SelectedSegmentItem
    {
        get
        {
            if (_selectedSegmentId is not long id)
            {
                return null;
            }

            foreach (var item in _segmentItems)
            {
                if (item.Id == id)
                {
                    return item;
                }
            }

            return null;
        }
        set => SelectedSegmentId = value?.Id;
    }

    /// <summary>The row picked from the Apps list, driving the title breakdown below (via
    /// RefreshSelectedAppTitles) instead of the segment detail. Not the same selection concept
    /// as SelectedSegmentId -- an app row has no segment Id of its own -- so the two are kept
    /// as separate properties that clear one another rather than one overloaded field.</summary>
    public AppUsageItemViewModel? SelectedAppUsageItem
    {
        get => _selectedAppUsageItem;
        set
        {
            if (ReferenceEquals(_selectedAppUsageItem, value))
            {
                return;
            }

            _selectedAppUsageItem = value;
            if (value is not null && _selectedSegmentId is not null)
            {
                SelectedSegmentId = null;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedApp));
            OnPropertyChanged(nameof(ShowSelectionEmptyState));
            RefreshSelectedAppTitles();
        }
    }

    public bool HasSelectedApp => _selectedAppUsageItem is not null;

    /// <summary>The "No selection" empty state is only for when NEITHER a segment nor an app
    /// is picked -- once Task 13 gave the app row its own detail view, HasSelectedSegment alone
    /// stopped being the right guard for it.</summary>
    public bool ShowSelectionEmptyState => !HasSelectedSegment && !HasSelectedApp;

    public ObservableCollection<TitleUsageItemViewModel> SelectedAppTitles => _selectedAppTitles;

    public bool HasSelectedAppTitles => _selectedAppTitles.Count > 0;

    public string SelectedAppDisplayName => _selectedAppDisplayName;

    public string SelectedAppTotalText => _selectedAppTotalText;

    /// <summary>An app is selected and has no title breakdown to show -- either titles are off
    /// (the default, where every row comes back with no title and no host and
    /// TitleUsagePresenter reports no items) or every window's detail was suppressed. The one
    /// case this must NOT claim is an app whose detail could never be read at all; that has its
    /// own message below.</summary>
    public bool ShowNoTitlesForSelectedApp =>
        HasSelectedApp && !HasSelectedAppTitles && !_selectedAppDetailUnavailable;

    /// <summary>An app is selected but Daylane never resolved its program file, so there is no
    /// breakdown to be had whatever the settings say. Kept apart from
    /// ShowNoTitlesForSelectedApp because sending this user to the title-capture switch would
    /// be advice that cannot work.</summary>
    public bool ShowNoDetailForSelectedApp =>
        HasSelectedApp && !HasSelectedAppTitles && _selectedAppDetailUnavailable;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value)
            {
                return;
            }

            _searchQuery = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchActive));
            OnPropertyChanged(nameof(ShowSearchPopup));

            if (string.IsNullOrWhiteSpace(value))
            {
                // Closing (e.g. SelectedSearchItem clearing the query after a pick) must be
                // instant -- there is no DB query involved, so nothing to debounce, and a
                // 200ms lag here would read as the popup failing to close promptly.
                _searchDebounceTimer.Stop();
                RefreshSearch();
            }
            else
            {
                // Debounced, not immediate: each keystroke would otherwise run SearchTitles
                // synchronously against _dbWriteLock, which the background writer also takes.
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }
    }

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(_searchQuery);

    /// <summary>Gates the results Popup on IsDaySelected too, not just IsSearchActive: the
    /// search box only exists in the Day header, but a query typed there survives switching to
    /// Insights or Settings, and the Popup's PlacementTarget (the search box) would be hidden
    /// with its whole header on those tabs -- it must not pop open floating over the wrong tab.</summary>
    public bool ShowSearchPopup => IsDaySelected && IsSearchActive;

    public ObservableCollection<TitleSearchItemViewModel> SearchResults => _searchResults;

    public bool HasSearchResults => _searchResults.Count > 0;

    public bool ShowSearchEmptyState => IsSearchActive && !HasSearchResults;

    public TitleSearchItemViewModel? SelectedSearchItem
    {
        get => _selectedSearchItem;
        set
        {
            if (ReferenceEquals(_selectedSearchItem, value))
            {
                return;
            }

            _selectedSearchItem = value;
            OnPropertyChanged();
            if (value is { SegmentId: long id })
            {
                SelectedSegmentId = id;

                // Picking a result both jumps to that segment AND closes the popup -- without
                // this the 380x360 dropdown stays parked over the Day view the user was just
                // sent to, with IsLightDismissEnabled left off deliberately (light dismiss would
                // fight the search TextBox for focus while typing).
                SearchQuery = "";
            }
        }
    }

    public bool HasAppUsage => _appUsage.Count > 0;

    public bool HasOpenApps => _openApps.Count > 0;

    public bool HasSegments => _segmentItems.Count > 0;

    public bool HasDayActivity => _timelineSegments.Count > 0;

    public bool HasInsightAppUsage => _insightAppUsage.Count > 0;

    public IReadOnlyList<double> InsightDailyValues => _insightDailyValues;

    public IReadOnlyList<string> InsightDailyLabels => _insightDailyLabels;

    public string InsightScreenTimeText => _insightScreenTimeText;

    public string InsightActiveDurationText => _insightActiveDurationText;

    public string InsightIdleDurationText => _insightIdleDurationText;

    public string InsightKeyPressCount => _insightKeyPressCount;

    public string InsightMouseClickCount => _insightMouseClickCount;

    public string InsightPeakDayText => _insightPeakDayText;

    public string InsightAvgActiveText => _insightAvgActiveText;

    public string PeriodLabel
    {
        get
        {
            var (start, end) = GetPeriodBounds();
            if (InsightPeriodKind == InsightPeriod.Month)
            {
                return start.ToString("MMMM yyyy");
            }

            if (start.Year == end.Year)
            {
                return $"{start:MMM d} – {end:MMM d, yyyy}";
            }

            return $"{start:MMM d, yyyy} – {end:MMM d, yyyy}";
        }
    }

    public bool CanGoNextPeriod
    {
        get
        {
            var (_, end) = GetPeriodBounds();
            return end.Date < DateTime.Today;
        }
    }

    public AppTab SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (_selectedTab == value)
            {
                return;
            }

            _selectedTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDaySelected));
            OnPropertyChanged(nameof(IsInsightsSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
            OnPropertyChanged(nameof(ShowSearchPopup));
            if (value == AppTab.Insights)
            {
                RefreshInsights();
            }
            else if (value == AppTab.Settings)
            {
                // Measured only when the tab is actually looked at, not on a timer -- two stat
                // calls behind a tab nobody is viewing would be pure waste.
                RefreshStorage();
            }
        }
    }

    public InsightPeriod InsightPeriodKind
    {
        get => _insightPeriod;
        private set
        {
            if (_insightPeriod == value)
            {
                return;
            }

            _insightPeriod = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWeekPeriod));
            OnPropertyChanged(nameof(IsMonthPeriod));
            OnPropertyChanged(nameof(PeriodLabel));
            OnPropertyChanged(nameof(CanGoNextPeriod));
            RefreshInsights();
        }
    }

    public bool IsDaySelected => SelectedTab == AppTab.Day;

    public bool IsInsightsSelected => SelectedTab == AppTab.Insights;

    public bool IsSettingsSelected => SelectedTab == AppTab.Settings;

    public bool IsWeekPeriod => InsightPeriodKind == InsightPeriod.Week;

    public bool IsMonthPeriod => InsightPeriodKind == InsightPeriod.Month;

    public string Appearance
    {
        get => _settings.Current.Appearance;
        set
        {
            if (_settings.Current.Appearance == value)
            {
                return;
            }

            _settings.Update(s => s with { Appearance = value });
            OnPropertyChanged();
        }
    }

    public bool TrackingEnabled
    {
        get => _settings.Current.TrackingEnabled;
        set
        {
            if (_settings.Current.TrackingEnabled == value)
            {
                return;
            }

            _settings.Update(s => s with { TrackingEnabled = value });
            OnPropertyChanged();
        }
    }

    // Nullable, not int: NumericUpDown reports Value as null while its box is momentarily
    // empty (the user clearing it to retype). Avalonia's binding cannot convert null into a
    // non-nullable int at all -- it never reaches this setter -- but converts cleanly into
    // int?, so accepting null here and no-oping (while still re-raising the property) coerces
    // the control back to the stored value instead of leaving it blank.
    public int? IdleThresholdMinutes
    {
        get => _settings.Current.IdleThresholdMinutes;
        set
        {
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }

            if (_settings.Current.IdleThresholdMinutes == value)
            {
                return;
            }

            _settings.Update(s => s with { IdleThresholdMinutes = value.Value });
            OnPropertyChanged();
        }
    }

    public int? RetentionDays
    {
        get => _settings.Current.RetentionDays;
        set
        {
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }

            if (_settings.Current.RetentionDays == value)
            {
                return;
            }

            _settings.Update(s => s with { RetentionDays = value.Value });
            OnPropertyChanged();
        }
    }

    public bool AutoStart
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            if (StartupRegistration.IsEnabled() == value)
            {
                return;
            }

            try
            {
                StartupRegistration.SetEnabled(value);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or SecurityException)
            {
                // Registry write failed (no resolvable process path, or access denied). Snap
                // the toggle back to what the registry actually holds rather than leaving the
                // UI showing a change that never took effect.
                OnPropertyChanged();
                return;
            }

            _settings.Update(s => s with { AutoStart = StartupRegistration.IsEnabled() });
            OnPropertyChanged();
        }
    }

    public bool ShowWindowOnAutoStart
    {
        get => _settings.Current.ShowWindowOnAutoStart;
        set
        {
            if (_settings.Current.ShowWindowOnAutoStart == value)
            {
                return;
            }

            _settings.Update(s => s with { ShowWindowOnAutoStart = value });
            OnPropertyChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.Current.MinimizeToTray;
        set
        {
            if (_settings.Current.MinimizeToTray == value)
            {
                return;
            }

            _settings.Update(s => s with { MinimizeToTray = value });
            OnPropertyChanged();
        }
    }

    public bool RecordWindowTitles
    {
        get => _settings.Current.RecordWindowTitles;
        set
        {
            if (_settings.Current.RecordWindowTitles == value)
            {
                return;
            }

            _settings.Update(s => s with { RecordWindowTitles = value });
            OnPropertyChanged();

            // Normalize clears RecordBrowserHost when titles go off, even though neither was
            // set directly here -- the host toggle and its enabled state must catch up.
            OnPropertyChanged(nameof(RecordBrowserHost));
            OnPropertyChanged(nameof(CanRecordBrowserHost));

            // A title-keyword ignore rule can only match a stored title, so the box that
            // accepts one follows the same switch.
            OnPropertyChanged(nameof(CanIgnoreByTitleKeyword));
        }
    }

    public bool RecordBrowserHost
    {
        get => _settings.Current.RecordBrowserHost;
        set
        {
            if (_settings.Current.RecordBrowserHost == value)
            {
                return;
            }

            _settings.Update(s => s with { RecordBrowserHost = value });
            OnPropertyChanged();
        }
    }

    /// <summary>Bound to the host toggle's IsEnabled. Not unit-tested: MainWindowViewModel
    /// cannot be constructed in a unit test because its TrackingService opens a real database
    /// next to the executable. The invariant it mirrors -- a host cannot survive without a
    /// title -- is pinned instead where it actually lives, in DaylaneSettings.Normalize, by
    /// PrivacySettingsViewModelTests.</summary>
    public bool CanRecordBrowserHost => _settings.Current.RecordWindowTitles;

    /// <summary>Bound to the ignore rule's title-keyword box (IsEnabled) and, negated, to the
    /// line explaining why it is off. CapturePolicy judges exclusion against the title that
    /// was STORED -- deliberately, so the live verdict and the rule-change recompute can never
    /// disagree -- which means a title keyword can match nothing at all while title capture is
    /// off. That cost was accepted on the grounds that it would be visible immediately rather
    /// than silently, and this is what makes it visible: without it the box happily accepts a
    /// rule that is inert forever, with no feedback. Same treatment as CanRecordBrowserHost,
    /// and untestable for the same reason (MainWindowViewModel cannot be constructed in a unit
    /// test); the ordering rule it depends on is pinned in CaptureDefaultsTests.</summary>
    public bool CanIgnoreByTitleKeyword => _settings.Current.RecordWindowTitles;

    public ObservableCollection<string> PrivacyKeywords => _privacyKeywords;

    public ObservableCollection<IgnoreRule> IgnoreRules => _ignoreRules;

    public string NewPrivacyKeyword
    {
        get => _newPrivacyKeyword;
        set
        {
            if (_newPrivacyKeyword == value)
            {
                return;
            }

            _newPrivacyKeyword = value;
            OnPropertyChanged();
        }
    }

    public string NewIgnoreRuleProcess
    {
        get => _newIgnoreRuleProcess;
        set
        {
            if (_newIgnoreRuleProcess == value)
            {
                return;
            }

            _newIgnoreRuleProcess = value;
            OnPropertyChanged();
        }
    }

    public string NewIgnoreRuleKeyword
    {
        get => _newIgnoreRuleKeyword;
        set
        {
            if (_newIgnoreRuleKeyword == value)
            {
                return;
            }

            _newIgnoreRuleKeyword = value;
            OnPropertyChanged();
        }
    }

    public bool CheckForUpdates
    {
        get => _settings.Current.CheckForUpdates;
        set
        {
            if (_settings.Current.CheckForUpdates == value)
            {
                return;
            }

            _settings.Update(s => s with { CheckForUpdates = value });
            OnPropertyChanged();
        }
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (_updateStatus == value)
            {
                return;
            }

            _updateStatus = value;
            OnPropertyChanged();
        }
    }

    public string? UpdateLinkUrl
    {
        get => _updateLinkUrl;
        private set
        {
            if (_updateLinkUrl == value)
            {
                return;
            }

            _updateLinkUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUpdateLink));
        }
    }

    public bool HasUpdateLink => !string.IsNullOrWhiteSpace(_updateLinkUrl);

    public bool ConfirmPurgeVisible
    {
        get => _confirmPurgeVisible;
        private set
        {
            if (_confirmPurgeVisible == value)
            {
                return;
            }

            _confirmPurgeVisible = value;
            OnPropertyChanged();
        }
    }

    public string StorageSummary
    {
        get => _storageSummary;
        private set
        {
            if (_storageSummary == value)
            {
                return;
            }

            _storageSummary = value;
            OnPropertyChanged();
        }
    }

    public string RecordedRowsSummary
    {
        get => _recordedRowsSummary;
        private set
        {
            if (_recordedRowsSummary == value)
            {
                return;
            }

            _recordedRowsSummary = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Reports what ConfirmPurge just did (how many rows, or that it failed), separate
    /// from StorageSummary/RecordedRowsSummary -- those are refreshed to the post-purge state by
    /// the same call, which would immediately overwrite a message living on either of them.
    /// Cleared whenever the confirmation panel is opened or dismissed, so a stale result from a
    /// previous purge cannot linger into the next one.</summary>
    public string PurgeResultSummary
    {
        get => _purgeResultSummary;
        private set
        {
            if (_purgeResultSummary == value)
            {
                return;
            }

            _purgeResultSummary = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPurgeResult));
        }
    }

    public bool HasPurgeResult => !string.IsNullOrWhiteSpace(_purgeResultSummary);

    // Not a backing field: MeasureStorage's own DatabasePath is always exactly
    // _tracking.DatabasePath (StorageUsage.Measure is handed that very string), so this is
    // correct from construction, not only after the Settings tab is first selected, and it can
    // never drift from the pre-existing DatabasePath property below.
    public string DatabasePathDisplay => _tracking.DatabasePath;

    public string AppVersion => AppInfo.Version;

    public string AppAuthor => AppInfo.Author;

    public string AppLicense => AppInfo.License;

    public ICommand OpenDataFolderCommand { get; }

    public ICommand SelectDayCommand { get; }

    public ICommand SelectInsightsCommand { get; }

    public ICommand SelectSettingsCommand { get; }

    public ICommand SelectWeekPeriodCommand { get; }

    public ICommand SelectMonthPeriodCommand { get; }

    public ICommand PreviousDayCommand { get; }

    public ICommand NextDayCommand { get; }

    public ICommand SelectTodayCommand { get; }

    public ICommand PreviousPeriodCommand { get; }

    public ICommand NextPeriodCommand { get; }

    public ICommand SelectCurrentPeriodCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    public ICommand AddPrivacyKeywordCommand { get; }

    public ICommand RemovePrivacyKeywordCommand { get; }

    public ICommand AddIgnoreRuleCommand { get; }

    public ICommand RemoveIgnoreRuleCommand { get; }

    public ICommand CheckForUpdatesCommand { get; }

    public ICommand OpenUpdateLinkCommand { get; }

    public ICommand PurgeDataCommand { get; }

    public ICommand ConfirmPurgeCommand { get; }

    public ICommand CancelPurgeCommand { get; }

    public ICommand OpenRepositoryCommand { get; }

    public ICommand OpenIssuesCommand { get; }

    public ICommand OpenLicenseCommand { get; }

    public ICommand OpenUpstreamProjectCommand { get; }

    private void AddPrivacyKeyword()
    {
        string keyword = NewPrivacyKeyword.Trim();

        // Case-insensitive, matching PrivacyKeywords.Suppresses' own comparison -- a duplicate
        // that differs only in case would still be a duplicate to the matcher, so the UI's
        // idea of "already there" must agree with it.
        if (keyword.Length == 0
            || _privacyKeywords.Any(existing => string.Equals(existing, keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _privacyKeywords.Add(keyword);
        _settings.Update(s => s with { PrivacyKeywords = _privacyKeywords.ToList() });
        NewPrivacyKeyword = "";
    }

    private void RemovePrivacyKeyword(string keyword)
    {
        _privacyKeywords.Remove(keyword);
        _settings.Update(s => s with { PrivacyKeywords = _privacyKeywords.ToList() });
    }

    private void AddIgnoreRule()
    {
        string process = NewIgnoreRuleProcess.Trim();
        if (process.Length == 0)
        {
            return;
        }

        string keyword = NewIgnoreRuleKeyword.Trim();
        var rule = new IgnoreRule(process, keyword.Length == 0 ? null : keyword);

        // Case-insensitive on both fields, matching IgnoreRules.IsExcluded's own comparison of
        // ProcessName -- a duplicate that differs only in case would still be a duplicate to
        // the matcher, so the UI's idea of "already there" must agree with it.
        if (_ignoreRules.Any(existing =>
                string.Equals(existing.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.TitleKeyword, rule.TitleKeyword, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _ignoreRules.Add(rule);
        _settings.Update(s => s with { IgnoreRules = _ignoreRules.ToList() });
        NewIgnoreRuleProcess = "";
        NewIgnoreRuleKeyword = "";
    }

    private void RemoveIgnoreRule(IgnoreRule rule)
    {
        _ignoreRules.Remove(rule);
        _settings.Update(s => s with { IgnoreRules = _ignoreRules.ToList() });
    }

    /// <summary>
    /// Runs an update check and applies its result.
    ///
    /// This can be reached two ways: fire-and-forget from the constructor (no guaranteed
    /// synchronization context to resume on) and from CheckForUpdatesCommand (a UI-thread call,
    /// but the same method must be safe from both callers). The "Checking…" assignment above the
    /// await runs synchronously on whichever thread called this method -- today always the UI
    /// thread for both call sites -- so it is not itself marshaled. ConfigureAwait(false) is used
    /// deliberately on the await below -- not because the continuation's thread doesn't matter,
    /// but because the code after it does not depend on it: every bound-property mutation in the
    /// post-await callback is explicitly marshaled through Dispatcher.UIThread.Post rather than
    /// assumed to already be on the UI thread. Raising PropertyChanged off the UI thread in
    /// Avalonia is a crash or silent binding corruption, not a warning.
    /// </summary>
    private async Task RunUpdateCheckAsync(bool userRequested)
    {
        UpdateStatus = "Checking…";

        var checker = new UpdateChecker(ReleaseFetch.LatestReleaseJsonAsync, AppInfo.Version);
        UpdateCheckResult result = await checker
            .CheckAsync(_settings.Current, userRequested, CancellationToken.None)
            .ConfigureAwait(false);

        Dispatcher.UIThread.Post(() =>
        {
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.UpdateAvailable when result.Release is { } release:
                    UpdateStatus = $"Version {release.TagName} is available. You have {AppInfo.Version}.";
                    UpdateLinkUrl = release.HtmlUrl;
                    _settings.Update(s => s with { LastSeenVersion = release.TagName });
                    break;

                case UpdateCheckOutcome.UpToDate:
                    UpdateStatus = $"Daylane {AppInfo.Version} is up to date.";
                    UpdateLinkUrl = null;
                    break;

                case UpdateCheckOutcome.Failed:
                    UpdateStatus = "Could not reach GitHub.";
                    UpdateLinkUrl = null;
                    break;

                default:
                    UpdateStatus = "Automatic checks are off.";
                    UpdateLinkUrl = null;
                    break;
            }
        });
    }

    /// <summary>
    /// Measures the database and refreshes the two independent summaries the Data panel shows.
    /// DatabaseBytes and RecordedRows are measured through unrelated code paths (a file stat and
    /// a SQLite query) and either can fail while the other succeeds, so each is rendered on its
    /// own: a null here means "could not be measured", never "zero" -- collapsing the two would
    /// turn an unknown into a false claim of an empty database.
    /// </summary>
    private void RefreshStorage()
    {
        StorageReport report = _tracking.MeasureStorage();

        StorageSummary = report.DatabaseBytes is { } bytes
            ? FormatBytes(bytes)
            : "Size unavailable";

        RecordedRowsSummary = report.RecordedRows is { } rows
            ? $"{rows:N0} rows recorded"
            : "Row count unavailable";
    }

    /// <summary>
    /// The store's own DELETE transaction deliberately runs outside a try (a failure there must
    /// leave history intact rather than half-delete it), and TrackingService.PurgeRecordedActivity
    /// has only a finally -- so a SqliteException (locked file, disk full) propagates all the
    /// way up here, and this is the single most destructive action in the product. Catching it
    /// is what stands between that and an unhandled exception on the UI thread from
    /// RelayCommand.Execute.
    ///
    /// The purge and the post-purge refreshes are caught separately, on purpose: if the purge
    /// itself succeeds but a refresh afterward throws, a single shared try/catch around both
    /// would report "Could not delete recorded activity" -- telling the user nothing was deleted
    /// when in fact it was, which is worse than the crash it replaces because it is silent and
    /// wrong rather than loud. Splitting them means PurgeResultSummary, once set to a genuine
    /// success by the purge itself, can never be overwritten by an unrelated refresh failure.
    /// The finally clears ConfirmPurgeVisible on every path, so a failed purge (or a purge whose
    /// refresh failed) can never strand the confirmation panel visible.
    /// </summary>
    private void ConfirmPurge()
    {
        try
        {
            try
            {
                int deleted = _tracking.PurgeRecordedActivity();
                PurgeResultSummary = $"Deleted {deleted:N0} rows.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Purge failed: {ex.Message}");
                PurgeResultSummary = "Could not delete recorded activity.";
                return;
            }

            try
            {
                RefreshDay();
                RefreshInsights();
                RefreshStorage();
            }
            catch (Exception ex)
            {
                // The purge itself already succeeded and PurgeResultSummary already reflects
                // that -- a refresh failure here must not overwrite it with a false "nothing was
                // deleted" message. The panels affected simply keep showing pre-purge data until
                // the next refresh (e.g. the next Settings-tab selection) succeeds.
                Debug.WriteLine($"Post-purge refresh failed: {ex.Message}");
            }
        }
        finally
        {
            ConfirmPurgeVisible = false;
        }
    }

    /// <summary>internal, not private: unit-tested directly (FormatBytesTests) since it is the
    /// only pure logic this task adds -- everything else on this view model needs a live
    /// TrackingService and cannot be constructed in a test. Binary units, because that is what a
    /// file manager shows for the same file.</summary>
    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// Refreshes PrivacyKeywords/IgnoreRules from the settings store. This -- not
    /// SettingsChangeNotifier's flat re-raise list -- is what keeps these two in sync with any
    /// writer's change: both are ObservableCollections handed out by reference, so
    /// OnPropertyChanged(nameof(PrivacyKeywords)) would tell a bound ItemsControl nothing new
    /// happened. See the comment in SettingsChangeNotifier.Properties.
    /// </summary>
    private void SyncPrivacyLists()
    {
        _privacyKeywords.Clear();
        foreach (string keyword in _settings.Current.PrivacyKeywords ?? Array.Empty<string>())
        {
            _privacyKeywords.Add(keyword);
        }

        _ignoreRules.Clear();
        foreach (IgnoreRule rule in _settings.Current.IgnoreRules ?? Array.Empty<IgnoreRule>())
        {
            _ignoreRules.Add(rule);
        }
    }

    private void OnTrackingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TrackingService.IsRunning):
                OnPropertyChanged(nameof(IsRunning));
                break;
            case nameof(TrackingService.CurrentAppName):
                OnPropertyChanged(nameof(CurrentAppName));
                break;
            case nameof(TrackingService.CurrentDateKey):
                OnDateRollover();
                break;
            case nameof(TrackingService.TodaySegments):
            case nameof(TrackingService.TodayAppUsage):
            case nameof(TrackingService.TodayOpenApps):
                if (IsSelectedDayToday)
                {
                    RefreshDay();
                }

                if (IsInsightsSelected && PeriodIncludesToday())
                {
                    RefreshInsights();
                }

                break;
        }
    }

    private void OnDateRollover()
    {
        DateTime today = DateTime.Today;

        // SelectedDay does not move with the clock; follow the live day across midnight
        // so the UI does not keep a stale "today" snapshot with an open-ended segment.
        if (_selectedDay.Date == today.AddDays(-1))
        {
            SelectedDay = today;
        }
        else
        {
            OnPropertyChanged(nameof(IsSelectedDayToday));
            OnPropertyChanged(nameof(CanGoNextDay));
            if (_selectedDay.Date == today)
            {
                RefreshDay();
            }
        }

        OnPropertyChanged(nameof(CanGoNextPeriod));
        if (IsInsightsSelected && PeriodIncludesToday())
        {
            RefreshInsights();
        }
    }

    private void GoPreviousPeriod()
    {
        _periodAnchor = InsightPeriodKind == InsightPeriod.Week
            ? _periodAnchor.AddDays(-7)
            : _periodAnchor.AddMonths(-1);
        NotifyPeriodChanged();
        RefreshInsights();
    }

    private void GoNextPeriod()
    {
        DateTime next = InsightPeriodKind == InsightPeriod.Week
            ? _periodAnchor.AddDays(7)
            : _periodAnchor.AddMonths(1);
        if (GetPeriodBoundsFor(next, InsightPeriodKind).Start > DateTime.Today)
        {
            return;
        }

        _periodAnchor = next;
        NotifyPeriodChanged();
        RefreshInsights();
    }

    private void GoCurrentPeriod()
    {
        _periodAnchor = DateTime.Today;
        NotifyPeriodChanged();
        RefreshInsights();
    }

    private void NotifyPeriodChanged()
    {
        OnPropertyChanged(nameof(PeriodLabel));
        OnPropertyChanged(nameof(CanGoNextPeriod));
    }

    private bool PeriodIncludesToday()
    {
        var (start, end) = GetPeriodBounds();
        DateTime today = DateTime.Today;
        return today >= start && today <= end;
    }

    private (DateTime Start, DateTime End) GetPeriodBounds() =>
        GetPeriodBoundsFor(_periodAnchor, InsightPeriodKind);

    private static (DateTime Start, DateTime End) GetPeriodBoundsFor(DateTime anchor, InsightPeriod period)
    {
        DateTime today = DateTime.Today;
        if (period == InsightPeriod.Week)
        {
            DateTime start = StartOfWeek(anchor);
            DateTime end = start.AddDays(6);
            if (end > today)
            {
                end = today;
            }

            return (start, end);
        }

        DateTime monthStart = new(anchor.Year, anchor.Month, 1);
        DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1);
        if (monthEnd > today)
        {
            monthEnd = today;
        }

        return (monthStart, monthEnd);
    }

    private static DateTime StartOfWeek(DateTime day)
    {
        int diff = ((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return day.Date.AddDays(-diff);
    }

    private void RefreshDay()
    {
        var snapshot = _tracking.GetDaySnapshot(SelectedDay);
        _timelineSegments = snapshot.Segments;
        _keyPressCount = snapshot.KeyCount.ToString("N0");
        _mouseClickCount = snapshot.MouseClickCount.ToString("N0");

        TimeSpan active = TimeSpan.Zero;
        TimeSpan idle = TimeSpan.Zero;
        double totalDuration = snapshot.AppUsage.Sum(a => a.Duration.TotalSeconds);

        _appUsage.Clear();
        foreach (var item in snapshot.AppUsage)
        {
            if (item.IsIdle)
            {
                idle += item.Duration;
            }
            else
            {
                active += item.Duration;
            }

            _appUsage.Add(AppUsageItemViewModel.From(item, totalDuration));
        }

        // _appUsage is rebuilt from scratch above, so a previously selected row's object
        // reference no longer appears in the list even when that app still has usage today --
        // re-anchor the selection to its refreshed row (by ExePath, the same key
        // AggregateAppUsage groups by) rather than silently keeping a dangling reference the
        // ListBox no longer shows as selected.
        if (_selectedAppUsageItem is { } previouslySelected)
        {
            _selectedAppUsageItem = _appUsage.FirstOrDefault(a =>
                string.Equals(a.ExePath, previouslySelected.ExePath, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(SelectedAppUsageItem));
            OnPropertyChanged(nameof(HasSelectedApp));
            OnPropertyChanged(nameof(ShowSelectionEmptyState));
        }

        _openApps.Clear();
        foreach (var item in snapshot.OpenApps)
        {
            _openApps.Add(OpenAppItemViewModel.From(item));
        }

        SyncSegmentItems(snapshot.Segments);

        _hourlyValues = BuildHourlyActiveMinutes(snapshot.Segments, SelectedDay);
        _peakHourText = BuildPeakHourText(_hourlyValues);
        _activeDurationText = FormatDuration(active);
        _idleDurationText = FormatDuration(idle);
        _screenTimeText = FormatDuration(active + idle);
        UpdateFocusSummary(snapshot.Segments);

        OnPropertyChanged(nameof(TimelineSegments));
        OnPropertyChanged(nameof(KeyPressCount));
        OnPropertyChanged(nameof(MouseClickCount));
        OnPropertyChanged(nameof(ScreenTimeText));
        OnPropertyChanged(nameof(ActiveDurationText));
        OnPropertyChanged(nameof(IdleDurationText));
        OnPropertyChanged(nameof(PeakHourText));
        OnPropertyChanged(nameof(FocusLongestText));
        OnPropertyChanged(nameof(FocusLongestDetailText));
        OnPropertyChanged(nameof(FocusAwayCountText));
        OnPropertyChanged(nameof(FocusSessionCountText));
        OnPropertyChanged(nameof(FocusLastAwayText));
        OnPropertyChanged(nameof(HasAppUsage));
        OnPropertyChanged(nameof(HasOpenApps));
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(HasDayActivity));
        RefreshSelectedDetail();
        RefreshSelectedAppTitles();
        RefreshSearch();
    }

    private void RefreshInsights()
    {
        var (start, end) = GetPeriodBounds();
        var snapshot = _tracking.GetRangeSnapshot(start, end);

        TimeSpan active = TimeSpan.Zero;
        TimeSpan idle = TimeSpan.Zero;
        double totalDuration = snapshot.AppUsage.Sum(a => a.Duration.TotalSeconds);

        _insightAppUsage.Clear();
        foreach (var item in snapshot.AppUsage)
        {
            if (item.IsIdle)
            {
                idle += item.Duration;
            }
            else
            {
                active += item.Duration;
            }

            _insightAppUsage.Add(AppUsageItemViewModel.From(item, totalDuration));
        }

        _insightDailyValues = snapshot.DailyActive.Select(p => p.ActiveMinutes).ToArray();
        _insightDailyLabels = BuildInsightLabels(snapshot.DailyActive, InsightPeriodKind);
        _insightKeyPressCount = snapshot.KeyCount.ToString("N0");
        _insightMouseClickCount = snapshot.MouseClickCount.ToString("N0");
        _insightActiveDurationText = FormatDuration(active);
        _insightIdleDurationText = FormatDuration(idle);
        _insightScreenTimeText = FormatDuration(active + idle);
        _insightPeakDayText = BuildPeakDayText(snapshot.DailyActive);

        int dayCount = Math.Max(1, snapshot.DailyActive.Count);
        _insightAvgActiveText = FormatDurationShort(TimeSpan.FromMinutes(active.TotalMinutes / dayCount));

        OnPropertyChanged(nameof(InsightDailyValues));
        OnPropertyChanged(nameof(InsightDailyLabels));
        OnPropertyChanged(nameof(InsightKeyPressCount));
        OnPropertyChanged(nameof(InsightMouseClickCount));
        OnPropertyChanged(nameof(InsightScreenTimeText));
        OnPropertyChanged(nameof(InsightActiveDurationText));
        OnPropertyChanged(nameof(InsightIdleDurationText));
        OnPropertyChanged(nameof(InsightPeakDayText));
        OnPropertyChanged(nameof(InsightAvgActiveText));
        OnPropertyChanged(nameof(HasInsightAppUsage));
        OnPropertyChanged(nameof(PeriodLabel));
        OnPropertyChanged(nameof(CanGoNextPeriod));
    }

    private static IReadOnlyList<string> BuildInsightLabels(
        IReadOnlyList<DailyActivityPoint> points,
        InsightPeriod period)
    {
        if (points.Count == 0)
        {
            return [];
        }

        if (period == InsightPeriod.Week)
        {
            return points.Select(p => p.LocalDay.ToString("ddd")).ToArray();
        }

        int step = points.Count <= 10 ? 1 : points.Count <= 20 ? 2 : 5;
        var labels = new string[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            labels[i] = i % step == 0 || i == points.Count - 1
                ? points[i].LocalDay.Day.ToString()
                : "";
        }

        return labels;
    }

    private static string BuildPeakDayText(IReadOnlyList<DailyActivityPoint> points)
    {
        if (points.Count == 0 || points.All(p => p.ActiveMinutes <= 0))
        {
            return "No peak yet";
        }

        var peak = points.MaxBy(p => p.ActiveMinutes)!;
        return $"Peak {peak.LocalDay:ddd MMM d} · {FormatDurationShort(TimeSpan.FromMinutes(peak.ActiveMinutes))}";
    }

    // Newest-first. Avoid Clear+rebuild when only the open segment grows, or one new
    // segment was prepended (typical 5s refresh / app-switch paths).
    private void SyncSegmentItems(IReadOnlyList<ActivitySegment> segments)
    {
        var ordered = segments
            .OrderByDescending(s => s.StartUtc)
            .ToList();

        if (!TryPatchSegmentItems(ordered))
        {
            _segmentItems.Clear();
            foreach (var segment in ordered)
            {
                _segmentItems.Add(ActivitySegmentItemViewModel.From(segment));
            }
        }

        if (_selectedSegmentId is long id && SelectedSegmentItem is null)
        {
            SelectedSegmentId = null;
        }
        else if (_selectedSegmentId is not null)
        {
            OnPropertyChanged(nameof(SelectedSegmentItem));
            RefreshSelectedDetail();
        }

        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(HasDayActivity));
    }

    private void UpdateFocusSummary(IReadOnlyList<ActivitySegment> segments)
    {
        int awayCount = 0;
        int sessionCount = 0;
        TimeSpan bestFocus = TimeSpan.Zero;
        DateTime bestStart = default;
        DateTime bestEnd = default;
        string bestApp = "";
        ActivitySegment? lastAway = null;

        TimeSpan runDuration = TimeSpan.Zero;
        DateTime runStart = default;
        DateTime runEnd = default;
        var runApps = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        void FinalizeRun()
        {
            if (runDuration <= TimeSpan.Zero)
            {
                return;
            }

            if (runDuration > bestFocus)
            {
                bestFocus = runDuration;
                bestStart = runStart;
                bestEnd = runEnd;
                bestApp = runApps
                    .OrderByDescending(p => p.Value)
                    .Select(p => p.Key)
                    .FirstOrDefault() ?? "";
            }

            runDuration = TimeSpan.Zero;
            runApps.Clear();
        }

        foreach (var segment in segments.OrderBy(s => s.StartUtc))
        {
            if (segment.IsIdle)
            {
                awayCount++;
                lastAway = segment;
                FinalizeRun();
                continue;
            }

            // Excluded time is not time spent, so it can neither lengthen the longest focus
            // stretch nor count as a session. It does not end the run either: an ignored window
            // in the middle of a work stretch is time the user asked not to be counted, not a
            // break away from the machine (which is what an idle segment above represents).
            if (segment.Excluded)
            {
                continue;
            }

            sessionCount++;
            DateTime start = segment.StartUtc;
            DateTime end = segment.EffectiveEndUtc;
            if (runDuration <= TimeSpan.Zero)
            {
                runStart = start;
            }

            runEnd = end;
            runDuration += end - start;
            string name = string.IsNullOrWhiteSpace(segment.DisplayName) ? segment.ProcessName : segment.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "App";
            }

            runApps[name] = runApps.TryGetValue(name, out TimeSpan existing)
                ? existing + (end - start)
                : end - start;
        }

        FinalizeRun();

        if (bestFocus > TimeSpan.Zero)
        {
            DateTime startLocal = bestStart.ToLocalTime();
            DateTime endLocal = bestEnd.ToLocalTime();
            _focusLongestText = FormatDuration(bestFocus);
            string appPart = string.IsNullOrWhiteSpace(bestApp) ? "" : $" · {bestApp}";
            _focusLongestDetailText = $"{startLocal:HH:mm}–{endLocal:HH:mm}{appPart}";
        }
        else
        {
            _focusLongestText = "—";
            _focusLongestDetailText = "No Active stretch yet";
        }

        _focusAwayCountText = awayCount.ToString("N0");
        _focusSessionCountText = sessionCount.ToString("N0");
        _focusLastAwayText = FormatLastAway(lastAway);
    }

    private static string FormatLastAway(ActivitySegment? away)
    {
        if (away is null)
        {
            return "Last Away: —";
        }

        DateTime startLocal = away.StartUtc.ToLocalTime();
        DateTime endLocal = away.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - startLocal;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return $"Last Away: {startLocal:HH:mm} - {endLocal:HH:mm} ({FormatDurationShort(duration)})";
    }

    private ActivitySegment? FindSelectedSegment()
    {
        if (_selectedSegmentId is not long id)
        {
            return null;
        }

        foreach (var segment in _timelineSegments)
        {
            if (segment.Id == id)
            {
                return segment;
            }
        }

        return null;
    }

    private void RefreshSelectedDetail()
    {
        var segment = FindSelectedSegment();
        if (segment is null)
        {
            _selectedDetailTitle = "";
            _selectedDetailRangeText = "";
            _selectedDetailDurationText = "";
            _selectedDetailKeysText = "0";
            _selectedDetailClicksText = "0";
            _selectedDetailShareText = "";
            _selectedDetailColor = null;
            _selectedDetailIcon = null;
            _selectedDetailIsAway = false;
            NotifySelectedDetailChanged();
            return;
        }

        DateTime startLocal = segment.StartUtc.ToLocalTime();
        DateTime endLocal = segment.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - startLocal;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        // The denominator of "% of tracked" has to agree with the Apps list beside it, so it
        // skips what an ignore rule excluded. The selected block itself can still be an
        // excluded one -- excluded blocks stay on the timeline so the rule stays discoverable
        // -- and a share of a total it is not part of would be meaningless, so that block says
        // so outright instead of quoting a percentage.
        TimeSpan dayTracked = TimeSpan.Zero;
        foreach (var item in _timelineSegments)
        {
            if (item.Excluded)
            {
                continue;
            }

            TimeSpan d = item.EffectiveEndUtc - item.StartUtc;
            if (d > TimeSpan.Zero)
            {
                dayTracked += d;
            }
        }

        double share = dayTracked.TotalSeconds <= 0
            ? 0
            : duration.TotalSeconds / dayTracked.TotalSeconds * 100;

        _selectedDetailIsAway = segment.IsIdle;
        _selectedDetailTitle = segment.IsIdle
            ? "Away"
            : (string.IsNullOrWhiteSpace(segment.DisplayName) ? segment.ProcessName : segment.DisplayName);
        _selectedDetailRangeText = $"{startLocal:HH:mm:ss} – {endLocal:HH:mm:ss}";
        _selectedDetailDurationText = FormatDuration(duration);
        _selectedDetailKeysText = segment.KeyCount.ToString("N0");
        _selectedDetailClicksText = segment.MouseClickCount.ToString("N0");
        _selectedDetailShareText = segment.Excluded
            ? "Excluded from totals"
            : $"{share:0.0}% of tracked";
        _selectedDetailIcon = segment.IsIdle ? null : AppIconLoader.Get(segment.ExePath);
        _selectedDetailColor = segment.IsIdle
            ? new SolidColorBrush(IdlePalette.Fill)
            : AppColor.For(segment.ExePath, segment.ProcessName);
        NotifySelectedDetailChanged();
    }

    private void NotifySelectedDetailChanged()
    {
        OnPropertyChanged(nameof(HasSelectedSegment));
        OnPropertyChanged(nameof(SelectedDetailTitle));
        OnPropertyChanged(nameof(SelectedDetailRangeText));
        OnPropertyChanged(nameof(SelectedDetailDurationText));
        OnPropertyChanged(nameof(SelectedDetailKeysText));
        OnPropertyChanged(nameof(SelectedDetailClicksText));
        OnPropertyChanged(nameof(SelectedDetailShareText));
        OnPropertyChanged(nameof(SelectedDetailColor));
        OnPropertyChanged(nameof(SelectedDetailIcon));
        OnPropertyChanged(nameof(HasSelectedDetailIcon));
        OnPropertyChanged(nameof(SelectedDetailIsAway));
        OnPropertyChanged(nameof(ShowSelectionEmptyState));
    }

    /// <summary>Rebuilds SelectedAppTitles from GetTitleUsage for the currently selected app
    /// row, over the shape TitleUsagePresenter.Build computes -- the total that excludes
    /// excluded rows, and the browser-vs-flat grouping. IsIdle apps ("Away") have no exe path
    /// and no titles of their own, so they clear the panel rather than querying for one.</summary>
    private void RefreshSelectedAppTitles()
    {
        AppUsageItemViewModel? app = _selectedAppUsageItem;
        if (app is null)
        {
            _selectedAppTitles.Clear();
            _selectedAppDisplayName = "";
            _selectedAppTotalText = "";
            _selectedAppDetailUnavailable = false;
            NotifySelectedAppTitlesChanged();
            return;
        }

        // No exe path (an elevated or protected process) or the synthetic "Away" row: there is
        // nothing to query and no privacy setting that would change that, so the panel says as
        // much rather than pointing at the title-capture switch.
        if (TitleUsagePresenter.DetailUnavailable(app.IsIdle, app.ExePath))
        {
            _selectedAppTitles.Clear();
            _selectedAppDisplayName = app.DisplayName;
            _selectedAppTotalText = "";
            _selectedAppDetailUnavailable = true;
            NotifySelectedAppTitlesChanged();
            return;
        }

        DateTime startLocal = SelectedDay.Date;
        DateTime endExclusiveLocal = startLocal.AddDays(1);
        var rows = _tracking.GetTitleUsage(app.ExePath, startLocal, endExclusiveLocal);
        bool isBrowser = BrowserHost.IsBrowser(app.ProcessName);
        TitleUsagePanel panel = TitleUsagePresenter.Build(rows, isBrowser);

        _selectedAppTitles.Clear();
        foreach (var item in panel.Items)
        {
            _selectedAppTitles.Add(item);
        }

        _selectedAppDisplayName = app.DisplayName;
        _selectedAppTotalText = FormatDuration(panel.TotalDuration);
        _selectedAppDetailUnavailable = false;
        NotifySelectedAppTitlesChanged();
    }

    private void NotifySelectedAppTitlesChanged()
    {
        OnPropertyChanged(nameof(HasSelectedAppTitles));
        OnPropertyChanged(nameof(SelectedAppDisplayName));
        OnPropertyChanged(nameof(SelectedAppTotalText));
        OnPropertyChanged(nameof(ShowNoTitlesForSelectedApp));
        OnPropertyChanged(nameof(ShowNoDetailForSelectedApp));
    }

    /// <summary>Rebuilds SearchResults from SearchTitles for SearchQuery over the selected day
    /// (the Day view's "current period"), grouped by app -- pure presentation over a query
    /// Task 11 already covers, so unlike RefreshSelectedAppTitles this has no pulled-out helper
    /// or dedicated tests. Called both from the debounced SearchQuery path and, unconditionally,
    /// from every RefreshDay tick (every 5s while today is selected) -- so it only actually
    /// touches _searchResults when the result set changed, rather than blindly Clear+rebuilding
    /// underneath a popup the user may currently have scrolled or selected a row in.</summary>
    private void RefreshSearch()
    {
        string query = _searchQuery.Trim();
        var next = new List<TitleSearchItemViewModel>();

        if (query.Length > 0)
        {
            DateTime startLocal = SelectedDay.Date;
            DateTime endExclusiveLocal = startLocal.AddDays(1);
            var hits = _tracking.SearchTitles(query, startLocal, endExclusiveLocal);

            var groups = hits
                .GroupBy(s => string.IsNullOrWhiteSpace(s.DisplayName) ? s.ProcessName : s.DisplayName)
                .OrderByDescending(g => g.Max(s => s.StartUtc));

            foreach (var group in groups)
            {
                next.Add(TitleSearchItemViewModel.Header(group.Key));
                foreach (var hit in group.OrderByDescending(s => s.StartUtc))
                {
                    next.Add(TitleSearchItemViewModel.Row(hit));
                }
            }
        }

        if (SearchResultsUnchanged(next))
        {
            return;
        }

        _searchResults.Clear();
        foreach (var item in next)
        {
            _searchResults.Add(item);
        }

        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowSearchEmptyState));
    }

    /// <summary>Identity-only comparison (header app name, or hit SegmentId) against what is
    /// already shown -- not a full field comparison, since a matching segment's already-closed
    /// title/host/excluded fields do not change between two refreshes of the same query.</summary>
    private bool SearchResultsUnchanged(IReadOnlyList<TitleSearchItemViewModel> next)
    {
        if (_searchResults.Count != next.Count)
        {
            return false;
        }

        for (int i = 0; i < next.Count; i++)
        {
            TitleSearchItemViewModel current = _searchResults[i];
            TitleSearchItemViewModel candidate = next[i];
            if (current.IsHeader != candidate.IsHeader
                || current.SegmentId != candidate.SegmentId
                || current.AppDisplayName != candidate.AppDisplayName)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryPatchSegmentItems(IReadOnlyList<ActivitySegment> ordered)
    {
        int existing = _segmentItems.Count;
        if (existing == 0)
        {
            return false;
        }

        if (ordered.Count == existing)
        {
            for (int i = 0; i < existing; i++)
            {
                if (_segmentItems[i].Id != ordered[i].Id)
                {
                    return false;
                }
            }

            for (int i = 0; i < existing; i++)
            {
                _segmentItems[i].UpdateTimes(ordered[i]);
            }

            return true;
        }

        // One new segment opened: [new, previous...,] vs [previous,...]
        if (ordered.Count == existing + 1)
        {
            for (int i = 0; i < existing; i++)
            {
                if (_segmentItems[i].Id != ordered[i + 1].Id)
                {
                    return false;
                }
            }

            _segmentItems.Insert(0, ActivitySegmentItemViewModel.From(ordered[0]));
            for (int i = 1; i < _segmentItems.Count; i++)
            {
                _segmentItems[i].UpdateTimes(ordered[i]);
            }

            return true;
        }

        return false;
    }

    /// <summary>Active minutes per hour for the Day view's chart. Internal rather than private
    /// for the same reason as TrackingService.BuildDailyActiveMinutes: it is pure and static, so
    /// the "excluded time is not counted" rule can be asserted on the real production code even
    /// though MainWindowViewModel itself cannot be constructed in a unit test.</summary>
    internal static double[] BuildHourlyActiveMinutes(IReadOnlyList<ActivitySegment> segments, DateTime localDay)
    {
        var hours = new double[24];
        DateTime dayStartLocal = localDay.Date;
        DateTime dayEndLocal = dayStartLocal.AddDays(1);

        // Same treatment as every other total: SegmentWindows clips to the day, clamps an open
        // segment to now, and drops what an ignore rule excluded.
        foreach (var (_, windowStart, end) in SegmentWindows.Counted(
            segments.Where(s => !s.IsIdle),
            dayStartLocal.ToUniversalTime(),
            dayEndLocal.ToUniversalTime(),
            DateTime.UtcNow))
        {
            DateTime start = windowStart;

            while (start < end)
            {
                DateTime local = start.ToLocalTime();
                int hour = local.Hour;
                DateTime hourEndLocal = local.Date.AddHours(hour + 1);
                DateTime sliceEnd = end < hourEndLocal.ToUniversalTime() ? end : hourEndLocal.ToUniversalTime();
                hours[hour] += (sliceEnd - start).TotalMinutes;
                start = sliceEnd;
            }
        }

        return hours;
    }

    private static string BuildPeakHourText(IReadOnlyList<double> hours)
    {
        if (hours.Count == 0 || hours.All(v => v <= 0))
        {
            return "No peak yet";
        }

        int peak = 0;
        for (int i = 1; i < hours.Count; i++)
        {
            if (hours[i] > hours[peak])
            {
                peak = i;
            }
        }

        string label = peak switch
        {
            0 => "12 am",
            12 => "12 pm",
            > 12 => $"{peak - 12} pm",
            _ => $"{peak} am"
        };

        return $"Peak {label} · {FormatDurationShort(TimeSpan.FromMinutes(hours[peak]))}";
    }

    /// <summary>internal, not private: TitleUsagePresenter.TitleUsageItemViewModel.Row also
    /// formats a duration for the same panel this feeds, and a second copy would let the two
    /// drift apart -- the panel total (formatted here) disagreeing with its own rows (formatted
    /// there).</summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds:D2}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }

    private static string FormatDurationShort(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }

    private static void OpenDataFolder(string databasePath) =>
        OpenLocalDirectory(Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory);

    /// <summary>
    /// Opens a release page, the repository, the issue tracker, the license or the upstream
    /// project. No-ops on a null, blank, or non-http(s) url.
    ///
    /// This argument may come from the network -- OpenUpdateLinkCommand feeds it release.HtmlUrl,
    /// taken from the GitHub release JSON, and ReleaseFeed.Parse only checks that it is
    /// non-blank -- so the check here is deliberately narrow: an absolute http/https URL, and
    /// nothing else. In particular, this must never also accept an existing local directory
    /// (Directory.Exists("\\host\share") can be true for a reachable UNC path regardless of
    /// scheme): a combined gate would let a remote-supplied string reach the filesystem branch
    /// meant only for OpenLocalDirectory's caller. Keep the two gates separate even if that looks
    /// like duplication -- it is the one place doing exactly the check its own caller needs.
    /// </summary>
    private static void OpenWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https"))
        {
            return;
        }

        StartShellProcess(url);
    }

    /// <summary>
    /// Opens a directory that already exists on this machine. The only caller is OpenDataFolder,
    /// whose argument is derived from _tracking.DatabasePath -- a path this process controls, not
    /// one supplied by a remote party -- so this has no scheme check and must never be given a
    /// value from a network response. See OpenWebUrl for why the two gates are not merged.
    /// </summary>
    private static void OpenLocalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        StartShellProcess(path);
    }

    private static void StartShellProcess(string target) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }

    private sealed class RelayCommand<T>(Action<T> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (parameter is T typed)
            {
                execute(typed);
            }
        }
    }
}

internal sealed class ActivitySegmentItemViewModel : INotifyPropertyChanged
{
    private string _endText = "";
    private string _durationText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; init; }
    public required string DisplayName { get; init; }
    public required string StartText { get; init; }

    public string EndText
    {
        get => _endText;
        private set
        {
            if (_endText == value)
            {
                return;
            }

            _endText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndText)));
        }
    }

    public string DurationText
    {
        get => _durationText;
        private set
        {
            if (_durationText == value)
            {
                return;
            }

            _durationText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
        }
    }

    public required IBrush Color { get; init; }
    public Avalonia.Media.Imaging.Bitmap? Icon { get; init; }
    public bool HasIcon => Icon is not null;
    public bool IsIdle { get; init; }

    public static ActivitySegmentItemViewModel From(ActivitySegment segment)
    {
        DateTime startLocal = segment.StartUtc.ToLocalTime();
        DateTime endLocal = segment.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - startLocal;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return new ActivitySegmentItemViewModel
        {
            Id = segment.Id,
            DisplayName = segment.IsIdle ? "Away" : segment.DisplayName,
            StartText = startLocal.ToString("HH:mm:ss"),
            EndText = endLocal.ToString("HH:mm:ss"),
            DurationText = FormatClock(duration),
            IsIdle = segment.IsIdle,
            Icon = segment.IsIdle ? null : AppIconLoader.Get(segment.ExePath),
            Color = segment.IsIdle
                ? new SolidColorBrush(IdlePalette.Fill)
                : AppColor.For(segment.ExePath, segment.ProcessName)
        };
    }

    public void UpdateTimes(ActivitySegment segment)
    {
        DateTime endLocal = segment.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - segment.StartUtc.ToLocalTime();
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        EndText = endLocal.ToString("HH:mm:ss");
        DurationText = FormatClock(duration);
    }

    private static string FormatClock(TimeSpan duration) =>
        $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
}

internal sealed class AppUsageItemViewModel
{
    private const double BarTrackWidth = 84;

    public required string DisplayName { get; init; }
    public required string DurationText { get; init; }
    public required string PercentageText { get; init; }
    public required string KeyText { get; init; }
    public required string ClickText { get; init; }
    public required IBrush Color { get; init; }
    public required double BarWidth { get; init; }
    public Avalonia.Media.Imaging.Bitmap? Icon { get; init; }
    public bool HasIcon => Icon is not null;
    public bool IsIdle { get; init; }

    // Needed to drive the title-breakdown panel once this row is selected: ExePath keys
    // GetTitleUsage (matching AggregateAppUsage's own grouping key) and ProcessName decides,
    // via BrowserHost.IsBrowser, whether that breakdown groups by host or stays flat.
    public required string ExePath { get; init; }
    public required string ProcessName { get; init; }

    public static AppUsageItemViewModel From(AppUsageSummary summary, double totalDurationSeconds)
    {
        double percent = totalDurationSeconds <= 0
            ? 0
            : summary.Duration.TotalSeconds / totalDurationSeconds * 100;
        double barRatio = Math.Clamp(percent / 100.0, 0, 1);
        if (barRatio > 0 && barRatio < 0.03)
        {
            barRatio = 0.03;
        }

        return new AppUsageItemViewModel
        {
            DisplayName = summary.IsIdle ? "Away" : summary.DisplayName,
            DurationText = FormatClock(summary.Duration),
            PercentageText = $"{percent:0.0}%",
            KeyText = summary.KeyCount.ToString("N0"),
            ClickText = summary.MouseClickCount.ToString("N0"),
            IsIdle = summary.IsIdle,
            BarWidth = BarTrackWidth * barRatio,
            Icon = summary.IsIdle ? null : AppIconLoader.Get(summary.ExePath),
            Color = summary.IsIdle
                ? new SolidColorBrush(IdlePalette.Fill)
                : AppColor.For(summary.ExePath, summary.ProcessName),
            ExePath = summary.ExePath,
            ProcessName = summary.ProcessName
        };
    }

    private static string FormatClock(TimeSpan duration) =>
        $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
}

internal sealed class OpenAppItemViewModel
{
    public required string DisplayName { get; init; }
    public required string OpenDurationText { get; init; }
    public required IBrush Color { get; init; }
    public Avalonia.Media.Imaging.Bitmap? Icon { get; init; }
    public bool HasIcon => Icon is not null;

    public static OpenAppItemViewModel From(OpenAppSummary summary) =>
        new()
        {
            DisplayName = summary.DisplayName,
            OpenDurationText = FormatClock(summary.OpenDuration),
            Icon = AppIconLoader.Get(summary.ExePath),
            Color = AppColor.For(summary.ExePath, summary.ProcessName)
        };

    private static string FormatClock(TimeSpan duration) =>
        $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
}

/// <summary>One row of the search-results list: either an app header (<see cref="IsHeader"/>,
/// no segment behind it) or a hit that can be selected to jump the "Selection" panel to that
/// segment. Presentation over SearchTitles, which Task 11's TitleSearchTests already covers --
/// unlike TitleUsagePresenter this has no pulled-out helper of its own.</summary>
internal sealed class TitleSearchItemViewModel
{
    public long? SegmentId { get; init; }
    public required string AppDisplayName { get; init; }
    public string TitleText { get; init; } = "";
    public string TimeText { get; init; } = "";
    public bool IsExcluded { get; init; }
    public bool IsHeader { get; init; }

    internal static TitleSearchItemViewModel Header(string appDisplayName) => new()
    {
        AppDisplayName = appDisplayName,
        IsHeader = true
    };

    internal static TitleSearchItemViewModel Row(ActivitySegment hit)
    {
        string title = string.IsNullOrWhiteSpace(hit.UrlHost)
            ? (hit.WindowTitle ?? "")
            : (string.IsNullOrWhiteSpace(hit.WindowTitle) ? hit.UrlHost : $"{hit.UrlHost} — {hit.WindowTitle}");

        return new TitleSearchItemViewModel
        {
            SegmentId = hit.Id,
            AppDisplayName = string.IsNullOrWhiteSpace(hit.DisplayName) ? hit.ProcessName : hit.DisplayName,
            TitleText = title,
            TimeText = hit.StartUtc.ToLocalTime().ToString("HH:mm"),
            IsExcluded = hit.Excluded
        };
    }
}
