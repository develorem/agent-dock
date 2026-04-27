using System.Text;
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

    // Leading "# vX.Y.Z" heading + trailing blank line (for release-notes files where
    // the version is already shown by the surrounding chrome / GitHub release page).
    [GeneratedRegex(@"\A#\s+v?\d+(\.\d+)+[^\r\n]*\r?\n(\r?\n)?")]
    private static partial Regex LeadingVersionHeadingRegex();

    // **...** containing at least one inline atom (code span or link). MdXaml parses
    // those atoms before bold, which leaves the ** delimiters orphaned around the
    // atom and renders them as literal text. We split the bold around each atom.
    [GeneratedRegex(@"\*\*([^*\n]*?(?:`[^`\n]+`|\[[^\]\n]+\]\([^)\n]+\))[^*\n]*?)\*\*")]
    private static partial Regex BoldContainingInlineRegex();

    [GeneratedRegex(@"`[^`\n]+`|\[[^\]\n]+\]\([^)\n]+\)")]
    private static partial Regex InlineAtomRegex();

    [GeneratedRegex(@"^(\s*)(.*?)(\s*)$", RegexOptions.Singleline)]
    private static partial Regex TrimWsRegex();

    /// <summary>
    /// Pre-processes markdown to fix patterns that MdXaml cannot parse.
    /// MdXaml parses links before bold/italic, so <c>**[text](url)**</c> leaves
    /// the asterisks as literal characters. Rewrite to <c>[**text**](url)</c>.
    /// </summary>
    public static string PreProcess(string markdown)
    {
        markdown = BoldLinkRegex().Replace(markdown, "[**$1**]($2)");
        markdown = ItalicLinkRegex().Replace(markdown, "[*$1*]($2)");
        markdown = FixBoldAcrossInlineAtoms(markdown);
        return markdown;
    }

    /// <summary>
    /// Rewrites <c>**text `code` text**</c> as <c>**text** `code` **text**</c> so that
    /// MdXaml can pair the bold delimiters. Also handles partial-link cases like
    /// <c>**See [foo](url) more**</c>. Trims whitespace at split boundaries to satisfy
    /// CommonMark flanking rules.
    /// </summary>
    private static string FixBoldAcrossInlineAtoms(string markdown)
    {
        return BoldContainingInlineRegex().Replace(markdown, m =>
        {
            var inner = m.Groups[1].Value;
            var sb = new StringBuilder();
            var lastEnd = 0;
            foreach (Match atom in InlineAtomRegex().Matches(inner))
            {
                EmitBoldTextSegment(inner.Substring(lastEnd, atom.Index - lastEnd), sb);
                sb.Append(atom.Value);
                lastEnd = atom.Index + atom.Length;
            }
            EmitBoldTextSegment(inner.Substring(lastEnd), sb);
            return sb.ToString();
        });
    }

    private static void EmitBoldTextSegment(string segment, StringBuilder sb)
    {
        if (segment.Length == 0) return;
        var m = TrimWsRegex().Match(segment);
        var lead = m.Groups[1].Value;
        var core = m.Groups[2].Value;
        var trail = m.Groups[3].Value;
        if (lead.Length > 0) sb.Append(lead);
        if (core.Length > 0) sb.Append("**").Append(core).Append("**");
        if (trail.Length > 0) sb.Append(trail);
    }

    /// <summary>
    /// Strips a leading <c># vX.Y.Z</c> heading (and the blank line after it) from
    /// release-notes markdown, since dialog chrome / GitHub release pages already
    /// display the version separately.
    /// </summary>
    public static string StripLeadingVersionHeading(string markdown)
        => LeadingVersionHeadingRegex().Replace(markdown, "", 1);

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
