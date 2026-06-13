using System.Windows;
using System.Windows.Controls;

namespace AgentDock.Controls;

/// <summary>
/// Attached behavior that keeps a <see cref="ScrollViewer"/> pinned to its bottom as
/// content grows — used by the activity bubble's inner scroller so streaming thinking /
/// tool output follows the bottom, without yanking the user back if they've scrolled up
/// to read. Mirrors the chat's outer sticky-bottom logic (see AiChatControl.OnScrollChanged):
/// all decisions come from the <see cref="ScrollChangedEventArgs"/> so the streaming hot
/// path never forces a layout pass by reading live offsets.
/// </summary>
public static class AutoScroll
{
    private const double BottomThreshold = 16;

    public static readonly DependencyProperty ToEndProperty =
        DependencyProperty.RegisterAttached(
            "ToEnd", typeof(bool), typeof(AutoScroll),
            new PropertyMetadata(false, OnToEndChanged));

    public static bool GetToEnd(DependencyObject o) => (bool)o.GetValue(ToEndProperty);
    public static void SetToEnd(DependencyObject o, bool value) => o.SetValue(ToEndProperty, value);

    // Per-scrollviewer follow flag. True while the bottom is (near) in view.
    private static readonly DependencyProperty StickProperty =
        DependencyProperty.RegisterAttached(
            "Stick", typeof(bool), typeof(AutoScroll), new PropertyMetadata(true));

    private static void OnToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
            sv.ScrollChanged += OnScrollChanged;
        else
            sv.ScrollChanged -= OnScrollChanged;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        // Only react to this scrollviewer's own events, not bubbled ones from any
        // nested scroller.
        if (!ReferenceEquals(e.OriginalSource, sv)) return;

        if (e.ExtentHeightChange != 0)
        {
            // Content grew/shrank — re-pin to the new bottom only while following.
            if ((bool)sv.GetValue(StickProperty)) sv.ScrollToEnd();
        }
        else if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
        {
            // The user scrolled (or the viewport resized) — follow only while the
            // bottom is in view.
            var atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - BottomThreshold;
            sv.SetValue(StickProperty, atBottom);
        }
    }
}
