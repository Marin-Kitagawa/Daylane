using Avalonia;
using Avalonia.Controls;

namespace Daylane.Controls;

/// <summary>
/// Base for the custom-drawn timeline controls. Caches a TimelinePalette resolved against
/// this control's resource chain -- which only reaches Application's theme dictionaries once
/// the control is attached -- and rebuilds it both on attach and on every later theme variant
/// change, so each drawing control doesn't repeat the same cache-and-resubscribe boilerplate.
/// </summary>
internal abstract class ThemedControl : Control
{
    private TimelinePalette? _palette;

    protected TimelinePalette Palette => _palette ??= TimelinePalette.Resolve(this, ActualThemeVariant);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeVariantChanged;

        // Resource lookup walks the resource parent chain up to Application, which a detached
        // control does not have, so a Palette read before attach caches an all-magenta fallback.
        // ActualThemeVariantChanged only fires on a later *variant change*, never on the attach
        // itself, so that stale miss would otherwise persist for the control's whole life.
        // Dropping the cache here re-resolves once, on attach -- not on every render.
        _palette = null;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnThemeVariantChanged;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        _palette = null;
        InvalidateVisual();
    }
}
