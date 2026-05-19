using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Input;
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

    // Path candidate: optional ./ or .\ prefix, optional drive letter, one or more
    // separator-terminated segments, then a final filename with extension, optional :line ref.
    [GeneratedRegex(@"\G(?:\.[\\/])?(?:[A-Za-z]:[\\/])?(?:[\w.\-]+[\\/])+[\w\-.]+\.[A-Za-z][\w]*(?::\d+)?")]
    private static partial Regex PathCandidateRegex();

    public const string FileLinkScheme = "agentdock-file";
    private const string FileLinkPrefix = "agentdock-file:///";

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

    /// <summary>
    /// Builds a standalone <see cref="FlowDocument"/> from markdown source.
    /// Used by the chat to cache a per-message rendered document at finalize
    /// time, so scrolling past and back doesn't re-parse markdown.
    ///
    /// Internally instantiates a throwaway <see cref="MarkdownScrollViewer"/>
    /// to drive MdXaml's parser, then detaches the document so it can be
    /// attached to a real viewer later. The temp viewer is GC-eligible on return.
    /// </summary>
    /// <param name="markdown">Raw markdown source — preprocessing is handled internally.</param>
    /// <param name="markdownStyle">Optional <see cref="FlowDocument"/> style (foreground, font, etc.).
    /// Pass the same style the live viewer uses so the cached document renders identically.</param>
    /// <param name="projectPath">Project root for path-link resolution. Pass empty to skip.</param>
    /// <param name="onFileLinkClicked">Click handler for resolved file references in the rendered document.</param>
    public static FlowDocument BuildDocument(
        string markdown,
        System.Windows.Style? markdownStyle,
        string projectPath,
        Action<string>? onFileLinkClicked)
    {
        var linkified = string.IsNullOrEmpty(projectPath) ? markdown : LinkifyPaths(markdown, projectPath);
        var preProcessed = PreProcess(linkified);

        var tmp = new MarkdownScrollViewer { MarkdownStyle = markdownStyle };
        tmp.Markdown = preProcessed;
        var doc = tmp.Document ?? new FlowDocument();

        ApplyCodeBlockTheme(doc);
        if (onFileLinkClicked != null)
            WireFileLinks(doc, onFileLinkClicked);

        // Detach from the temp viewer so the document can be re-parented to a
        // real viewer when the chat container materializes.
        tmp.Document = null;

        return doc;
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

    /// <summary>
    /// Rewrites file-path references in <paramref name="markdown"/> as markdown links
    /// using the <c>agentdock-file://</c> scheme. Only paths containing a directory
    /// separator that resolve to an existing file (under <paramref name="projectRoot"/>
    /// or absolute) are linked. Skips fenced code blocks and existing markdown links.
    /// </summary>
    public static string LinkifyPaths(string markdown, string projectRoot)
    {
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrEmpty(projectRoot))
            return markdown;

        var sb = new StringBuilder(markdown.Length + 64);
        var inFence = false;
        var i = 0;

        while (i < markdown.Length)
        {
            // Fenced code block delimiter at line start — toggle and skip the line.
            if ((i == 0 || markdown[i - 1] == '\n') && IsFenceAt(markdown, i))
            {
                var nl = markdown.IndexOf('\n', i);
                if (nl < 0) { sb.Append(markdown, i, markdown.Length - i); break; }
                sb.Append(markdown, i, nl - i + 1);
                i = nl + 1;
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                var nl = markdown.IndexOf('\n', i);
                if (nl < 0) { sb.Append(markdown, i, markdown.Length - i); break; }
                sb.Append(markdown, i, nl - i + 1);
                i = nl + 1;
                continue;
            }

            // Skip past existing markdown links [text](url) so we don't double-wrap.
            if (markdown[i] == '[')
            {
                var endBracket = markdown.IndexOf(']', i + 1);
                if (endBracket > i && endBracket + 1 < markdown.Length && markdown[endBracket + 1] == '(')
                {
                    var endParen = markdown.IndexOf(')', endBracket + 2);
                    if (endParen > endBracket)
                    {
                        sb.Append(markdown, i, endParen - i + 1);
                        i = endParen + 1;
                        continue;
                    }
                }
            }

            // Inline backtick span — wrap the entire span as a link if the inner text
            // resolves to a file. Inline code spans don't span lines per CommonMark.
            if (markdown[i] == '`')
            {
                var nlIdx = markdown.IndexOf('\n', i + 1);
                var endTick = markdown.IndexOf('`', i + 1);
                if (endTick > i && (nlIdx < 0 || endTick < nlIdx))
                {
                    var inner = markdown.Substring(i + 1, endTick - i - 1);
                    if (TryResolvePath(inner.Trim(), projectRoot, out var abs))
                    {
                        sb.Append('[').Append('`').Append(inner).Append('`').Append("](")
                            .Append(ToFileLinkUri(abs)).Append(')');
                        i = endTick + 1;
                        continue;
                    }
                    sb.Append(markdown, i, endTick - i + 1);
                    i = endTick + 1;
                    continue;
                }
            }

            // Try to match a path starting here. Previous char must be a non-path boundary.
            if (i == 0 || IsPathBoundary(markdown[i - 1]))
            {
                var match = PathCandidateRegex().Match(markdown, i);
                if (match.Success && match.Index == i)
                {
                    var text = match.Value;
                    var next = i + text.Length;
                    if (next >= markdown.Length || IsPathBoundary(markdown[next]))
                    {
                        if (TryResolvePath(text, projectRoot, out var abs))
                        {
                            sb.Append('[').Append(EscapeLinkText(text)).Append("](")
                                .Append(ToFileLinkUri(abs)).Append(')');
                            i = next;
                            continue;
                        }
                    }
                }
            }

            sb.Append(markdown[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Walks <paramref name="doc"/> for hyperlinks with the <c>agentdock-file://</c>
    /// scheme and wires their navigation to <paramref name="onClick"/>, passing the
    /// decoded absolute path.
    /// </summary>
    public static void WireFileLinks(FlowDocument? doc, Action<string> onClick)
    {
        if (doc == null) return;
        foreach (var link in EnumerateHyperlinks(doc))
        {
            if (link.NavigateUri == null) continue;
            var abs = FromFileLinkUri(link.NavigateUri.OriginalString);
            if (abs == null) continue;
            link.Cursor = Cursors.Hand;
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                onClick(abs);
            };
        }
    }

    private static bool IsFenceAt(string s, int i)
        => i + 2 < s.Length && s[i] == '`' && s[i + 1] == '`' && s[i + 2] == '`';

    private static bool IsPathBoundary(char c)
        => !(char.IsLetterOrDigit(c) || c == '_' || c == '/' || c == '\\');

    private static string EscapeLinkText(string text)
        => text.Replace("\\", "\\\\").Replace("]", "\\]");

    private static string ToFileLinkUri(string absolutePath)
        => FileLinkPrefix + Uri.EscapeDataString(absolutePath);

    private static string? FromFileLinkUri(string uriString)
    {
        if (!uriString.StartsWith(FileLinkPrefix, StringComparison.Ordinal)) return null;
        return Uri.UnescapeDataString(uriString.Substring(FileLinkPrefix.Length));
    }

    private static bool TryResolvePath(string text, string projectRoot, out string absolute)
    {
        absolute = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Strip optional :line ref. Don't strip the colon in a Windows drive prefix.
        var lastColon = text.LastIndexOf(':');
        if (lastColon > 1 && lastColon < text.Length - 1 && AllDigits(text, lastColon + 1))
            text = text.Substring(0, lastColon);

        if (text.IndexOf('/') < 0 && text.IndexOf('\\') < 0) return false;

        string candidate;
        if (Path.IsPathRooted(text))
        {
            candidate = text;
        }
        else
        {
            if (text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith(".\\", StringComparison.Ordinal))
                text = text.Substring(2);
            candidate = Path.Combine(projectRoot, text);
        }

        try
        {
            if (File.Exists(candidate))
            {
                absolute = Path.GetFullPath(candidate);
                return true;
            }
        }
        catch
        {
            // Invalid path characters etc. — treat as no match.
        }
        return false;
    }

    private static bool AllDigits(string s, int start)
    {
        for (var k = start; k < s.Length; k++)
            if (!char.IsDigit(s[k])) return false;
        return start < s.Length;
    }

    private static IEnumerable<Hyperlink> EnumerateHyperlinks(FlowDocument doc)
    {
        foreach (var b in doc.Blocks)
            foreach (var h in EnumerateHyperlinks(b))
                yield return h;
    }

    private static IEnumerable<Hyperlink> EnumerateHyperlinks(Block block)
    {
        switch (block)
        {
            case Paragraph p:
                foreach (var inl in p.Inlines)
                    foreach (var h in EnumerateHyperlinksInline(inl))
                        yield return h;
                break;
            case Section s:
                foreach (var b in s.Blocks)
                    foreach (var h in EnumerateHyperlinks(b))
                        yield return h;
                break;
            case List list:
                foreach (var item in list.ListItems)
                    foreach (var b in item.Blocks)
                        foreach (var h in EnumerateHyperlinks(b))
                            yield return h;
                break;
            case Table table:
                foreach (var rg in table.RowGroups)
                    foreach (var row in rg.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var b in cell.Blocks)
                                foreach (var h in EnumerateHyperlinks(b))
                                    yield return h;
                break;
        }
    }

    private static IEnumerable<Hyperlink> EnumerateHyperlinksInline(Inline inline)
    {
        // Hyperlink derives from Span — check it first so we don't recurse into its
        // inner inlines twice.
        if (inline is Hyperlink h)
        {
            yield return h;
            yield break;
        }
        if (inline is Span span)
            foreach (var child in span.Inlines)
                foreach (var hh in EnumerateHyperlinksInline(child))
                    yield return hh;
    }
}
