using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentDock.Controls;

/// <summary>
/// The panel behind the group strip and the project tab strip: a <see cref="StackPanel"/>
/// that scrolls its tabs past the strip's edge instead of letting them run off it, while
/// keeping one child — the active tab — permanently in view.
///
/// WHY NOT A ScrollViewer: the requirement isn't "scroll the tabs", it's "scroll the tabs
/// <em>without ever hiding the one you're looking at</em>". A ScrollViewer moves its whole
/// content as one block, so the active tab leaves the screen like everything else. Here the
/// active tab is sticky: it sits in its natural position while that position is on screen,
/// and pins itself to whichever edge it would otherwise have slid past, so the rest of the
/// strip scrolls underneath it. That is also why the panel owns the scroll offset itself —
/// a ScrollViewer wrapper would fight the drag-and-drop reordering and the per-tab hit
/// testing that both strips already do against child coordinates.
///
/// It derives from StackPanel so that everything already written against these two strips —
/// <c>Children</c>, <c>Orientation</c> (the project strip turns vertical when docked to a
/// side), <c>Margin</c>, drag-and-drop — keeps working untouched.
/// </summary>
public sealed class TabStripPanel : StackPanel
{
    private double _offset;
    private double _extent;
    private double _viewport;
    private UIElement? _sticky;

    // Last values handed to MetricsChanged, so an arrange pass that moved nothing
    // doesn't wake the arrow buttons up for no reason.
    private double _reportedExtent = -1;
    private double _reportedViewport = -1;
    private double _reportedOffset = -1;
    private bool _metricsPosted;

    public TabStripPanel()
    {
        // Tabs scrolled past the edge must be cut off at it, not painted over the
        // neighbouring chrome.
        ClipToBounds = true;

        // Hit-testable across its whole area without painting anything: the strip's own
        // background and accent rail show through. This is what makes the mouse wheel and
        // the drag-and-drop reordering work in the empty space beyond the last tab.
        Background = Brushes.Transparent;
    }

    /// <summary>
    /// Raised after layout when the scroll position, the total tab length, or the visible
    /// length changed — i.e. when the navigation arrows may need to appear, disappear, or
    /// change their enabled state. Raised asynchronously, because handlers typically change
    /// element visibility and must not run inside the arrange pass that detected the change.
    /// </summary>
    public event EventHandler? MetricsChanged;

    /// <summary>
    /// How far the strip is scrolled, in pixels from its leading edge. Clamped to the
    /// scrollable range; setting it past the end lands on the end.
    /// </summary>
    public double Offset
    {
        get => _offset;
        set
        {
            var clamped = Math.Max(0, Math.Min(value, MaxOffset));
            if (Math.Abs(clamped - _offset) < 0.01) return;
            _offset = clamped;
            InvalidateArrange();
        }
    }

    /// <summary>
    /// The child kept in view at all times — the active tab. It is laid out in its natural
    /// position while that is on screen, and sticks to the strip's leading or trailing edge
    /// rather than scrolling off either one.
    /// </summary>
    public UIElement? StickyChild
    {
        get => _sticky;
        set
        {
            if (ReferenceEquals(_sticky, value)) return;
            // Drawn over the tabs sliding beneath it, not under them.
            if (_sticky != null) SetZIndex(_sticky, 0);
            _sticky = value;
            if (value != null) SetZIndex(value, 1);
            InvalidateArrange();
        }
    }

    /// <summary>Total length of every visible tab, laid end to end.</summary>
    public double ExtentLength => _extent;

    /// <summary>Length of the strip actually on screen.</summary>
    public double ViewportLength => _viewport;

    /// <summary>The largest meaningful <see cref="Offset"/>; zero when everything fits.</summary>
    public double MaxOffset => Math.Max(0, _extent - _viewport);

    /// <summary>True when there are more tabs than fit, so the arrows have work to do.</summary>
    public bool IsOverflowing => MaxOffset > 0.5;

    private bool IsHorizontal => Orientation == Orientation.Horizontal;

    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = base.MeasureOverride(availableSize);

