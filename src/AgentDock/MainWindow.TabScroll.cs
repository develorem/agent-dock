using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgentDock.Controls;
using AgentDock.Models;

namespace AgentDock;

/// <summary>
/// Scrolling for the two tab strips.
///
/// Both strips used to lay their tabs out and simply run off the edge of the window: past
/// a certain number of groups, or of projects inside one group, the tabs at the end were
/// unreachable — and so was the "+" button, which sat after the last tab and went over the
/// edge with it. This file adds the missing controls and pins them where they cannot be
/// pushed away:
///
/// <list type="bullet">
///   <item>Each strip gets a <b>pinned cluster at its leading edge</b> — "+" first, then the
///         two scroll arrows. It is docked outside the scrolling area, so it is reachable at
///         any number of tabs, and it does not move around as tabs come and go.</item>
///   <item>The arrows appear only when there is something to scroll to, and disable
///         themselves at each end, so the common case looks exactly as it did.</item>
///   <item><b>The active tab never scrolls out of view.</b> It is the strip's sticky child
///         (see <see cref="TabStripPanel"/>): the other tabs scroll past underneath it while
///         it holds the edge. Scrolling to find a tab therefore never costs you sight of the
///         one you're working in.</item>
/// </list>
///
/// The two strips are deliberately independent: scrolling the groups doesn't touch the
/// project tabs, and vice versa.
/// </summary>
public partial class MainWindow
{
    private RepeatButton? _projectScrollBack;
    private RepeatButton? _projectScrollForward;
    private RepeatButton? _groupScrollBack;
    private RepeatButton? _groupScrollForward;

    /// <summary>Side of the strip within which a drag auto-scrolls, in pixels.</summary>
    private const double DragScrollEdge = 36;

    /// <summary>Minimum gap between two drag auto-scroll steps, so a held drag creeps.</summary>
    private static readonly TimeSpan DragScrollInterval = TimeSpan.FromMilliseconds(140);

    private DateTime _lastDragScroll = DateTime.MinValue;

    /// <summary>
    /// Builds both pinned clusters and starts tracking each strip's scroll state. Called
    /// once from the constructor, after the add-project button exists.
    /// </summary>
    private void InitializeTabScrolling()
    {
        _projectScrollBack = CreateTabScrollButton(
            "Scroll tabs back", () => ToolbarPanel.ScrollByOne(-1));
        _projectScrollForward = CreateTabScrollButton(
            "Scroll tabs forward", () => ToolbarPanel.ScrollByOne(1));

        ProjectNavCluster.Children.Add(_toolbarAddButton);
        ProjectNavCluster.Children.Add(_projectScrollBack);
        ProjectNavCluster.Children.Add(_projectScrollForward);

        _groupScrollBack = CreateTabScrollButton(
            "Scroll groups back", () => MetaTabPanel.ScrollByOne(-1));
        _groupScrollForward = CreateTabScrollButton(
            "Scroll groups forward", () => MetaTabPanel.ScrollByOne(1));

        GroupNavCluster.Children.Add(_groupScrollBack);
        GroupNavCluster.Children.Add(_groupScrollForward);

        ToolbarPanel.MetricsChanged += (_, _) => UpdateTabScrollButtons();
        MetaTabPanel.MetricsChanged += (_, _) => UpdateTabScrollButtons();

        ApplyTabStripOrientation(_currentToolbarPosition);
        UpdateTabScrollButtons();
    }

    /// <summary>
    /// Puts the add-group button at the head of the group cluster, replacing the previous
    /// one. The button is rebuilt (rather than kept and recoloured) every time the group
    /// strip is refreshed, which is also how it picks up a theme change.
    /// </summary>
    private void SetGroupAddButton(Button addButton)
    {
        // Only the add button is a plain Button here — the arrows are RepeatButtons, which
        // don't derive from it — so this never removes one of them by mistake.
        if (GroupNavCluster.Children.Count > 0 && GroupNavCluster.Children[0] is Button)
            GroupNavCluster.Children.RemoveAt(0);

        GroupNavCluster.Children.Insert(0, addButton);
    }

    /// <summary>
    /// One scroll arrow. A <see cref="RepeatButton"/> rather than a Button so holding it
    /// keeps scrolling — with thirty tabs open, one tab per click is a lot of clicking.
    /// Styled to match the "+" beside it: the cluster has to read as one control group.
    /// </summary>
    private RepeatButton CreateTabScrollButton(string toolTip, Action onScroll)
    {
        var template = new ControlTemplate(typeof(RepeatButton));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentPresenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentPresenter);
        template.VisualTree = borderFactory;

        // Fade the arrow that points at an end already reached. Without this the two look
        // identical and the disabled one just swallows clicks.
        template.Triggers.Add(new Trigger
        {
            Property = IsEnabledProperty,
            Value = false,
            Setters = { new Setter(OpacityProperty, 0.35, "Bd") }
        });

