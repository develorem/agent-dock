using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace AgentDock.Controls;

/// <summary>
/// Colors URLs and markdown link syntax with a theme-aware foreground brush.
/// Handles: [text](url), <url>, and bare https://... URLs.
/// </summary>
public partial class MarkdownLinkColorizer : DocumentColorizingTransformer
{
    [GeneratedRegex(@"https?://[^\s\)>\]]+|\[([^\]]*)\]\(([^\)]+)\)|<(https?://[^>]+)>")]
    private static partial Regex LinkPattern();

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0)
            return;

        var text = CurrentContext.Document.GetText(line);
        var linkBrush = GetBrush("PreviewLinkForeground");
        if (linkBrush == null)
            return;

        foreach (Match match in LinkPattern().Matches(text))
        {
            var start = line.Offset + match.Index;
            var end = start + match.Length;

            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetForegroundBrush(linkBrush);
            });
        }
    }

    private static Brush? GetBrush(string resourceKey)
    {
        return Application.Current.TryFindResource(resourceKey) as Brush;
    }
}
