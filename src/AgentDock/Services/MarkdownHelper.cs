using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

    [GeneratedRegex(@"`[^`\n]+`|\[[^\]\n]+\]\([^)\n]+\)")]
    private static partial Regex InlineAtomRegex();

    // Inline markdown constructs that make an otherwise single-line message worth
    // rendering: bold/italic emphasis, an inline code span, a [text](url) link, or a
    // bare URL. Used to decide whether a newline-free reply needs the markdown renderer
    // (and the source/rendered toggle) rather than being shown verbatim. Patterns are
    // deliberately loose — a false positive just routes a plain sentence through the
    // renderer (which produces identical output) and adds a toggle; a false negative is
    // the actual bug (literal ** shown, no formatting). Emphasis alternatives require
    // non-space, non-delimiter content so bare arithmetic like "a * b" doesn't count.
    [GeneratedRegex(
        @"\*\*[^*\n\s][^*\n]*\*\*"                 // **bold**
        + @"|(?<!\*)\*[^*\n\s][^*\n]*\*(?!\*)"     // *italic*
        + @"|(?<![\w_])__[^_\n\s][^_\n]*__(?![\w_])" // __bold__
        + @"|(?<![\w_])_[^_\n\s][^_\n]*_(?![\w_])" // _italic_
        + @"|`[^`\n]+`"                            // `code`
        + @"|\[[^\]\n]+\]\([^)\n]+\)"              // [text](url)
        + @"|https?://",                           // bare URL
        RegexOptions.IgnoreCase)]
    private static partial Regex InlineMarkdownRegex();

    [GeneratedRegex(@"^(\s*)(.*?)(\s*)$", RegexOptions.Singleline)]
    private static partial Regex TrimWsRegex();

    // Path candidate: optional ./ or .\ prefix, optional drive letter, one or more
    // separator-terminated segments, then a final filename with extension, optional :line ref.
    [GeneratedRegex(@"\G(?:\.[\\/])?(?:[A-Za-z]:[\\/])?(?:[\w.\-]+[\\/])+[\w\-.]+\.[A-Za-z][\w]*(?::\d+)?")]
    private static partial Regex PathCandidateRegex();

    // Bare http/https URL. Stops at whitespace, quotes, brackets, and parens so the
    // generated [url](url) markdown stays well-formed; trailing sentence punctuation
    // is trimmed separately (see TrimUrlTrailing). Asterisks are excluded too: a URL
    // wrapped in bold (**https://x.com**) would otherwise swallow the closing ** into
    // the match, leaving a stray leading ** that renders as literal stars. Asterisks
    // are legal-but-rare in URLs and conventionally percent-encoded, so this is safe.
    [GeneratedRegex(@"\Ghttps?://[^\s<>""'`()\[\]{}|\\^*]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCandidateRegex();

    public const string FileLinkScheme = "agentdock-file";
    private const string FileLinkPrefix = "agentdock-file:///";

    // Matches the code font used by chat/preview code spans (see MarkdownStyles.xaml),
    // applied to file-reference Hyperlinks so linkified paths keep their monospace look.
    private static readonly FontFamily CodeFontFamily = new("Cascadia Code, Consolas, Courier New");

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
    /// Returns true when <paramref name="text"/> should be handed to the markdown
    /// renderer rather than shown verbatim. Multi-line text always qualifies (it may
    /// carry lists, headings, tables, or code fences); single-line text qualifies only
    /// when it contains an inline construct the renderer would change — emphasis, a code
    /// span, a link, or a bare URL. A single-line plain sentence stays plain (no toggle).
    /// </summary>
    public static bool LooksLikeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains('\n') || InlineMarkdownRegex().IsMatch(text);
    }

    /// <summary>
    /// Rewrites <c>**text `code` text**</c> as <c>**text** `code` **text**</c> so that
    /// MdXaml can pair the bold delimiters. MdXaml parses code spans / links before bold,
    /// so a <c>**</c> pair straddling one of those atoms is left orphaned and renders as
    /// literal asterisks; splitting the bold around each atom lets every fragment pair.
    /// Also handles partial-link cases like <c>**See [foo](url) more**</c>. Trims
    /// whitespace at split boundaries to satisfy CommonMark flanking rules.
    ///
    /// Delimiters are paired left-to-right and non-overlapping — a <c>**</c> is matched
    /// to the very next <c>**</c> on the same line, then scanning resumes past it. A pair
    /// with no atom inside is emitted unchanged (MdXaml already renders plain bold), which
    /// is what keeps a line like <c>**a**: `x` and **b**: `y`</c> intact: the closing
    /// <c>**</c> of <c>a</c> can never bind to the opening <c>**</c> of <c>b</c> and wrap
    /// the code span between two separate bolds. An unpaired <c>**</c> (next <c>**</c> is
    /// on a later line, or absent) is emitted literally and scanning continues past it.
    /// </summary>
    private static string FixBoldAcrossInlineAtoms(string markdown)
    {
        var sb = new StringBuilder(markdown.Length);
        var i = 0;
        while (i < markdown.Length)
        {
            var open = markdown.IndexOf("**", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(markdown, i, markdown.Length - i);
                break;
            }

            var close = markdown.IndexOf("**", open + 2, StringComparison.Ordinal);
            var newline = markdown.IndexOf('\n', open + 2);
            if (close < 0 || (newline >= 0 && newline < close))
            {
                // Unpaired on this line — emit the ** literally and keep scanning.
                sb.Append(markdown, i, open + 2 - i);
                i = open + 2;
                continue;
            }

            sb.Append(markdown, i, open - i);
            var inner = markdown.Substring(open + 2, close - (open + 2));
            if (InlineAtomRegex().IsMatch(inner))
                sb.Append(SplitBoldAroundAtoms(inner));
            else
                sb.Append("**").Append(inner).Append("**");
            i = close + 2;
        }
        return sb.ToString();
    }

    private static string SplitBoldAroundAtoms(string inner)
    {
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
        ConvertTablesToGrids(viewer.Document);
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
        // Instrumentation: this runs synchronously (MdXaml parse + an AvalonEdit
        // TextEditor per code block + table→Grid conversion) on the UI thread at
        // every turn finalize, for every session including background tabs — a
        // prime source of multi-hundred-ms UI freezes. Time it and count it.
        PerfDiagnostics.MarkdownBuildDelta(1);
        using var _ = PerfDiagnostics.Time("MarkdownHelper.BuildDocument", thresholdMs: 80);
        try
        {
            var linkified = LinkifyPaths(markdown, projectPath);
            var preProcessed = PreProcess(linkified);

            var tmp = new MarkdownScrollViewer { MarkdownStyle = markdownStyle };
            tmp.Markdown = preProcessed;
            var doc = tmp.Document ?? new FlowDocument();

            ApplyCodeBlockTheme(doc);
            WireLinks(doc, onFileLinkClicked);

            // Replace FlowDocument tables with WPF Grids — see ConvertTablesToGrids.
            // Runs AFTER WireLinks so any Hyperlinks inside table cells keep their
            // wired click handlers when moved into the per-cell RichTextBox.
            ConvertTablesToGrids(doc);

            // Detach from the temp viewer so the document can be re-parented to a
            // real viewer when the chat container materializes.
            tmp.Document = null;

            return doc;
        }
        finally
        {
            PerfDiagnostics.MarkdownBuildDelta(-1);
        }
    }

    /// <summary>
    /// Builds a minimal, render-safe <see cref="FlowDocument"/> containing <paramref name="text"/>
    /// verbatim as a single plain-text paragraph. Used as a fallback when
    /// <see cref="BuildDocument"/> throws, so a markdown-render bug degrades to readable
    /// (if unformatted) text instead of re-throwing on every realize. Touches no PTS table
    /// code and re-parents no inlines — the two paths that have caused render failures.
    /// </summary>
    public static FlowDocument BuildPlainTextFallback(string text, System.Windows.Style? markdownStyle)
    {
        var doc = new FlowDocument();
        if (markdownStyle != null) doc.Style = markdownStyle;
        doc.Blocks.Add(new Paragraph(new Run(text)));
        return doc;
    }

    /// <summary>
    /// Replaces every FlowDocument <see cref="Table"/> in <paramref name="doc"/> with a
    /// <see cref="BlockUIContainer"/> hosting an equivalent WPF <see cref="Grid"/>.
    ///
    /// WHY: WPF's PTS layout engine crashes the entire process via
    /// <c>Invariant.FailFast</c> inside <c>TableParaClient.UpdateChunkInfo</c> when it
    /// arranges certain FlowDocument tables (confirmed root cause of the long-standing
    /// silent crash — a FailFast bypasses every managed exception handler). MdXaml renders
    /// GitHub-flavored markdown tables as FlowDocument <see cref="Table"/>s, so any chat
    /// reply or previewed file containing a table could hit it. A <see cref="Grid"/> inside
    /// a <see cref="BlockUIContainer"/> lays out through the normal WPF layout system (the
    /// same path the AvalonEdit code blocks already use) and never touches PTS table code.
    /// </summary>
    public static void ConvertTablesToGrids(FlowDocument? doc)
    {
        if (doc == null) return;
        ConvertTablesInBlocks(doc.Blocks, doc.Foreground);
    }

    private static void ConvertTablesInBlocks(BlockCollection blocks, Brush foreground)
    {
        // Snapshot first: we mutate the collection (insert/remove) while walking it.
        foreach (var block in blocks.ToList())
        {
            switch (block)
            {
                case Table table:
                    var container = new BlockUIContainer(BuildTableGrid(table, foreground));
                    blocks.InsertAfter(table, container);
                    blocks.Remove(table);
                    break;
                case Section section:
                    ConvertTablesInBlocks(section.Blocks, foreground);
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                        ConvertTablesInBlocks(item.Blocks, foreground);
                    break;
            }
        }
    }

    private static Grid BuildTableGrid(Table table, Brush foreground)
    {
        var rows = table.RowGroups
            .SelectMany(rg => rg.Rows.Select(row => (row, isHeader: (rg.Tag as string) == "TableHeader")))
            .ToList();

        var columnCount = table.Columns.Count;
        if (columnCount == 0)
            columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.row.Cells.Sum(c => c.ColumnSpan));

        var grid = new Grid { Margin = new Thickness(0, 7, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
        // Foreground inherits to every child TextBlock through the WPF element tree
        // (TextElement.Foreground), so cell text matches the document's theme color.
        grid.SetValue(TextElement.ForegroundProperty, foreground);

        for (var c = 0; c < columnCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Logical text grid, captured in parallel with the visual cells so the table
        // can be copied to the clipboard (TSV + HTML) — a BlockUIContainer's Grid is a
        // UI island in the FlowDocument and contributes no selectable/copyable text, so
        // a right-click "Copy table" is the only way to get this data into Excel etc.
        var textGrid = new string[rows.Count][];
        for (var r = 0; r < rows.Count; r++)
        {
            textGrid[r] = new string[columnCount];
            Array.Fill(textGrid[r], "");
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var (row, isHeader) = rows[r];
            var isEvenRow = (row.Tag as string) == "EvenTableRow";
            var col = 0;
            foreach (var cell in row.Cells)
            {
                if (col >= columnCount) break;
                // Read the cell's plain text before BuildCellBorder, which moves (and
                // clears) the cell's inlines into a TextBlock.
                if (col < columnCount) textGrid[r][col] = GetCellPlainText(cell);
                var border = BuildCellBorder(cell, isHeader, isEvenRow, textGrid, foreground);
                Grid.SetRow(border, r);
                Grid.SetColumn(border, col);
                var span = Math.Max(1, cell.ColumnSpan);
                if (span > 1) Grid.SetColumnSpan(border, Math.Min(span, columnCount - col));
                grid.Children.Add(border);
                col += span;
            }
        }

        grid.ContextMenu = BuildTableContextMenu(textGrid);
        return grid;
    }

    // Right-click menu for a rendered table: copies the whole table to the clipboard.
    private static ContextMenu BuildTableContextMenu(string[][] textGrid)
    {
        var menu = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copy table" };
        copyItem.Click += (_, _) => CopyTableToClipboard(textGrid);
        menu.Items.Add(copyItem);
        return menu;
    }

    // Concatenates a cell's block plain text, joining multiple blocks with a space so
    // the result stays on one logical line (newlines inside a cell would break the
    // TSV row/column grid Excel reconstructs).
    private static string GetCellPlainText(TableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var block in cell.Blocks)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(GetBlockPlainText(block));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Places a rendered table on the clipboard as tab-separated text (so Excel /
    /// Sheets split it into columns on paste) and as an HTML table (so rich targets
    /// like Word keep the grid). Tabs / newlines inside a cell are collapsed to spaces
    /// so the column alignment survives.
    /// </summary>
    private static void CopyTableToClipboard(string[][] textGrid)
    {
        var tsv = new StringBuilder();
        foreach (var row in textGrid)
        {
            for (var c = 0; c < row.Length; c++)
            {
                if (c > 0) tsv.Append('\t');
                tsv.Append(CleanTsvCell(row[c]));
            }
            tsv.Append("\r\n");
        }

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, tsv.ToString());
        data.SetData(DataFormats.Text, tsv.ToString());
        data.SetData(DataFormats.Html, BuildHtmlClipboard(textGrid));

        try
        {
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex)
        {
            // The clipboard can be transiently locked by another process; a failed copy
            // shouldn't take down the chat.
            Log.Error("Failed to copy table to clipboard", ex);
        }
    }

    // Collapses the characters that define TSV structure (tab / CR / LF) to spaces so a
    // multi-line or tab-containing cell can't shift the grid Excel parses.
    private static string CleanTsvCell(string cell)
        => cell.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    // Builds a CF_HTML clipboard payload (the byte-offset header WPF/Windows require)
    // wrapping an HTML <table> of the cell text.
    private static string BuildHtmlClipboard(string[][] textGrid)
    {
        var body = new StringBuilder();
        body.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">");
        foreach (var row in textGrid)
        {
            body.Append("<tr>");
            foreach (var cell in row)
                body.Append("<td>").Append(System.Net.WebUtility.HtmlEncode(cell)).Append("</td>");
            body.Append("</tr>");
        }
        body.Append("</table>");
        return WrapCfHtml(body.ToString());
    }

    // Wraps an HTML fragment in the CF_HTML format: a header declaring UTF-8 byte
    // offsets of the document and fragment, followed by the markup. Offsets are
    // computed in bytes (UTF-8) per the spec; the header uses fixed-width 8-digit
    // fields so its own length is constant and doesn't perturb the offsets.
    private static string WrapCfHtml(string fragment)
    {
        const string header =
            "Version:0.9\r\nStartHTML:{0:00000000}\r\nEndHTML:{1:00000000}\r\n"
            + "StartFragment:{2:00000000}\r\nEndFragment:{3:00000000}\r\n";
        const string preFragment = "<html><body>\r\n<!--StartFragment-->";
        const string postFragment = "<!--EndFragment-->\r\n</body></html>";

        var utf8 = Encoding.UTF8;
        var headerLen = utf8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        var startHtml = headerLen;
        var startFragment = startHtml + utf8.GetByteCount(preFragment);
        var endFragment = startFragment + utf8.GetByteCount(fragment);
        var endHtml = endFragment + utf8.GetByteCount(postFragment);

        return string.Format(header, startHtml, endHtml, startFragment, endFragment)
            + preFragment + fragment + postFragment;
    }

    private static Border BuildCellBorder(TableCell cell, bool isHeader, bool isEvenRow, string[][] textGrid, Brush foreground)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(13, 6, 13, 6),
            Child = BuildCellContent(cell, isHeader, textGrid, foreground),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "MarkdownTableBorderBrush");
        if (isHeader)
            border.SetResourceReference(Border.BackgroundProperty, "MarkdownTableHeaderBackground");
        else if (isEvenRow)
            border.SetResourceReference(Border.BackgroundProperty, "MarkdownTableEvenRowBackground");

        return border;
    }

    /// <summary>
    /// Builds the selectable content for one table cell. A read-only, chrome-less
    /// <see cref="RichTextBox"/> is used rather than a <see cref="TextBlock"/> so cell
    /// text can be highlighted and copied with the mouse — a TextBlock renders text but
    /// exposes no selection, which is why table text was previously uncopyable. Read-only
    /// keeps wired Hyperlinks single-click clickable while still allowing selection, and
    /// re-parenting the cell's inlines (rather than flattening to plain text) preserves
    /// bold/italic/code formatting. The whole grid is still a UI island in the outer
    /// FlowDocument (the document-wide selection can't reach into it), so the cell's
    /// context menu also offers "Copy table" for a whole-table copy.
    /// </summary>
    private static RichTextBox BuildCellContent(TableCell cell, bool isHeader, string[][] textGrid, Brush foreground)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        if (isHeader) paragraph.FontWeight = FontWeights.Bold;
        PopulateCellParagraph(cell, paragraph);

        return new RichTextBox
        {
            Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) },
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            // Set the theme foreground explicitly rather than relying on the grid's
            // inherited TextElement.Foreground: the default RichTextBox style carries a
            // Foreground setter (system text brush, ~black), and a style-setter value
            // outranks inheritance — so without this the cell text renders black-on-dark
            // and is unreadable in every dark theme. A local value beats the style setter.
            Foreground = foreground,
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            IsTabStop = false,
            IsReadOnlyCaretVisible = false,
            // Vertical hidden + horizontal disabled: the box grows to fit its content and
            // wraps long text to the column width instead of scrolling within the cell.
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ContextMenu = BuildCellContextMenu(textGrid),
        };
    }

    // Right-click menu for a table cell: "Copy" (the current selection, via the standard
    // editing command routed to the focused RichTextBox) and "Copy table" (the whole grid).
    private static ContextMenu BuildCellContextMenu(string[][] textGrid)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy });
        var copyTable = new MenuItem { Header = "Copy table" };
        copyTable.Click += (_, _) => CopyTableToClipboard(textGrid);
        menu.Items.Add(copyTable);
        return menu;
    }

    /// <summary>
    /// Moves the inline content of a table cell's paragraphs into <paramref name="target"/>.
    /// Inlines are re-parented (cleared from their paragraph first) so existing formatting
    /// and wired hyperlink handlers survive the move. Non-paragraph blocks are flattened to
    /// plain text — GFM cells are inline-only, so that path is a defensive fallback.
    /// </summary>
    private static void PopulateCellParagraph(TableCell cell, Paragraph target)
    {
        var first = true;
        // Snapshot first: moving a paragraph's inlines into the target mutates the source
        // FlowDocument's shared TextContainer (p.Inlines.Clear below), which bumps the
        // generation that a live cell.Blocks enumerator validates against — otherwise the
        // next iteration throws "Collection was modified". Same reason p.Inlines is ToList'd.
        foreach (var block in cell.Blocks.ToList())
        {
            if (!first) target.Inlines.Add(new LineBreak());
            first = false;

            if (block is Paragraph p)
            {
                var inlines = p.Inlines.ToList();
                p.Inlines.Clear();
                foreach (var inl in inlines)
                {
                    if (inl is Hyperlink h)
                    {
                        // The cell's new FlowDocument carries none of the outer document's
                        // implicit styles, so apply the link look explicitly.
                        h.TextDecorations = null;
                        h.SetResourceReference(Hyperlink.ForegroundProperty, "MarkdownLinkForeground");
                    }
                    target.Inlines.Add(inl);
                }
            }
            else
            {
                target.Inlines.Add(new Run(GetBlockPlainText(block)));
            }
        }
    }

    private static string GetBlockPlainText(Block block)
    {
        var sb = new StringBuilder();
        switch (block)
        {
            case Paragraph p:
                AppendInlineText(p.Inlines, sb);
                break;
            case Section s:
                foreach (var b in s.Blocks) sb.Append(GetBlockPlainText(b));
                break;
            case List list:
                foreach (var item in list.ListItems)
                    foreach (var b in item.Blocks) sb.Append(GetBlockPlainText(b));
                break;
        }
        return sb.ToString();
    }

    private static void AppendInlineText(InlineCollection inlines, StringBuilder sb)
    {
        foreach (var inl in inlines)
        {
            switch (inl)
            {
                case Run run: sb.Append(run.Text); break;
                case Span span: AppendInlineText(span.Inlines, sb); break;
                case LineBreak: sb.Append(' '); break;
            }
        }
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
    /// Rewrites references in <paramref name="markdown"/> as markdown links.
    /// File-path references that resolve to an existing file (under
    /// <paramref name="projectRoot"/> or absolute) become <c>agentdock-file://</c>
    /// links; bare <c>http(s)</c> URLs become ordinary web links. Path linking is
    /// skipped when <paramref name="projectRoot"/> is empty; URL linking always runs.
    /// Skips fenced code blocks, inline code, and existing markdown links.
    /// </summary>
    public static string LinkifyPaths(string markdown, string projectRoot)
    {
        if (string.IsNullOrEmpty(markdown))
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
                        // Emit a plain link, NOT [`inner`](url). MdXaml runs its code-span
                        // parser before its anchor parser, so a code span inside a link
                        // label is consumed first and the surrounding []() renders as
                        // literal text. We drop the backticks here and restore the
                        // monospace look on the resulting Hyperlink in WireLinks instead.
                        sb.Append('[').Append(EscapeLinkText(inner)).Append("](")
                            .Append(ToFileLinkUri(abs)).Append(')');
                        i = endTick + 1;
                        continue;
                    }
                    sb.Append(markdown, i, endTick - i + 1);
                    i = endTick + 1;
                    continue;
                }
            }

            // Bare http(s) URL at a boundary — wrap as [url](url) so MdXaml renders a
            // real Hyperlink that WireLinks opens in the browser. [text](url) links are
            // already skipped above, so this only catches raw URLs.
            if ((i == 0 || IsPathBoundary(markdown[i - 1]))
                && (markdown[i] == 'h' || markdown[i] == 'H'))
            {
                var urlMatch = UrlCandidateRegex().Match(markdown, i);
                if (urlMatch.Success && urlMatch.Index == i)
                {
                    var url = TrimUrlTrailing(urlMatch.Value);
                    if (url.Length > 0)
                    {
                        sb.Append('[').Append(EscapeLinkText(url)).Append("](").Append(url).Append(')');
                        i += url.Length;
                        continue;
                    }
                }
            }

            // Try to match a path starting here. Previous char must be a non-path boundary.
            if (!string.IsNullOrEmpty(projectRoot) && (i == 0 || IsPathBoundary(markdown[i - 1])))
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
    /// Walks <paramref name="doc"/> and wires every hyperlink for clicking:
    /// <c>agentdock-file://</c> links invoke <paramref name="onFileClick"/> with the
    /// decoded absolute path (revealing the file in the preview); <c>http</c>/<c>https</c>/
    /// <c>mailto</c> links open in the system browser. File links are wired only when
    /// <paramref name="onFileClick"/> is supplied.
    /// </summary>
    public static void WireLinks(FlowDocument? doc, Action<string>? onFileClick)
    {
        if (doc == null) return;
        foreach (var link in EnumerateHyperlinks(doc))
        {
            // MdXaml does NOT set NavigateUri on the hyperlinks it generates. It stores the
            // target in CommandParameter and sets Command = NavigationCommands.GoToPage. We
            // host the built document by assigning FlowDocumentScrollViewer.Document directly
            // (not MarkdownScrollViewer.Markdown), so MdXaml never installs a CommandBinding
            // for GoToPage — that command's CanExecute is therefore false, which DISABLES the
            // Hyperlink. It renders as a link but swallows every click. Recover the URL, drop
            // the dead command to re-enable the link, and drive navigation from Click (which,
            // unlike RequestNavigate, doesn't depend on NavigateUri being set).
            var url = link.CommandParameter as string ?? link.NavigateUri?.OriginalString;
            if (string.IsNullOrEmpty(url)) continue;

            link.Command = null;
            link.CommandParameter = null;

            var abs = FromFileLinkUri(url);
            if (abs != null)
            {
                if (onFileClick == null) continue;
                // These labels were stripped of their backtick code span in LinkifyPaths
                // (MdXaml can't render a code span inside a link), so restore the
                // monospace look here — file references read as code.
                link.FontFamily = CodeFontFamily;
                link.Cursor = Cursors.Hand;
                link.Click += (_, _) => onFileClick(abs);
                continue;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp
                    || uri.Scheme == Uri.UriSchemeHttps
                    || uri.Scheme == Uri.UriSchemeMailto))
            {
                var target = uri.AbsoluteUri;
                link.Cursor = Cursors.Hand;
                link.Click += (_, _) => OpenInBrowser(target);
            }
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open URL in browser: {url}", ex);
        }
    }

    // Trims sentence punctuation that commonly trails a URL in prose (e.g. the period
    // ending a sentence). Quotes, brackets, and parens are already excluded by the URL
    // regex, so only these need stripping.
    private static string TrimUrlTrailing(string url)
    {
        var end = url.Length;
        while (end > 0 && ".,;:!?".IndexOf(url[end - 1]) >= 0)
            end--;
        return url.Substring(0, end);
    }

    private static bool IsFenceAt(string s, int i)
        => i + 2 < s.Length && s[i] == '`' && s[i + 1] == '`' && s[i + 2] == '`';

    private static bool IsPathBoundary(char c)
        => !(char.IsLetterOrDigit(c) || c == '_' || c == '/' || c == '\\');

    // Backslash-escapes the markdown-active characters that would otherwise be
    // re-parsed inside a link label: the structural \ and ], plus the emphasis
    // delimiters _ * ~. Without this, a path like permission_groups italicizes
    // ("permission" .. "groups" with the underscores eaten) and a *-containing
    // segment turns bold. MdXaml's text handler honors backslash escapes for
    // exactly this set (see DoTextDecorations), so the displayed text stays literal.
    private static string EscapeLinkText(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or ']' or '_' or '*' or '~')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

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
