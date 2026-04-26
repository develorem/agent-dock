using System.Text.RegularExpressions;
using System.Windows.Documents;
using ICSharpCode.AvalonEdit;
using MdXaml;

namespace AgentDock.Services;

/// <summary>
/// Shared markdown helpers used by both AI chat and file preview.
/// </summary>
public static partial class MarkdownHelper
{
    // **[text](url)** → [**text**](url)
    [GeneratedRegex(@"\*\*\[([^\]]+)\]\(([^)]+)\)\*\*")]
    private static partial Regex BoldLinkRegex();

    // *[text](url)* → [*text*](url)  — negative lookbehind avoids matching **
    [GeneratedRegex(@"(?<!\*)\*\[([^\]]+)\]\(([^)]+)\)\*(?!\*)")]
    private static partial Regex ItalicLinkRegex();

    /// <summary>
    /// Pre-processes markdown to fix patterns that MdXaml cannot parse.
    /// MdXaml parses links before bold/italic, so <c>**[text](url)**</c> leaves
    /// the asterisks as literal characters. Rewrite to <c>[**text**](url)</c>.
    /// </summary>
    public static string PreProcess(string markdown)
    {
        markdown = BoldLinkRegex().Replace(markdown, "[**$1**]($2)");
        markdown = ItalicLinkRegex().Replace(markdown, "[*$1*]($2)");
        return markdown;
    }

    /// <summary>
    /// Applies theme colors to fenced code blocks in a rendered MdXaml FlowDocument.
    /// MdXaml renders these as BlockUIContainer containing an AvalonEdit TextEditor
    /// with default (white) colors.
    /// </summary>
    public static void ApplyCodeBlockTheme(FlowDocument? doc)
    {
        if (doc == null) return;

        var codeBg = ThemeManager.GetBrush("MarkdownCodeBackground");
        var codeFg = ThemeManager.GetBrush("PreviewForeground");

        foreach (var block in doc.Blocks)
            ApplyCodeBlockStyle(block, codeBg, codeFg);
    }

    /// <summary>
    /// Convenience method: pre-processes markdown, sets it on the viewer,
    /// then applies code block theming.
    /// </summary>
    public static void RenderTo(MarkdownScrollViewer viewer, string markdown)
    {
        viewer.Markdown = PreProcess(markdown);
        ApplyCodeBlockTheme(viewer.Document);
    }

    private static void ApplyCodeBlockStyle(
        Block block, System.Windows.Media.Brush bg, System.Windows.Media.Brush fg)
    {
        if (block is BlockUIContainer container && container.Child is TextEditor editor)
        {
            editor.Background = bg;
            editor.Foreground = fg;
            return;
        }

        if (block is Section section)
        {
            foreach (var child in section.Blocks)
                ApplyCodeBlockStyle(child, bg, fg);
        }
        else if (block is List list)
        {
            foreach (var item in list.ListItems)
                foreach (var child in item.Blocks)
                    ApplyCodeBlockStyle(child, bg, fg);
        }
    }
}
