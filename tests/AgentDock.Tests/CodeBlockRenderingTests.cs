using System.Windows.Documents;
using System.Windows.Media;
using AgentDock.Models;
using AgentDock.Services;
using AgentDock.Services.Abstractions;
using HL.Interfaces;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Fenced code blocks used to render as a <see cref="BlockUIContainer"/> hosting an AvalonEdit
/// <see cref="TextEditor"/>, which caused three separate defects at once: consecutive fences could
/// not be selected and copied together (a BlockUIContainer is a UI island the document's selection
/// cannot enter), each fence grew its own horizontal scrollbar instead of wrapping, and URLs inside
/// them were auto-linkified by AvalonEdit in its hardcoded <c>Brushes.Blue</c> — near-black against
/// a dark theme.
///
/// These tests pin the fix at the level that actually matters to the user: the code text has to be
/// part of the document's own text flow (which is what makes a cross-block selection possible at
/// all), and the links have to carry a resolvable target and a theme-driven brush.
///
/// The renderer builds WPF controls, so everything runs on an STA thread.
/// </summary>
public class CodeBlockRenderingTests
{
    // The shape that surfaced the bug: consecutive fences, each holding one long URL.
    private const string HealthMarkdown = """
        ## Health

        ```
        https://example.azurewebsites.net/api/health
        ```
        ```
        https://example.azurewebsites.net/api/health/ready?code=AbC123==
        ```
        ```
        https://example.azurewebsites.net/api/health/cache?code=AbC123==
        ```
        """;

    [Fact]
    public void CodeBlocks_LeaveNoUiIslands_InTheDocument()
    {
        var doc = RunSta(() => Build(HealthMarkdown));

        // A leftover BlockUIContainer/TextEditor is exactly what broke selection and added the
        // per-block scrollbar, so assert on the absence of the control rather than on appearance.
        Assert.Empty(Descendants(doc).OfType<BlockUIContainer>());
    }

    [Fact]
    public void AllCodeBlocks_AreSelectableAsOneRange()
    {
        // The core regression guard. Under the old rendering the code text lived inside embedded
        // editors and did not appear in the document's own TextRange at all — the same reason a
        // selection could not span from one fence to the next. Reaching every URL through a single
        // ContentStart..ContentEnd range is what makes the drag-across-all-three copy possible.
        var text = RunSta(() =>
        {
            var doc = Build(HealthMarkdown);
            return new TextRange(doc.ContentStart, doc.ContentEnd).Text;
        });

        Assert.Contains("https://example.azurewebsites.net/api/health", text);
        Assert.Contains("https://example.azurewebsites.net/api/health/ready?code=AbC123==", text);
        Assert.Contains("https://example.azurewebsites.net/api/health/cache?code=AbC123==", text);
    }

    [Fact]
    public void UrlsInCodeBlocks_BecomeHyperlinksCarryingTheirTarget()
    {
        var targets = RunSta(() =>
            Descendants(Build(HealthMarkdown))
                .SelectMany(FlattenInlines)
                .OfType<Hyperlink>()
                .Select(h => h.CommandParameter as string)
                .ToList());

        // CommandParameter is where the copy path (GetLinkTarget) looks for a link's target, so a
        // link that renders but carries nothing there would silently lose its URL when copied.
        Assert.Contains("https://example.azurewebsites.net/api/health/ready?code=AbC123==", targets);
        Assert.Contains("https://example.azurewebsites.net/api/health/cache?code=AbC123==", targets);
    }

    [Fact]
    public void CodeBlockLinks_UseTheThemeBrush_NotAvalonEditsHardcodedBlue()
    {
        var foregrounds = RunSta(() =>
            Descendants(Build(HealthMarkdown))
                .SelectMany(FlattenInlines)
                .OfType<Hyperlink>()
                .Select(h => h.Foreground)
                .ToList());

        Assert.NotEmpty(foregrounds);

        // The defect was AvalonEdit's own default, a hardcoded pure blue that no theme could move.
        foreach (var brush in foregrounds)
            Assert.NotEqual(Colors.Blue, (brush as SolidColorBrush)?.Color);
    }

