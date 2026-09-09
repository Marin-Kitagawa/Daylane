namespace Daylane.ViewModels;

/// <summary>
/// The list of MainWindowViewModel properties that must be re-notified whenever
/// SettingsService.Changed fires -- i.e. whenever the settings store changes from any writer
/// (the Settings tab itself, the tray's startup checkbox, or a future one), not only from this
/// view model's own property setters. Split out as a plain list plus a callback so the exact
/// set of re-raised properties is unit-testable without needing a live TrackingService (which
/// owns real OS hooks and timers) or a running Avalonia dispatcher loop -- MainWindowViewModel
/// still does the real subscribing and marshals onto the UI thread itself.
/// </summary>
internal static class SettingsChangeNotifier
{
    internal static readonly IReadOnlyList<string> Properties =
    [
        nameof(MainWindowViewModel.Appearance),
        nameof(MainWindowViewModel.TrackingEnabled),
        nameof(MainWindowViewModel.IdleThresholdMinutes),
        nameof(MainWindowViewModel.RetentionDays),
        nameof(MainWindowViewModel.AutoStart),
        nameof(MainWindowViewModel.ShowWindowOnAutoStart),
        nameof(MainWindowViewModel.MinimizeToTray),
        nameof(MainWindowViewModel.RecordWindowTitles),
        nameof(MainWindowViewModel.RecordBrowserHost),
        nameof(MainWindowViewModel.CanRecordBrowserHost)
    ];

    internal static void Raise(Action<string> onPropertyChanged)
    {
        foreach (string property in Properties)
        {
            onPropertyChanged(property);
        }
    }
}
