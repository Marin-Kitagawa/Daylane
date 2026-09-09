using Daylane.Models;

namespace Daylane.Services;

/// <summary>Decides what detail a segment may carry, and whether its time counts at all.
///
/// Pure and static on purpose: live capture (<see cref="TrackingService"/>) and the
/// rule-change recompute share one verdict, and tests exercise the real decision rather
/// than a re-implementation of it.</summary>
internal static class CapturePolicy
{
    internal static ForegroundApp Apply(ForegroundApp app, DaylaneSettings settings, out bool excluded)
    {
        // Both lists are nullable on DaylaneSettings (a settings blob written by an older
        // build has no such key) and both callees dereference .Count. Live capture always
        // hands over a Normalize()d instance so this cannot fire today, but this method is by
        // design the single shared decision point: the next caller to pass a raw
        // `new DaylaneSettings()` would otherwise get a NullReferenceException in the capture
        // path. These two coalesces are also what the build's only two warnings were about.
        IReadOnlyList<string> keywords = settings.PrivacyKeywords ?? Array.Empty<string>();
        IReadOnlyList<IgnoreRule> rules = settings.IgnoreRules ?? Array.Empty<IgnoreRule>();

        // Two distinct reasons to store nothing: the user never opted into titles at all,
        // or this particular window matched a privacy keyword. Same outcome either way.
        ForegroundApp stored =
            !settings.RecordWindowTitles
            || PrivacyKeywords.Suppresses(app.WindowTitle, app.UrlHost, keywords)
                ? app with { WindowTitle = null, UrlHost = null }
                : app;

        // RecordBrowserHost off alone (titles still on) must still strip the host: Normalize
        // keeps the two in sync for settings already at rest, but a browser walk in flight when
        // the user flips this off would otherwise finish with the host it already read.
        if (!settings.RecordBrowserHost)
        {
            stored = stored with { UrlHost = null };
        }

        // Evaluated against the title that will be STORED, not the one just read from the
        // window. The recompute can only ever see the stored title, so judging live capture
        // by a richer title would make the two disagree: a row excluded now by a title
        // keyword would silently un-exclude itself on the next unrelated rule edit.
        // Consequence, and it is the intended one: title-keyword rules require title capture
        // to be on. Whole-process rules (TitleKeyword = null) work regardless. That cost was
        // accepted on the grounds that it would be visible rather than silent, so the Settings
        // panel now disables the keyword box and says so while titles are off -- see
        // MainWindowViewModel.CanIgnoreByTitleKeyword.
        excluded = IgnoreRules.IsExcluded(stored.ProcessName, stored.WindowTitle, rules);

        return stored;
    }
}
