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
        // Two distinct reasons to store nothing: the user never opted into titles at all,
        // or this particular window matched a privacy keyword. Same outcome either way.
        // RecordBrowserHost is deliberately not gated here: that check lives at the Task 8
        // call site, which is what fills in UrlHost to begin with. Apply only ever strips it.
        ForegroundApp stored =
            !settings.RecordWindowTitles
            || PrivacyKeywords.Suppresses(app.WindowTitle, app.UrlHost, settings.PrivacyKeywords)
                ? app with { WindowTitle = null, UrlHost = null }
                : app;

        // Evaluated against the title that will be STORED, not the one just read from the
        // window. The recompute can only ever see the stored title, so judging live capture
        // by a richer title would make the two disagree: a row excluded now by a title
        // keyword would silently un-exclude itself on the next unrelated rule edit.
        // Consequence, and it is the intended one: title-keyword rules require title capture
        // to be on. Whole-process rules (TitleKeyword = null) work regardless.
        excluded = IgnoreRules.IsExcluded(stored.ProcessName, stored.WindowTitle, settings.IgnoreRules);

        return stored;
    }
}
