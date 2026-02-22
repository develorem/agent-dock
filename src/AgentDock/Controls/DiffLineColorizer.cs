using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace AgentDock.Controls;

/// <summary>
/// Colors diff lines by prefix: green background for additions (+),
/// red background for deletions (-), purple for hunk headers (@@).
/// Uses theme-aware brushes from Application.Resources.
/// </summary>
public class DiffLineColorizer : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0)
            return;

        var text = CurrentContext.Document.GetText(line);

        Brush? foreground = null;
        Brush? background = null;

        if (text.StartsWith("@@") && text.Contains("@@", StringComparison.Ordinal))
        {
            foreground = GetBrush("DiffHunkHeaderForeground");
            background = GetBrush("DiffHunkHeaderBackground");
        }
        else if (text.StartsWith('+'))
        {
            foreground = GetBrush("DiffAddedForeground");
            background = GetBrush("DiffAddedBackground");
        }
        else if (text.StartsWith('-'))
        {
            foreground = GetBrush("DiffRemovedForeground");
            background = GetBrush("DiffRemovedBackground");
        }

        if (foreground == null && background == null)
            return;

        ChangeLinePart(line.Offset, line.EndOffset, element =>
        {
            if (foreground != null)
                element.TextRunProperties.SetForegroundBrush(foreground);
            if (background != null)
                element.TextRunProperties.SetBackgroundBrush(background);
        });
    }

    private static Brush? GetBrush(string resourceKey)
    {
        return Application.Current.TryFindResource(resourceKey) as Brush;
    }
}
