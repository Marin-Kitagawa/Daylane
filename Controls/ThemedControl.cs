using Avalonia;
using Avalonia.Controls;

namespace Daylane.Controls;

/// <summary>
/// Base for the custom-drawn timeline controls. Caches a TimelinePalette resolved
/// against this control's own theme resources and rebuilds it whenever the active
/// theme variant changes, so each drawing control doesn't repeat the same
/// cache-and-resubscribe boilerplate.
/// </summary>
internal abstract class ThemedControl : Control
{
    private TimelinePalette? _palette;

    protected TimelinePalette Palette => _palette ??= TimelinePalette.Resolve(this, ActualThemeVariant);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeVariantChanged;
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
