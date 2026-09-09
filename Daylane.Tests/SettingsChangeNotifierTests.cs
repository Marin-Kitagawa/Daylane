using Daylane.ViewModels;

namespace Daylane.Tests;

/// <summary>
/// Covers the property list MainWindowViewModel re-raises whenever SettingsService.Changed
/// fires -- the part of Task 13's tray/Settings reconciliation fix that is testable without a
/// live TrackingService (which owns real OS hooks and timers, and whose parameterless
/// constructor would touch a real database file) or a pumped Avalonia dispatcher loop (this
/// project's other tests already avoid initializing an Avalonia platform in-process; a scratch
/// probe confirmed Dispatcher.UIThread.Post never runs its callback here because nothing pumps
/// the dispatcher's queue).
/// </summary>
public class SettingsChangeNotifierTests
{
    [Fact]
    public void Raise_NotifiesEverySettingBackedProperty()
    {
        var raised = new List<string>();

        SettingsChangeNotifier.Raise(raised.Add);

        Assert.Equal(
            [
                "Appearance",
                "TrackingEnabled",
                "IdleThresholdMinutes",
                "RetentionDays",
                "AutoStart",
                "ShowWindowOnAutoStart",
                "MinimizeToTray",
                "RecordWindowTitles",
                "RecordBrowserHost",
                "CanRecordBrowserHost"
            ],
            raised);
    }
}