        // Report that we fit, whatever the children add up to. Without this the layout
        // system hands ArrangeOverride the full overflowing extent as its "final size"
        // and the panel has no way to tell where its own visible edge is — which is the
        // one number the whole scrolling model is built on. The real extent is recovered
        // from the children in ArrangeOverride.
        if (IsHorizontal)
        {
            if (!double.IsInfinity(availableSize.Width))
                desired.Width = Math.Min(desired.Width, availableSize.Width);
        }
        else
        {
            if (!double.IsInfinity(availableSize.Height))
                desired.Height = Math.Min(desired.Height, availableSize.Height);
        }

        return desired;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var horizontal = IsHorizontal;
        _viewport = horizontal ? finalSize.Width : finalSize.Height;

        _extent = 0;
        foreach (UIElement child in InternalChildren)
            _extent += Length(child);

        // Closing tabs can leave the strip scrolled past its own end. Correct it in place
        // rather than through the property, which would invalidate the arrange we're in.
        _offset = Math.Max(0, Math.Min(_offset, MaxOffset));

        var position = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            var length = Length(child);
            var start = position - _offset;

            if (ReferenceEquals(child, _sticky))
                start = Math.Max(0, Math.Min(start, Math.Max(0, _viewport - length)));

            child.Arrange(horizontal
                ? new Rect(start, 0, length, finalSize.Height)
                : new Rect(0, start, finalSize.Width, length));

            position += length;
        }

        if (_extent != _reportedExtent || _viewport != _reportedViewport || _offset != _reportedOffset)
            PostMetricsChanged();

        return finalSize;
    }

    /// <summary>
    /// Scrolls by one whole tab — <paramref name="direction"/> is -1 towards the start,
    /// +1 towards the end. Whole tabs rather than a fixed pixel step, so a click never
    /// leaves a sliver of a tab half-cut at the edge.
    /// </summary>
    public void ScrollByOne(int direction)
    {
        double position = 0;
        double? target = null;

        foreach (UIElement child in InternalChildren)
        {
            var length = Length(child);
            if (length > 0)
            {
                if (direction > 0 && position > _offset + 0.5)
                {
                    target = position;
                    break;
                }
                // Walk the whole strip for the backwards case: the answer is the *last*
                // tab start still behind the current offset.
                if (direction < 0 && position < _offset - 0.5)
                    target = position;
            }
            position += length;
        }

        Offset = target ?? (direction > 0 ? MaxOffset : 0);
    }

    /// <summary>
    /// Scrolls <paramref name="child"/> fully into view, after layout has settled. Called
    /// when the active tab changes by some route other than clicking it — a keyboard
    /// shortcut, a menu, a session demanding attention — where the tab being switched to
    /// may currently be off screen.
    /// </summary>
    public void EnsureVisible(UIElement? child)
    {
        if (child == null) return;
        Dispatcher.BeginInvoke(new Action(() => ScrollIntoViewCore(child)), DispatcherPriority.Loaded);
    }

    private void ScrollIntoViewCore(UIElement child)
    {
        double position = 0;
        foreach (UIElement candidate in InternalChildren)
        {
            var length = Length(candidate);
            if (ReferenceEquals(candidate, child))
            {
                if (length <= 0) return; // collapsed — nothing to show
                if (position < _offset)
                    Offset = position;
                else if (position + length > _offset + _viewport)
                    Offset = position + length - _viewport;
                return;
            }
            position += length;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsOverflowing) return;
        ScrollByOne(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private double Length(UIElement child)
        => IsHorizontal ? child.DesiredSize.Width : child.DesiredSize.Height;

    private void PostMetricsChanged()
    {
        _reportedExtent = _extent;
        _reportedViewport = _viewport;
        _reportedOffset = _offset;

        if (_metricsPosted) return;
        _metricsPosted = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _metricsPosted = false;
            MetricsChanged?.Invoke(this, EventArgs.Empty);
        }), DispatcherPriority.Loaded);
    }
}