        var glyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = _theme.GetBrush("AddButtonForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = new RepeatButton
        {
            Width = 22,
            Height = 26,
            Margin = new Thickness(0, 3, 3, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = _theme.GetBrush("AddButtonBorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Template = template,
            Content = glyph,
            Delay = 400,
            Interval = 90,
            Visibility = Visibility.Collapsed
        };

        button.Click += (_, _) => onScroll();
        button.MouseEnter += (_, _) =>
        {
            if (button.IsEnabled) button.BorderBrush = _theme.GetBrush("TabButtonHoverBorderBrush");
        };
        button.MouseLeave += (_, _) => button.BorderBrush = _theme.GetBrush("AddButtonBorderBrush");

        return button;
    }

    /// <summary>
    /// Points the clusters and their arrows the way the strips run: the project strip turns
    /// vertical when docked to a side, and its cluster has to dock to the top and stack
    /// downwards with it. The group strip is always a horizontal bar across the top.
    /// </summary>
    private void ApplyTabStripOrientation(string toolbarPosition)
    {
        var vertical = toolbarPosition is "Left" or "Right";

        ProjectNavCluster.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DockPanel.SetDock(ProjectNavCluster, vertical ? Dock.Top : Dock.Left);
        ProjectNavCluster.VerticalAlignment = vertical ? VerticalAlignment.Top : VerticalAlignment.Center;
        ProjectNavCluster.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        // Segoe MDL2 Assets, in order: ChevronUp (E70E), ChevronLeft (E76B),
        // ChevronDown (E70D), ChevronRight (E76C). They are private-use glyphs, so an
        // editor without that font shows four blank boxes below — the codepoints are here.
        SetScrollGlyph(_projectScrollBack, vertical ? "" : "");
        SetScrollGlyph(_projectScrollForward, vertical ? "" : "");
        SetScrollGlyph(_groupScrollBack, "");
        SetScrollGlyph(_groupScrollForward, "");
    }

    private static void SetScrollGlyph(RepeatButton? button, string glyph)
    {
        if (button?.Content is TextBlock text) text.Text = glyph;
    }

    /// <summary>
    /// Shows each strip's arrows only while that strip has somewhere to scroll to, and
    /// greys out the one pointing at an end already reached.
    /// </summary>
    private void UpdateTabScrollButtons()
    {
        ApplyScrollState(ToolbarPanel, _projectScrollBack, _projectScrollForward);
        ApplyScrollState(MetaTabPanel, _groupScrollBack, _groupScrollForward);
    }

    private static void ApplyScrollState(TabStripPanel panel, RepeatButton? back, RepeatButton? forward)
    {
        if (back == null || forward == null) return;

        var visibility = panel.IsOverflowing ? Visibility.Visible : Visibility.Collapsed;
        back.Visibility = visibility;
        forward.Visibility = visibility;
        back.IsEnabled = panel.Offset > 0.5;
        forward.IsEnabled = panel.Offset < panel.MaxOffset - 0.5;
    }

    // Which project/group each strip was last scrolled to. Compared by identity rather than
    // by tab element, because a strip rebuild hands back a brand-new button for the same tab.
    private ProjectInfo? _scrolledToProject;
    private string? _scrolledToGroupId;

    /// <summary>
    /// Re-points both strips at their current active tab: that tab becomes the sticky one
    /// (held in view while the rest scroll past) and is scrolled back on screen if a tab
    /// switch arrived from somewhere other than a click on the tab itself.
    ///
    /// Scrolling happens only when the active tab actually <em>changed</em>. Both strips are
    /// rebuilt for all sorts of reasons that leave the active tab alone — a group reorder, a
    /// theme change, a settings edit — and scrolling on those would throw away wherever the
    /// user had scrolled to, right at the moment they were looking at the result.
    /// </summary>
    private void SyncStickyTabs()
    {
        UIElement? projectTab = _activeProject != null
            && _projectTabButtons.TryGetValue(_activeProject, out var tab) ? tab : null;
        ToolbarPanel.StickyChild = projectTab;
        if (!ReferenceEquals(_scrolledToProject, _activeProject))
        {
            _scrolledToProject = _activeProject;
            ToolbarPanel.EnsureVisible(projectTab);
        }

        UIElement? groupTab = null;
        foreach (var child in MetaTabPanel.Children)
        {
            if (child is FrameworkElement { Tag: ProjectGroup group } element
                && group.Id == _activeGroupId)
            {
                groupTab = element;
                break;
            }
        }
        MetaTabPanel.StickyChild = groupTab;
        if (_scrolledToGroupId != _activeGroupId)
        {
            _scrolledToGroupId = _activeGroupId;
            MetaTabPanel.EnsureVisible(groupTab);
        }
    }

    /// <summary>
    /// Nudges a strip along while a tab is dragged against its edge — without it, a tab can
    /// only be dropped somewhere already on screen, which with a scrolling strip is a trap.
    /// Rate-limited, because DragOver fires on every mouse move.
    /// </summary>
    private void AutoScrollDuringDrag(TabStripPanel panel, DragEventArgs e)
    {
        if (!panel.IsOverflowing) return;

        var position = e.GetPosition(panel);
        var horizontal = panel.Orientation == Orientation.Horizontal;
        var coordinate = horizontal ? position.X : position.Y;
        var length = horizontal ? panel.ActualWidth : panel.ActualHeight;

        var direction = coordinate < DragScrollEdge ? -1
            : coordinate > length - DragScrollEdge ? 1
            : 0;
        if (direction == 0) return;

        var now = DateTime.UtcNow;
        if (now - _lastDragScroll < DragScrollInterval) return;
        _lastDragScroll = now;

        panel.ScrollByOne(direction);
    }

    /// <summary>Recolours the scroll arrows after a theme change.</summary>
    private void RefreshTabScrollTheme()
    {
        foreach (var button in new[] { _projectScrollBack, _projectScrollForward, _groupScrollBack, _groupScrollForward })
        {
            if (button == null) continue;
            button.BorderBrush = _theme.GetBrush("AddButtonBorderBrush");
            if (button.Content is TextBlock glyph)
                glyph.Foreground = _theme.GetBrush("AddButtonForeground");
        }
    }
}