    [Fact]
    public void PlainCodeBlocks_KeepTheirTextVerbatim()
    {
        // Guards the newline handling: a Run renders embedded newlines as nothing, so multi-line
        // code has to become explicit LineBreaks or the block collapses to one line.
        const string markdown = """
            ```
            first line
                indented line
            last line
            ```
            """;

        var text = RunSta(() =>
        {
            var doc = Build(markdown);
            return new TextRange(doc.ContentStart, doc.ContentEnd).Text;
        });

        Assert.Contains("first line", text);
        Assert.Contains("    indented line", text);
        Assert.Contains("last line", text);
    }

    // ---------------------------------------------------------------- helpers

    private static System.Windows.Documents.FlowDocument Build(string markdown)
        => new MarkdownRenderer(new NullLog(), new NullPerf(), new StubTheme())
            .BuildDocument(markdown, markdownStyle: null, projectPath: "", onFileLinkClicked: null);

    /// <summary>Every block in the document, descending through sections and lists.</summary>
    private static IEnumerable<Block> Descendants(System.Windows.Documents.FlowDocument doc)
        => doc.Blocks.SelectMany(Descendants);

    private static IEnumerable<Block> Descendants(Block block)
    {
        yield return block;

        switch (block)
        {
            case Section section:
                foreach (var b in section.Blocks.SelectMany(Descendants)) yield return b;
                break;
            case List list:
                foreach (var b in list.ListItems.SelectMany(i => i.Blocks).SelectMany(Descendants))
                    yield return b;
                break;
        }
    }

    private static IEnumerable<Inline> FlattenInlines(Block block)
        => block is Paragraph paragraph
            ? paragraph.Inlines.SelectMany(Flatten)
            : [];

    private static IEnumerable<Inline> Flatten(Inline inline)
    {
        yield return inline;

        // Hyperlink derives from Span; yielding it above and recursing here is harmless because
        // the tests only ever filter for the Hyperlink itself.
        if (inline is Span span)
            foreach (var child in span.Inlines.SelectMany(Flatten))
                yield return child;
    }

    /// <summary>
    /// Runs <paramref name="func"/> on an STA thread. WPF control construction (MdXaml
    /// instantiates a MarkdownScrollViewer to drive its parser) requires one; the default xunit
    /// test thread is MTA.
    /// </summary>
    private static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");

        return result;
    }

    // ---------------------------------------------------------------- test doubles

    private sealed class StubTheme : IThemeService
    {
        public ThemeDescriptor CurrentTheme => null!;
        public ThemeBaseVariant BaseVariant => default;
        public IThemedHighlightingManager HighlightingManager => null!;
        public event Action<ThemeDescriptor>? ThemeChanged { add { } remove { } }
        public void Initialize() { }
        public void ApplyTheme(ThemeDescriptor theme, bool raiseEvent = true) { }
        public void ApplyTheme(string themeId) { }

        // A distinctly non-blue placeholder, so the "not AvalonEdit's blue" assertion is
        // meaningful rather than accidentally passing on a default.
        public SolidColorBrush GetBrush(string key) => new(Color.FromRgb(0x79, 0xC0, 0xFF));

        public IHighlightingDefinition? GetHighlighting(string extension) => null;
    }

    private sealed class NullLog : ILogService
    {
        public string? LogFilePath => null;
        public void Init(string? logsFolder = null, string? sessionContext = null) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception ex) { }
    }

    private sealed class NullPerf : IPerfDiagnostics
    {
        public bool Enabled { get; set; }
        public int LiveSessions => 0;
        public int WorkingSessions => 0;
        public void SessionCreated() { }
        public void SessionDisposed() { }
        public void WorkingSessionDelta(int delta) { }
        public void NoteWorkspaceDirty() { }
        public void MarkdownBuildDelta(int delta) { }
        public void GitOpStart() { }
        public void GitOpEnd(string command, double ms, int threadId) { }
        public void Start() { }
        public void Stop() { }
        public void LogEnvironment() { }
        public void LogHealth() { }
        public IDisposable Time(string name, double thresholdMs = 50) => new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
